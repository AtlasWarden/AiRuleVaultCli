using System.Text.Json;
using System.Text.RegularExpressions;
using RuleVault.Agents;
using RuleVault.Core;
using RuleVault.Storage;

namespace RuleVault.Cli;

internal sealed record VaultInspection(
    string VaultId,
    string VaultRoot,
    string IndexCanonicalSha256,
    int IndexCharacters,
    bool RouteCatalogPresent,
    string ContentIntegrityCanonicalSha256);

internal sealed record VaultIdentityInspection(string VaultId, string ContentIntegrityCanonicalSha256);
internal sealed record VaultContextFile(string RelativePath, string Content, string RawSha256, bool Mandatory);
internal sealed record VaultTaskContext(ContextPacket Packet, IReadOnlyList<VaultContextFile> Files);

internal static class VaultInspector
{
    private sealed record VerifiedVaultFile(string FileId, string Content, string RawSha256, string CanonicalSha256);
    private sealed record InspectionSnapshot(VaultInspection Inspection, IReadOnlyDictionary<string, VerifiedVaultFile> VerifiedContent);

    public static async Task<VaultIdentityInspection> InspectIdentityAsync(string vaultRoot, CancellationToken cancellationToken = default)
    {
        var metadataBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, ".vault-system/vault.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var integrityBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, ".vault-system/content-integrity.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        return ParseIdentity(metadataBytes, integrityBytes);
    }

    public static async Task<VaultInspection> InspectAsync(string vaultRoot, CancellationToken cancellationToken = default) =>
        (await InspectSnapshotAsync(vaultRoot, cancellationToken)).Inspection;

    private static async Task<InspectionSnapshot> InspectSnapshotAsync(string vaultRoot, CancellationToken cancellationToken)
    {
        var metadataBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, ".vault-system/vault.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var integrityBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, ".vault-system/content-integrity.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var identity = ParseIdentity(metadataBytes, integrityBytes);

        using var integrityDocument = StrictJson.Parse(integrityBytes);
        if (!integrityDocument.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new StorageFormatException("INTEGRITY_MANIFEST_INVALID", "Content integrity manifest has no files array.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var verifiedContent = new Dictionary<string, VerifiedVaultFile>(StringComparer.Ordinal);
        foreach (var entry in files.EnumerateArray())
        {
            if (!entry.TryGetProperty("file_id", out var entryFileId) || entryFileId.ValueKind != JsonValueKind.String || !Guid.TryParse(entryFileId.GetString(), out _) ||
                !entry.TryGetProperty("path", out var entryPath) || entryPath.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("sha256", out var expectedHash) || expectedHash.ValueKind != JsonValueKind.String ||
                !paths.Add(entryPath.GetString()!))
            {
                throw new StorageFormatException("INTEGRITY_MANIFEST_INVALID", "Content integrity manifest has an invalid or duplicate entry.");
            }

            var contentBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, entryPath.GetString()!, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            var content = CanonicalHash.DecodeUtf8(contentBytes);
            var canonicalHash = CanonicalHash.Text(content);
            if (!string.Equals(canonicalHash, expectedHash.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new StorageFormatException("INTEGRITY_HASH_MISMATCH", $"Protected content does not match manifest: '{entryPath.GetString()}'.");
            }
            verifiedContent.Add(entryPath.GetString()!, new VerifiedVaultFile(entryFileId.GetString()!, content, CanonicalHash.Raw(contentBytes), canonicalHash));
        }
        var index = verifiedContent.TryGetValue("index.md", out var protectedIndex)
            ? protectedIndex.Content
            : await TrustedFileSystem.ReadTextAsync(vaultRoot, "index.md", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var routes = SafePath.ValidateRelative(vaultRoot, ".vault-system/routes.json", SafePathProfile.PrivateConfig);
        var inspection = new VaultInspection(
            identity.VaultId,
            Path.GetFullPath(vaultRoot),
            CanonicalHash.Text(index),
            index.Length,
            routes.Supported && File.Exists(routes.FullPath!),
            identity.ContentIntegrityCanonicalSha256);
        return new InspectionSnapshot(inspection, verifiedContent);
    }

    private static VaultIdentityInspection ParseIdentity(ReadOnlySpan<byte> metadataBytes, ReadOnlySpan<byte> integrityBytes)
    {
        using var metadata = StrictJson.Parse(metadataBytes);
        if (!metadata.RootElement.TryGetProperty("vault_id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
        {
            throw new StorageFormatException("VAULT_METADATA_INVALID", "vault.json must contain a non-empty vault_id.");
        }

        using var integrity = StrictJson.Parse(integrityBytes);
        if (!integrity.RootElement.TryGetProperty("vault_id", out var manifestVaultId) ||
            manifestVaultId.ValueKind != JsonValueKind.String ||
            !string.Equals(manifestVaultId.GetString(), id.GetString(), StringComparison.OrdinalIgnoreCase) ||
            !integrity.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new StorageFormatException("INTEGRITY_MANIFEST_INVALID", "Content integrity manifest identity or files array is invalid.");
        }

        return new VaultIdentityInspection(id.GetString()!, CanonicalHash.Text(CanonicalHash.DecodeUtf8(integrityBytes, allowBom: false)));
    }

    public static async Task<VaultTaskContext> BuildTaskPacketAsync(string vaultRoot, TaskDescriptor descriptor, int? maxTotalChars = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await InspectSnapshotAsync(vaultRoot, cancellationToken);
        if (!snapshot.VerifiedContent.TryGetValue(".vault-system/routes.json", out var routeCatalog))
        {
            throw new ContextCatalogException("ROUTE_CATALOG_UNVERIFIED", "The route catalog is not integrity-protected. Update or repair the vault before starting an agent session.");
        }

        using var routeDocument = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(routeCatalog.Content));
        var routes = ParseRoutes(routeDocument.RootElement);
        var documents = new List<ContextDocument>();
        var contextFiles = new Dictionary<string, VaultContextFile>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (!snapshot.VerifiedContent.TryGetValue(route.RelativePath, out var verified))
            {
                throw new ContextCatalogException("ROUTE_CONTENT_UNVERIFIED", $"Route '{route.RouteId}' points to content that is not integrity-protected: '{route.RelativePath}'.");
            }

            var resolved = route with { FileId = verified.FileId };
            documents.Add(new ContextDocument(resolved, verified.Content, verified.CanonicalSha256));
            contextFiles[route.RelativePath] = new VaultContextFile(route.RelativePath, verified.Content, verified.RawSha256, route.LoadPolicy == LoadPolicy.Always);
        }

        await AddProjectContextAsync(vaultRoot, descriptor, snapshot.VerifiedContent, documents, contextFiles, cancellationToken);
        var result = ContextCatalog.Compile(documents).Render(descriptor, maxTotalChars);
        if (result.Packet is null)
        {
            var diagnostic = result.Diagnostics.Count == 0 ? null : result.Diagnostics[0];
            throw new ContextCatalogException(diagnostic?.Code ?? "CONTEXT_BUILD_BLOCKED", diagnostic?.Message ?? "The required task context could not be built.");
        }

        var selected = result.Packet.Segments.Select(segment => segment.RelativePath).ToHashSet(StringComparer.Ordinal);
        var files = contextFiles.Values.Where(file => selected.Contains(file.RelativePath)).OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        return new VaultTaskContext(result.Packet, files);
    }

    private static IReadOnlyList<ContextRoute> ParseRoutes(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => property.Name is not ("schema_version" or "routes")) ||
            !root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
            !root.TryGetProperty("routes", out var routeArray) || routeArray.ValueKind != JsonValueKind.Array)
        {
            throw new ContextCatalogException("ROUTE_CATALOG_INVALID", "The protected route catalog does not match schema version 1.");
        }

        var result = new List<ContextRoute>();
        foreach (var item in routeArray.EnumerateArray())
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "route_id", "path", "kind", "scope", "load_policy", "subjects", "operations", "path_patterns", "optional_priority", "requires", "audience"
            };
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            {
                throw new ContextCatalogException("ROUTE_CATALOG_INVALID", "A route contains unsupported fields.");
            }

            var id = RequiredString(item, "route_id");
            var path = RequiredString(item, "path");
            SafePath.ValidateRelative(Path.GetTempPath(), path, SafePathProfile.PrivateConfig);
            result.Add(new ContextRoute(
                id,
                "pending-manifest-file-id",
                path,
                RequiredString(item, "kind"),
                ParseScope(RequiredString(item, "scope")),
                ParseLoadPolicy(RequiredString(item, "load_policy")),
                RequiredStrings(item, "subjects"),
                RequiredStrings(item, "operations").Select(ParseOperation).ToArray(),
                RequiredStrings(item, "path_patterns"),
                RequiredInt(item, "optional_priority"),
                RequiredStrings(item, "requires"),
                ParseAudience(RequiredString(item, "audience"))));
        }

        return result;
    }

    private static async Task AddProjectContextAsync(
        string vaultRoot,
        TaskDescriptor descriptor,
        IReadOnlyDictionary<string, VerifiedVaultFile> verifiedContent,
        List<ContextDocument> documents,
        Dictionary<string, VaultContextFile> contextFiles,
        CancellationToken cancellationToken)
    {
        if (descriptor.ProjectId == "global")
        {
            return;
        }
        if (!Regex.IsMatch(descriptor.ProjectId, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant))
        {
            throw new ContextCatalogException("PROJECT_SCOPE_INVALID", "Task context requires 'global' or a normalized project slug.");
        }

        var basePath = $"projects/{descriptor.ProjectId}";
        var routerTarget = SafePath.ValidateRelative(vaultRoot, $"{basePath}/index.md", SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(routerTarget);
        if (!File.Exists(routerTarget.FullPath!))
        {
            return;
        }

        var paths = new List<string>
        {
            $"{basePath}/index.md",
            $"{basePath}/rules/index.md",
            $"{basePath}/context/index.md",
            $"{basePath}/context/current-state.md",
            $"{basePath}/daily/index.md"
        };
        if (!verifiedContent.TryGetValue($"{basePath}/daily/index.md", out var dailyIndex))
        {
            throw new ContextCatalogException("MANDATORY_CONTEXT_UNPROTECTED", $"Mandatory project content is not integrity-protected: '{basePath}/daily/index.md'. Update the vault before starting work.");
        }
        var match = Regex.Match(dailyIndex.Content, @"\./active/(?<date>\d{4}-\d{2}-\d{2})\.md", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new ContextCatalogException("DAILY_POINTER_INVALID", "The project daily index does not identify one active ISO-date note.");
        }
        var activePath = $"{basePath}/daily/active/{match.Groups["date"].Value}.md";
        if (!verifiedContent.TryGetValue(activePath, out var activeRead))
        {
            var activeBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, activePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            var activeContent = CanonicalHash.DecodeUtf8(activeBytes);
            var activeMetadata = ManagedDocumentMetadataValidator.Parse(activePath, activeContent, requireCompleteSchema: true);
            activeRead = new VerifiedVaultFile(activeMetadata.FileId, activeContent, CanonicalHash.Raw(activeBytes), CanonicalHash.Text(activeContent));
        }
        paths.Add(activePath);
        foreach (var path in paths)
        {
            var read = path == activePath ? activeRead : verifiedContent.GetValueOrDefault(path);
            if (read is null)
            {
                throw new ContextCatalogException("MANDATORY_CONTEXT_UNPROTECTED", $"Mandatory project content is not integrity-protected: '{path}'. Update the vault before starting work.");
            }
            var metadata = ManagedDocumentMetadataValidator.Parse(path, read.Content, requireCompleteSchema: true);
            var routeId = "project-" + path[basePath.Length..].Trim('/').Replace('/', '-').Replace('.', '-');
            var route = new ContextRoute(routeId, metadata.FileId, path, metadata.Kind, RouteScope.Project, LoadPolicy.Always, [], AllOperations(), [], 100, ["vault-discovery-and-integrity"], ContextAudience.Private);
            documents.Add(new ContextDocument(route, read.Content, CanonicalHash.Text(read.Content)));
            contextFiles[path] = new VaultContextFile(path, read.Content, read.RawSha256, true);
        }

        var fixedPaths = paths.ToHashSet(StringComparer.Ordinal);
        foreach (var pair in verifiedContent.Where(item => item.Key.StartsWith(basePath + "/", StringComparison.Ordinal) && item.Key.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !fixedPaths.Contains(item.Key)))
        {
            var withinProject = pair.Key[(basePath.Length + 1)..];
            var segments = withinProject.Split('/');
            var subject = segments.Length >= 3 && segments[0] is "rules" or "context" ? segments[1] : null;
            var isApplicableSubjectIndex = subject is not null &&
                string.Equals(segments[^1], "index.md", StringComparison.OrdinalIgnoreCase) &&
                descriptor.Subjects.Contains(subject, StringComparer.Ordinal);

            ManagedDocumentMetadata metadata;
            try
            {
                metadata = ManagedDocumentMetadataValidator.Parse(pair.Key, pair.Value.Content, requireCompleteSchema: true);
            }
            catch (VaultAuthoringException) when (!isApplicableSubjectIndex)
            {
                // Invalid non-routing content remains protected data but cannot
                // declare itself mandatory until its metadata is repaired.
                continue;
            }

            if (metadata.LoadPolicy != "always" && !isApplicableSubjectIndex)
            {
                continue;
            }

            var scope = subject is null ? RouteScope.Project : RouteScope.Subject;
            IReadOnlyList<string> routeSubjects = subject is null ? [] : [subject];
            var routeId = "project-managed-" + withinProject.Replace('/', '-').Replace('.', '-');
            var route = new ContextRoute(routeId, metadata.FileId, pair.Key, metadata.Kind, scope, LoadPolicy.Always, routeSubjects, AllOperations(), [], 100, ["vault-discovery-and-integrity"], ContextAudience.Private);
            documents.Add(new ContextDocument(route, pair.Value.Content, pair.Value.CanonicalSha256));
            contextFiles[pair.Key] = new VaultContextFile(pair.Key, pair.Value.Content, pair.Value.RawSha256, true);
        }
    }

    private static IReadOnlyList<TaskOperation> AllOperations() =>
        [TaskOperation.Read, TaskOperation.Edit, TaskOperation.Test, TaskOperation.Review, TaskOperation.Release, TaskOperation.MaintainVault];

    private static string RequiredString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ContextCatalogException("ROUTE_CATALOG_INVALID", $"A route has no valid '{name}'.");

    private static int RequiredInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) && result >= 0
            ? result
            : throw new ContextCatalogException("ROUTE_CATALOG_INVALID", $"A route has no valid '{name}'.");

    private static IReadOnlyList<string> RequiredStrings(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(entry => entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString())))
        {
            throw new ContextCatalogException("ROUTE_CATALOG_INVALID", $"A route has no valid '{name}' array.");
        }
        return value.EnumerateArray().Select(entry => entry.GetString()!).ToArray();
    }

    private static RouteScope ParseScope(string value) => value switch
    {
        "global" => RouteScope.Global,
        "project" => RouteScope.Project,
        "subject" => RouteScope.Subject,
        _ => throw new ContextCatalogException("ROUTE_CATALOG_INVALID", $"Unsupported route scope '{value}'.")
    };

    private static LoadPolicy ParseLoadPolicy(string value) => value switch
    {
        "always" => LoadPolicy.Always,
        "conditional" => LoadPolicy.Conditional,
        "historical" => LoadPolicy.Historical,
        _ => throw new ContextCatalogException("ROUTE_CATALOG_INVALID", $"Unsupported load policy '{value}'.")
    };

    internal static TaskOperation ParseOperation(string value) => value switch
    {
        "read" => TaskOperation.Read,
        "edit" => TaskOperation.Edit,
        "test" => TaskOperation.Test,
        "review" => TaskOperation.Review,
        "release" => TaskOperation.Release,
        "maintain-vault" => TaskOperation.MaintainVault,
        _ => throw new ContextCatalogException("TASK_OPERATION_INVALID", $"Unsupported task operation '{value}'.")
    };

    internal static ContextAudience ParseAudience(string value) => value switch
    {
        "private" => ContextAudience.Private,
        "shared" => ContextAudience.Shared,
        _ => throw new ContextCatalogException("TASK_AUDIENCE_INVALID", $"Unsupported task audience '{value}'.")
    };
}
