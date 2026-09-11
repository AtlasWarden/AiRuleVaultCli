using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleVault.Core;
using RuleVault.Storage;

namespace RuleVault.Installation;

public enum LifecycleMode
{
    Install,
    Update,
    Migrate,
    Repair,
    Rollback,
    Uninstall
}

public enum LifecycleOperationKind
{
    Create,
    Replace,
    Preserve,
    RemoveOwned
}

public sealed record LifecycleOperation(
    string OperationId,
    LifecycleOperationKind Kind,
    string RelativePath,
    string BeforeRawSha256,
    string? Content,
    string Ownership,
    IReadOnlyList<string> RequiredControlIds);

public sealed record LifecycleDecision(string DecisionId, string PlanSha256, string AffectedBeforeSha256, string Choice);

public sealed record LifecyclePlan(
    int SchemaVersion,
    string PlanId,
    LifecycleMode Mode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string VaultId,
    string ConfigRoot,
    string VaultRoot,
    IReadOnlyList<LifecycleOperation> Operations,
    IReadOnlyList<string> RequiredDecisions,
    string PlanSha256)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record PlanValidationResult(bool Allowed, string Code, string Detail);

public sealed record AdditiveUpdatePlan(LifecyclePlan Plan, int AddedFiles, int ConvertedVendorFiles, int PreservedDivergentFiles, string? ArchiveRoot);

internal sealed record IntegrityEntry(string FileId, string Path, string Sha256, int Revision, string ChangeOrigin, string ChangedAt);
internal sealed record RegistryEntry(string VaultRoot, string AppliedGuideSha256);
internal sealed record MigrationRule(string Id, string From, string To);

public static class LifecyclePlanner
{
    public static async Task<LifecyclePlan> CreateNewInstallAsync(
        string configRoot,
        string vaultRoot,
        string vaultId,
        DateTimeOffset now,
        string? packageRoot = null,
        string? appliedGuideSha256 = null,
        CancellationToken cancellationToken = default)
    {
        var operations = new List<LifecycleOperation>
        {
            Create(".vault-system/vault.json", $"{{\"schema_version\":1,\"vault_id\":\"{vaultId}\"}}\n", "vendor"),
            Create(".vault-system/routes.json", "{\"schema_version\":1,\"routes\":[]}\n", "vendor")
        };
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            operations.Add(Create("global/core-runtime.md", "# Core runtime\n\nVerified Rule Vault runtime content.\n", "vendor"));
        }
        else
        {
            foreach (var relativePath in EnumeratePackagePayload(packageRoot))
            {
                var content = File.ReadAllText(Path.Combine(packageRoot, "runtime-payload", relativePath.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8);
                operations.Add(Create(relativePath, content, "vendor"));
            }
        }
        var protectedContent = operations
            .Where(operation => operation.Ownership == "vendor" &&
                !operation.RelativePath.StartsWith(".vault-system/", StringComparison.Ordinal) &&
                !string.Equals(operation.RelativePath, "vault-registry.json", StringComparison.Ordinal))
            .ToArray();
        var integrityManifest = BuildIntegrityManifest(vaultId, now, protectedContent);
        operations.Add(Create(
            ".vault-system/content-integrity.json",
            integrityManifest,
            "vendor"));
        operations.Add(await CreateRegistryAnchorOperationAsync(
            configRoot,
            vaultRoot,
            vaultId,
            appliedGuideSha256 ?? "UNSPECIFIED",
            CanonicalHash.Text(integrityManifest).ToUpperInvariant(),
            now,
            cancellationToken));
        var bare = new LifecyclePlan(
            LifecyclePlan.CurrentSchemaVersion,
            Guid.NewGuid().ToString("D"),
            LifecycleMode.Install,
            now,
            now.AddHours(24),
            vaultId,
            configRoot,
            vaultRoot,
            operations.OrderBy(operation => operation.RelativePath, StringComparer.Ordinal).ToArray(),
            [],
            string.Empty);
        return bare with { PlanSha256 = PlanDigest.Compute(bare) };
    }

    public static LifecyclePlan CreatePreservationPlan(
        LifecycleMode mode,
        string configRoot,
        string vaultRoot,
        string vaultId,
        IEnumerable<LifecycleOperation> operations,
        IEnumerable<string>? requiredDecisions,
        DateTimeOffset now)
    {
        var bare = new LifecyclePlan(
            LifecyclePlan.CurrentSchemaVersion,
            Guid.NewGuid().ToString("D"),
            mode,
            now,
            now.AddHours(24),
            vaultId,
            configRoot,
            vaultRoot,
            operations.OrderBy(operation => operation.RelativePath, StringComparer.Ordinal).ToArray(),
            (requiredDecisions ?? []).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            string.Empty);
        return bare with { PlanSha256 = PlanDigest.Compute(bare) };
    }

    public static async Task<AdditiveUpdatePlan> CreateAdditiveUpdateAsync(
        string configRoot,
        string vaultRoot,
        string vaultId,
        string packageRoot,
        string appliedGuideSha256,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!IsSha256(appliedGuideSha256))
        {
            throw new LifecycleException("GUIDE_HASH_INVALID", "The update guide hash must be a SHA-256 value.");
        }

        var packagePaths = EnumeratePackagePayload(packageRoot);
        var metadata = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, ".vault-system/vault.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var metadataDocument = StrictJson.Parse(metadata);
        if (!metadataDocument.RootElement.TryGetProperty("vault_id", out var metadataId) ||
            metadataId.ValueKind != JsonValueKind.String ||
            !string.Equals(metadataId.GetString(), vaultId, StringComparison.OrdinalIgnoreCase))
        {
            throw new LifecycleException("VAULT_ID_MISMATCH", "Selected vault metadata does not match the requested immutable vault ID.");
        }

        var manifestBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, ".vault-system/content-integrity.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var manifestDocument = StrictJson.Parse(manifestBytes);
        var entries = ParseIntegrityEntries(manifestDocument.RootElement);
        var manifestText = CanonicalHash.DecodeUtf8(manifestBytes, allowBom: false);
        var registryEntry = await ReadVerifiedRegistryEntryAsync(
            configRoot,
            vaultRoot,
            vaultId,
            CanonicalHash.Text(manifestText).ToUpperInvariant(),
            cancellationToken);

        if (string.Equals(registryEntry.AppliedGuideSha256, appliedGuideSha256, StringComparison.OrdinalIgnoreCase))
        {
            var noOp = CreatePreservationPlan(LifecycleMode.Update, configRoot, vaultRoot, vaultId, [], [], now);
            return new AdditiveUpdatePlan(noOp, 0, 0, 0, null);
        }

        await VerifyProtectedContentAsync(vaultRoot, entries, cancellationToken);
        var migrationRules = ReadMigrationRules(packageRoot);
        var knownPaths = entries.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var entriesByPath = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var operations = new List<LifecycleOperation>();
        var addedEntries = new List<IntegrityEntry>();
        var preservedDivergent = 0;
        var convertedVendorFiles = 0;
        var archiveRoot = $".vault-system/archive/{now.UtcDateTime:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var archivedLegacyFiles = 0;

        foreach (var migration in migrationRules)
        {
            if (!entriesByPath.TryGetValue(migration.From, out var sourceEntry) ||
                !string.Equals(sourceEntry.ChangeOrigin, "installation", StringComparison.Ordinal) ||
                !packagePaths.Contains(migration.To, StringComparer.Ordinal))
            {
                continue;
            }

            var sourceTarget = SafePath.ValidateRelative(vaultRoot, migration.From, SafePathProfile.PrivateConfig);
            var destinationTarget = SafePath.ValidateRelative(vaultRoot, migration.To, SafePathProfile.PrivateConfig);
            TrustedFileSystem.EnsureSupported(sourceTarget);
            TrustedFileSystem.EnsureSupported(destinationTarget);
            if (!File.Exists(sourceTarget.FullPath) || File.Exists(destinationTarget.FullPath))
            {
                continue;
            }

            var sourceBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, migration.From, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            var archivePath = $"{archiveRoot}/{migration.From}";
            operations.Add(Create(archivePath, CanonicalHash.DecodeUtf8(sourceBytes, allowBom: false), "archive"));
            operations.Add(new LifecycleOperation(
                Guid.NewGuid().ToString("D"),
                LifecycleOperationKind.RemoveOwned,
                migration.From,
                CanonicalHash.Raw(sourceBytes),
                null,
                "vendor",
                ["RV-MIG-001", "RV-WRITE-001"]));
            entriesByPath.Remove(migration.From);
            knownPaths.Remove(migration.From);
            archivedLegacyFiles++;
        }

        foreach (var relativePath in packagePaths)
        {
            var packagePath = Path.Combine(packageRoot, "runtime-payload", relativePath.Replace('/', Path.DirectorySeparatorChar));
            var packageContent = File.ReadAllText(packagePath, Encoding.UTF8);
            var installedTarget = SafePath.ValidateRelative(vaultRoot, relativePath, SafePathProfile.PrivateConfig);
            TrustedFileSystem.EnsureSupported(installedTarget);
            if (!File.Exists(installedTarget.FullPath))
            {
                operations.Add(Create(relativePath, packageContent, "vendor"));
                if (knownPaths.Add(relativePath))
                {
                    addedEntries.Add(new IntegrityEntry(Guid.NewGuid().ToString("D"), relativePath, CanonicalHash.Text(packageContent).ToUpperInvariant(), 1, "installation", now.ToUniversalTime().ToString("O")));
                }
                continue;
            }

            var current = await TrustedFileSystem.ReadTextAsync(vaultRoot, relativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            if (!string.Equals(CanonicalHash.Text(current), CanonicalHash.Text(packageContent), StringComparison.OrdinalIgnoreCase))
            {
                if (entriesByPath.TryGetValue(relativePath, out var entry) && string.Equals(entry.ChangeOrigin, "installation", StringComparison.Ordinal))
                {
                    var currentBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, relativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
                    operations.Add(new LifecycleOperation(
                        Guid.NewGuid().ToString("D"),
                        LifecycleOperationKind.Replace,
                        relativePath,
                        CanonicalHash.Raw(currentBytes),
                        packageContent,
                        "vendor",
                        ["RV-MIG-001", "RV-WRITE-001"]));
                    entriesByPath[relativePath] = entry with
                    {
                        Sha256 = CanonicalHash.Text(packageContent).ToUpperInvariant(),
                        Revision = checked(entry.Revision + 1),
                        ChangeOrigin = "installation",
                        ChangedAt = now.ToUniversalTime().ToString("O")
                    };
                    convertedVendorFiles++;
                    continue;
                }

                // Custom, unknown, and semantically ambiguous text stays active until reviewed.
                preservedDivergent++;
            }

            operations.Add(new LifecycleOperation(Guid.NewGuid().ToString("D"), LifecycleOperationKind.Preserve, relativePath, "PRESERVE", null, "mixed", ["RV-MIG-001"]));
        }

        if (addedEntries.Count > 0 || convertedVendorFiles > 0 || archivedLegacyFiles > 0)
        {
            operations.Add(new LifecycleOperation(
                Guid.NewGuid().ToString("D"),
                LifecycleOperationKind.Replace,
                ".vault-system/content-integrity.json",
                CanonicalHash.Raw(manifestBytes),
                BuildUpdatedIntegrityManifest(manifestDocument.RootElement, entriesByPath.Values.Concat(addedEntries).ToArray(), now),
                "vendor",
                ["RV-INTEGRITY-001", "RV-WRITE-001"]));
        }

        var resultingManifest = addedEntries.Count > 0 || convertedVendorFiles > 0 || archivedLegacyFiles > 0
            ? BuildUpdatedIntegrityManifest(manifestDocument.RootElement, entriesByPath.Values.Concat(addedEntries).ToArray(), now)
            : CanonicalHash.DecodeUtf8(manifestBytes, allowBom: false);
        operations.Add(await CreateRegistryAnchorOperationAsync(
            configRoot,
            vaultRoot,
            vaultId,
            appliedGuideSha256,
            CanonicalHash.Text(resultingManifest).ToUpperInvariant(),
            now,
            cancellationToken));
        var plan = CreatePreservationPlan(LifecycleMode.Update, configRoot, vaultRoot, vaultId, operations, [], now);
        return new AdditiveUpdatePlan(plan, addedEntries.Count, convertedVendorFiles, preservedDivergent, archivedLegacyFiles > 0 ? archiveRoot : null);
    }

    public static PlanValidationResult Validate(
        LifecyclePlan plan,
        DateTimeOffset now,
        IEnumerable<LifecycleDecision>? decisions,
        string expectedVaultId,
        string expectedConfigRoot,
        string expectedVaultRoot)
    {
        if (plan.SchemaVersion != LifecyclePlan.CurrentSchemaVersion)
        {
            return new(false, "PLAN_SCHEMA_UNSUPPORTED", "The plan schema is not supported.");
        }

        if (now > plan.ExpiresAt)
        {
            return new(false, "PLAN_STALE", "The plan has expired.");
        }

        if (!string.Equals(plan.PlanSha256, PlanDigest.Compute(plan), StringComparison.Ordinal) ||
            !string.Equals(plan.VaultId, expectedVaultId, StringComparison.Ordinal) ||
            !string.Equals(plan.ConfigRoot, expectedConfigRoot, StringComparison.Ordinal) ||
            !string.Equals(plan.VaultRoot, expectedVaultRoot, StringComparison.Ordinal))
        {
            return new(false, "PLAN_STALE", "The plan identity or digest no longer matches current inputs.");
        }

        var decisionMap = (decisions ?? []).ToDictionary(decision => decision.DecisionId, StringComparer.Ordinal);
        foreach (var required in plan.RequiredDecisions)
        {
            if (!decisionMap.TryGetValue(required, out var decision) ||
                decision.PlanSha256 != plan.PlanSha256 ||
                string.IsNullOrWhiteSpace(decision.Choice))
            {
                return new(false, "DECISION_REQUIRED", $"Decision '{required}' is required and bound to this plan digest.");
            }
        }

        return new(true, "OK", "Plan is current and complete.");
    }

    private static LifecycleOperation Create(string path, string content, string ownership) => new(
        Guid.NewGuid().ToString("D"),
        LifecycleOperationKind.Create,
        path,
        "MISSING",
        content,
        ownership,
        ["RV-WRITE-001"]);

    private static IReadOnlyList<string> EnumeratePackagePayload(string packageRoot)
    {
        var payloadRoot = Path.Combine(Path.GetFullPath(packageRoot), "runtime-payload");
        if (!Directory.Exists(payloadRoot) || File.GetAttributes(payloadRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new LifecycleException("PACKAGE_PAYLOAD_INVALID", "The package runtime-payload directory is missing or linked.");
        }

        var paths = new List<string>();
        foreach (var file in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new LifecycleException("PACKAGE_PAYLOAD_INVALID", "Package payload contains a linked file.");
            }

            var relative = Path.GetRelativePath(payloadRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            TrustedFileSystem.EnsureSupported(SafePath.ValidateRelative(payloadRoot, relative, SafePathProfile.PackageExtraction));
            paths.Add(relative);
        }

        if (paths.Count == 0)
        {
            throw new LifecycleException("PACKAGE_PAYLOAD_INVALID", "The package runtime payload is empty.");
        }

        return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static string BuildIntegrityManifest(string vaultId, DateTimeOffset now, IReadOnlyList<LifecycleOperation> protectedContent)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 1);
        writer.WriteString("vault_id", vaultId);
        writer.WriteNumber("revision", 1);
        writer.WriteString("updated_at", now.ToUniversalTime().ToString("O"));
        writer.WritePropertyName("files");
        writer.WriteStartArray();
        foreach (var operation in protectedContent.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("file_id", Guid.NewGuid().ToString("D"));
            writer.WriteString("path", operation.RelativePath);
            writer.WriteString("sha256", CanonicalHash.Text(operation.Content!).ToUpperInvariant());
            writer.WriteNumber("revision", 1);
            writer.WriteString("change_origin", "installation");
            writer.WriteString("changed_at", now.ToUniversalTime().ToString("O"));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static IReadOnlyList<IntegrityEntry> ParseIntegrityEntries(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object || !manifest.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new LifecycleException("INTEGRITY_MANIFEST_INVALID", "The existing content integrity manifest has no files array.");
        }

        var entries = new List<IntegrityEntry>();
        foreach (var file in files.EnumerateArray())
        {
            string Required(string property) => file.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new LifecycleException("INTEGRITY_MANIFEST_INVALID", $"Integrity entry has no valid {property}.");
            if (!file.TryGetProperty("revision", out var revision) || !revision.TryGetInt32(out var revisionNumber))
            {
                throw new LifecycleException("INTEGRITY_MANIFEST_INVALID", "Integrity entry has no valid revision.");
            }

            entries.Add(new IntegrityEntry(Required("file_id"), Required("path"), Required("sha256"), revisionNumber, Required("change_origin"), Required("changed_at")));
        }

        if (entries.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count() != entries.Count)
        {
            throw new LifecycleException("INTEGRITY_MANIFEST_INVALID", "Integrity manifest has duplicate paths.");
        }

        return entries;
    }

    private static async Task<RegistryEntry> ReadVerifiedRegistryEntryAsync(
        string configRoot,
        string vaultRoot,
        string vaultId,
        string expectedManifestSha256,
        CancellationToken cancellationToken)
    {
        const string registryPath = "vault-registry.json";
        var registryTarget = SafePath.ValidateRelative(configRoot, registryPath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(registryTarget);
        if (!File.Exists(registryTarget.FullPath))
        {
            throw new LifecycleException("REGISTRY_ENTRY_NOT_FOUND", "The selected vault has no registry entry; no update was planned.");
        }

        var registryBytes = await TrustedFileSystem.ReadAllBytesAsync(configRoot, registryPath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var registry = StrictJson.Parse(registryBytes);
        if (registry.RootElement.ValueKind != JsonValueKind.Object ||
            !registry.RootElement.TryGetProperty("vaults", out var vaults) ||
            vaults.ValueKind != JsonValueKind.Array)
        {
            throw new LifecycleException("REGISTRY_INVALID", "Registry has no valid vaults array.");
        }

        JsonElement? selected = null;
        foreach (var candidate in vaults.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object ||
                !candidate.TryGetProperty("vault_id", out var candidateId) ||
                candidateId.ValueKind != JsonValueKind.String)
            {
                throw new LifecycleException("REGISTRY_INVALID", "Registry has an invalid vault entry.");
            }

            if (string.Equals(candidateId.GetString(), vaultId, StringComparison.OrdinalIgnoreCase))
            {
                if (selected is not null)
                {
                    throw new LifecycleException("REGISTRY_INVALID", "Registry has duplicate entries for the selected vault ID.");
                }

                selected = candidate;
            }
        }

        if (selected is null)
        {
            throw new LifecycleException("REGISTRY_ENTRY_NOT_FOUND", "The selected vault ID is not present in the registry; no update was planned.");
        }

        string RequiredString(string property) => selected.Value.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new LifecycleException("REGISTRY_INVALID", $"Registry entry has no valid {property}.");

        var registeredRoot = RequiredString("vault_root");
        if (!string.Equals(NormalizeRoot(registeredRoot), NormalizeRoot(vaultRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new LifecycleException("REGISTRY_ROOT_MISMATCH", "The registry entry points to a different vault root; use the migration workflow instead of updating an unrelated folder.");
        }

        var manifestAnchor = RequiredString("protected_content_manifest_sha256");
        if (!string.Equals(manifestAnchor, expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new LifecycleException("INTEGRITY_ANCHOR_MISMATCH", "The registry manifest anchor does not match the selected vault. Existing bytes were preserved and no update was planned.");
        }

        return new RegistryEntry(registeredRoot, RequiredString("applied_guide_sha256"));
    }

    private static async Task VerifyProtectedContentAsync(string vaultRoot, IReadOnlyList<IntegrityEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            if (!IsSha256(entry.Sha256))
            {
                throw new LifecycleException("INTEGRITY_MANIFEST_INVALID", $"Integrity entry '{entry.Path}' has an invalid SHA-256 value.");
            }

            var content = await TrustedFileSystem.ReadTextAsync(vaultRoot, entry.Path, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            if (!string.Equals(CanonicalHash.Text(content), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new LifecycleException("INTEGRITY_HASH_MISMATCH", $"Protected content does not match manifest: '{entry.Path}'. Existing bytes were preserved and no update was planned.");
            }
        }
    }

    private static IReadOnlyList<MigrationRule> ReadMigrationRules(string packageRoot)
    {
        const string rulesPath = "migrations.json";
        var target = SafePath.ValidateRelative(packageRoot, rulesPath, SafePathProfile.PackageExtraction);
        TrustedFileSystem.EnsureSupported(target);
        if (!File.Exists(target.FullPath))
        {
            throw new LifecycleException("MIGRATION_RULES_INVALID", "The package migration rules are missing.");
        }

        var bytes = File.ReadAllBytes(target.FullPath);
        using var document = StrictJson.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schema_version", out var schemaVersion) || !schemaVersion.TryGetInt32(out var version) || version != 1 ||
            !document.RootElement.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
        {
            throw new LifecycleException("MIGRATION_RULES_INVALID", "The package migration rules have an unsupported format.");
        }

        var parsed = new List<MigrationRule>();
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object || rule.EnumerateObject().Any(property => property.Name is not ("id" or "from" or "to" or "kind" or "requires_exact_prior_base")))
            {
                throw new LifecycleException("MIGRATION_RULES_INVALID", "A migration rule has unsupported fields.");
            }

            string Required(string property) => rule.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new LifecycleException("MIGRATION_RULES_INVALID", $"A migration rule has no valid {property}.");
            var id = Required("id");
            var from = Required("from");
            var to = Required("to");
            if (!string.Equals(Required("kind"), "vendor-alias", StringComparison.Ordinal) ||
                !rule.TryGetProperty("requires_exact_prior_base", out var exactBase) || exactBase.ValueKind != JsonValueKind.True)
            {
                throw new LifecycleException("MIGRATION_RULES_INVALID", $"Migration rule '{id}' does not require an exact prior vendor base.");
            }

            SafePath.ValidateRelative(packageRoot, from, SafePathProfile.PrivateConfig);
            SafePath.ValidateRelative(packageRoot, to, SafePathProfile.PrivateConfig);
            parsed.Add(new MigrationRule(id, from.Replace('\\', '/'), to.Replace('\\', '/')));
        }

        if (parsed.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != parsed.Count ||
            parsed.Select(rule => rule.From).Distinct(StringComparer.Ordinal).Count() != parsed.Count)
        {
            throw new LifecycleException("MIGRATION_RULES_INVALID", "Migration rules have duplicate IDs or source paths.");
        }

        return parsed;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));

    private static string NormalizeRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string BuildUpdatedIntegrityManifest(JsonElement existing, IReadOnlyList<IntegrityEntry> entries, DateTimeOffset now)
    {
        var vaultId = existing.GetProperty("vault_id").GetString() ?? throw new LifecycleException("INTEGRITY_MANIFEST_INVALID", "Manifest vault_id is missing.");
        var revision = existing.GetProperty("revision").GetInt32();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 1);
        writer.WriteString("vault_id", vaultId);
        writer.WriteNumber("revision", checked(revision + 1));
        writer.WriteString("updated_at", now.ToUniversalTime().ToString("O"));
        writer.WritePropertyName("files");
        writer.WriteStartArray();
        foreach (var entry in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("file_id", entry.FileId);
            writer.WriteString("path", entry.Path);
            writer.WriteString("sha256", entry.Sha256);
            writer.WriteNumber("revision", entry.Revision);
            writer.WriteString("change_origin", entry.ChangeOrigin);
            writer.WriteString("changed_at", entry.ChangedAt);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static async Task<LifecycleOperation> CreateRegistryAnchorOperationAsync(
        string configRoot,
        string vaultRoot,
        string vaultId,
        string appliedGuideSha256,
        string protectedManifestSha256,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string registryPath = "vault-registry.json";
        var registryTarget = SafePath.ValidateRelative(configRoot, registryPath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(registryTarget);
        JsonObject root;
        string beforeHash;
        if (File.Exists(registryTarget.FullPath))
        {
            var bytes = await TrustedFileSystem.ReadAllBytesAsync(configRoot, registryPath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            using var strict = StrictJson.Parse(bytes);
            root = JsonNode.Parse(strict.RootElement.GetRawText())?.AsObject() ?? throw new LifecycleException("REGISTRY_INVALID", "Registry root must be a JSON object.");
            beforeHash = CanonicalHash.Raw(bytes);
        }
        else
        {
            root = new JsonObject
            {
                ["schema_version"] = 1,
                ["default_vault_root"] = vaultRoot.Replace('\\', '/'),
                ["vaults"] = new JsonArray(),
                ["updated_at"] = now.ToUniversalTime().ToString("O")
            };
            beforeHash = "MISSING";
        }

        if (root["vaults"] is not JsonArray vaults)
        {
            throw new LifecycleException("REGISTRY_INVALID", "Registry has no vaults array.");
        }

        JsonObject? entry = null;
        foreach (var candidate in vaults.OfType<JsonObject>())
        {
            if (string.Equals(candidate["vault_id"]?.GetValue<string>(), vaultId, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                break;
            }
        }

        entry ??= new JsonObject();
        if (!vaults.Contains(entry)) { vaults.Add(entry); }
        entry["vault_root"] = vaultRoot.Replace('\\', '/');
        entry["vault_id"] = vaultId;
        entry["applied_guide_sha256"] = appliedGuideSha256.ToUpperInvariant();
        entry["protected_content_manifest_sha256"] = protectedManifestSha256;
        entry["last_verified_at"] = now.ToUniversalTime().ToString("O");
        root["updated_at"] = now.ToUniversalTime().ToString("O");
        var content = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        return new LifecycleOperation(
            Guid.NewGuid().ToString("D"),
            beforeHash == "MISSING" ? LifecycleOperationKind.Create : LifecycleOperationKind.Replace,
            registryPath,
            beforeHash,
            content,
            "vendor",
            ["RV-REGISTRY-001", "RV-INTEGRITY-001"]);
    }
}

public static class PlanDigest
{
    public static string Compute(LifecyclePlan plan)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", plan.SchemaVersion);
        writer.WriteString("plan_id", plan.PlanId);
        writer.WriteString("mode", plan.Mode.ToString().ToLowerInvariant());
        writer.WriteString("created_at", plan.CreatedAt.ToUniversalTime());
        writer.WriteString("expires_at", plan.ExpiresAt.ToUniversalTime());
        writer.WriteString("vault_id", plan.VaultId);
        writer.WriteString("config_root", plan.ConfigRoot);
        writer.WriteString("vault_root", plan.VaultRoot);
        writer.WritePropertyName("operations");
        writer.WriteStartArray();
        foreach (var operation in plan.Operations)
        {
            writer.WriteStartObject();
            writer.WriteString("operation_id", operation.OperationId);
            writer.WriteString("kind", operation.Kind.ToString().ToLowerInvariant());
            writer.WriteString("path", operation.RelativePath);
            writer.WriteString("before_raw_sha256", operation.BeforeRawSha256);
            if (operation.Content is null)
            {
                writer.WriteNull("content");
            }
            else
            {
                writer.WriteString("content", operation.Content);
            }
            writer.WriteString("ownership", operation.Ownership);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("required_decisions");
        writer.WriteStartArray();
        foreach (var decision in plan.RequiredDecisions)
        {
            writer.WriteStringValue(decision);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return TaskDescriptor.Sha256(Encoding.UTF8.GetString(stream.ToArray()));
    }
}

public sealed class LifecycleApplier
{
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        LifecyclePlan plan,
        IEnumerable<LifecycleDecision>? decisions,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var validation = LifecyclePlanner.Validate(plan, now, decisions, plan.VaultId, plan.ConfigRoot, plan.VaultRoot);
        if (!validation.Allowed)
        {
            throw new LifecycleException(validation.Code, validation.Detail);
        }

        Directory.CreateDirectory(plan.VaultRoot);
        Directory.CreateDirectory(plan.ConfigRoot);
        var written = new List<string>();
        foreach (var operation in plan.Operations.Where(operation => operation.RelativePath is not "vault-registry.json" and not ".vault-system/content-integrity.json"))
        {
            await ApplyOperationAsync(plan.VaultRoot, operation, cancellationToken);
            written.Add(operation.RelativePath);
        }

        foreach (var operation in plan.Operations.Where(operation => operation.RelativePath == ".vault-system/content-integrity.json"))
        {
            await ApplyOperationAsync(plan.VaultRoot, operation, cancellationToken);
            written.Add(operation.RelativePath);
        }

        foreach (var operation in plan.Operations.Where(operation => operation.RelativePath == "vault-registry.json"))
        {
            await ApplyOperationAsync(plan.ConfigRoot, operation, cancellationToken);
            written.Add(operation.RelativePath);
        }

        return written;
    }

    private static async Task ApplyOperationAsync(string root, LifecycleOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Kind == LifecycleOperationKind.Preserve)
        {
            return;
        }

        if (operation.Kind == LifecycleOperationKind.RemoveOwned)
        {
            if (!string.Equals(operation.Ownership, "vendor", StringComparison.Ordinal) || operation.BeforeRawSha256 == "MISSING")
            {
                throw new LifecycleException("REMOVE_OWNED_REJECTED", "Only an archived, verified vendor file may be removed by an update plan.");
            }

            var target = SafePath.ValidateRelative(root, operation.RelativePath, SafePathProfile.PrivateConfig);
            TrustedFileSystem.EnsureSupported(target);
            if (!File.Exists(target.FullPath))
            {
                return;
            }

            var current = await TrustedFileSystem.ReadAllBytesAsync(root, operation.RelativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            if (!string.Equals(CanonicalHash.Raw(current), operation.BeforeRawSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new LifecycleException("PLAN_STALE", $"Vendor removal target '{operation.RelativePath}' changed after planning.");
            }

            File.Delete(target.FullPath);
            return;
        }

        if (operation.Content is null)
        {
            throw new LifecycleException("PLAN_CONTENT_MISSING", $"Operation '{operation.OperationId}' has no staged content.");
        }

        var expected = operation.BeforeRawSha256 == "MISSING" ? null : operation.BeforeRawSha256;
        if (expected is null && File.Exists(Path.Combine(root, operation.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
        {
            var existing = await TrustedFileSystem.ReadTextAsync(root, operation.RelativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            if (string.Equals(existing, operation.Content, StringComparison.Ordinal))
            {
                return;
            }

            throw new LifecycleException("PLAN_STALE", $"Create target '{operation.RelativePath}' already exists with different content.");
        }

        await TrustedWriter.WriteTextAsync(root, operation.RelativePath, operation.Content, expected, SafePathProfile.PrivateConfig, cancellationToken);
    }
}

public sealed class LifecycleException : Exception
{
    public LifecycleException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
