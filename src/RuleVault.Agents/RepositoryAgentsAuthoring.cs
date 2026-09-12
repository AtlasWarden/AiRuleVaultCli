using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleVault.Storage;

namespace RuleVault.Agents;

/// <summary>
/// Narrow writer for repository-owned .agents Markdown. The gate runs before
/// any repository-controlled .agents file is read or dereferenced. Existing
/// files must be ordinary Git index entries; new files may be created only
/// beneath a physically verified, non-reparse .agents directory. Shared Git
/// content deliberately has no private-vault hash anchor: branch divergence is
/// normal. The gate establishes safe provenance and object type, not identical
/// bytes across developers. Content is returned as repository data and is never
/// executed by this adapter.
/// </summary>
public sealed record RepositoryAgentsWriteRequest(string RepositoryRoot, string RelativePath, string Content, string? ExpectedRawSha256);
public sealed record RepositoryAgentsWriteResult(string RelativePath, string RawSha256, bool Created, bool BranchManifestUpdated);
public sealed record RepositoryAgentsReadResult(string RelativePath, string Content, string RawSha256, string MetadataStatus, string ContentHandling);
public sealed record RepositoryAgentsInitializeResult(string ProjectId, string Project, IReadOnlyList<string> CreatedPaths, string GitState);

public static class RepositoryAgentsAuthoring
{
    private const string RepositoryWriterTarget = "__rule-vault-repository-agents-transaction__";
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    public static async Task<RepositoryAgentsWriteResult> WriteAsync(RepositoryAgentsWriteRequest request, CancellationToken cancellationToken = default)
    {
        ValidateWritePath(request.RelativePath);
        var metadata = ManagedDocumentMetadataValidator.Parse(request.RelativePath, request.Content, requireCompleteSchema: true);
        var repositoryRoot = await VerifyRepositoryRootAsync(request.RepositoryRoot, cancellationToken);
        await VerifyGateAsync(repositoryRoot, request.RelativePath, cancellationToken);
        var target = SafePath.ValidateRelative(repositoryRoot, request.RelativePath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(target);
        var exists = File.Exists(target.FullPath!);
        if (exists && string.IsNullOrWhiteSpace(request.ExpectedRawSha256))
        {
            throw new VaultAuthoringException("REPOSITORY_WRITE_PRECONDITION_REQUIRED", "Read the repository .agents file through the adapter and supply its current raw SHA-256 before writing.");
        }

        var protectedUpdate = await PrepareBranchManifestUpdateAsync(repositoryRoot, request.RelativePath, request.Content, metadata, cancellationToken);
        try
        {
            if (protectedUpdate is null)
            {
                var ordinaryWrite = await TrustedWriter.WriteTextAsync(repositoryRoot, request.RelativePath, request.Content, request.ExpectedRawSha256, SafePathProfile.PrivateConfig, cancellationToken);
                return new RepositoryAgentsWriteResult(ordinaryWrite.RelativePath, ordinaryWrite.RawSha256, ordinaryWrite.Created, false);
            }

            var protectedWrite = await WriteProtectedPairAsync(repositoryRoot, request, protectedUpdate, cancellationToken);
            return new RepositoryAgentsWriteResult(protectedWrite.RelativePath, protectedWrite.RawSha256, protectedWrite.Created, true);
        }
        catch (WriteConflictException exception)
        {
            throw new VaultAuthoringException("REPOSITORY_WRITE_CONFLICT", exception.Message);
        }
    }

    public static async Task<RepositoryAgentsReadResult> ReadAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken = default)
    {
        ValidateReadPath(relativePath);
        var root = await VerifyRepositoryRootAsync(repositoryRoot, cancellationToken);
        return await ReadStableAsync(root, relativePath, cancellationToken);
    }

    private static async Task<RepositoryAgentsReadResult> ReadCoreAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        await VerifyGateAsync(root, relativePath, cancellationToken);
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(root, relativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var content = CanonicalHash.DecodeUtf8(bytes);
        var metadataStatus = "not-present";
        if (relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var json = StrictJson.Parse(bytes);
                metadataStatus = ValidateRepositoryJsonSchema(relativePath, json.RootElement) ? "schema-valid" : "unsupported-schema-data-only";
            }
            catch (Exception exception) when (exception is StorageFormatException or VaultAuthoringException)
            {
                metadataStatus = "invalid-data-only";
            }
        }
        else if (content.StartsWith("---\n", StringComparison.Ordinal))
        {
            try
            {
                _ = ManagedDocumentMetadataValidator.Parse(relativePath, content, requireCompleteSchema: true);
                metadataStatus = "schema-valid";
            }
            catch (VaultAuthoringException) { metadataStatus = "invalid-data-only"; }
        }
        var handling = await InspectBranchIntegrityAsync(root, relativePath, content, cancellationToken);
        if (handling == "verified-current-branch" && metadataStatus != "schema-valid")
        {
            handling = "invalid-metadata-data-only";
        }
        return new RepositoryAgentsReadResult(relativePath, content, CanonicalHash.Raw(bytes), metadataStatus, handling);
    }

    public static async Task<string> ResolveRepositoryRootAsync(string workspaceFolder, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(workspaceFolder) || !Directory.Exists(workspaceFolder))
        {
            throw new VaultAuthoringException("REPOSITORY_WORKSPACE_INVALID", "The registered workspace folder must be an existing absolute path.");
        }

        var reported = (await RunGitAsync(workspaceFolder, "rev-parse --show-toplevel", cancellationToken)).Trim();
        var actual = Path.GetFullPath(reported).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(actual) || File.GetAttributes(actual).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new VaultAuthoringException("REPOSITORY_ROOT_REPARSE", "The Git work-tree root must be a real, non-reparse directory.");
        }

        return actual;
    }

    public static async Task<RepositoryAgentsInitializeResult> InitializeAsync(
        string workspaceFolder,
        string projectId,
        string project,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(projectId, out _) ||
            !System.Text.RegularExpressions.Regex.IsMatch(project, "^[a-z0-9]+(?:-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(title))
        {
            throw new VaultAuthoringException("REPOSITORY_PROJECT_IDENTITY_INVALID", "Repository initialization requires a UUID project id, a sanitized project slug, and a title.");
        }

        var root = await ResolveRepositoryRootAsync(workspaceFolder, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".agents/project.json"] = JsonSerializer.Serialize(new
            {
                schema_version = 1,
                project_id = projectId,
                project_slug = project,
                title,
                storage_mode = "git-backed",
                git_state = "initialized",
                updated_at = now.ToUniversalTime().ToString("O")
            }, IndentedJson) + "\n",
            [".agents/index.md"] = ManagedRepositoryDocument(title, project, "index", $"# {title}\n\n## File Categories\n\n- [Rules](./rules/index.md)\n- [Context](./context/index.md)"),
            [".agents/rules/index.md"] = ManagedRepositoryDocument($"{title} Rules", project, "index", $"# {title} Rules\n\n## Project-Wide Always Load\n\nNone.\n\n## Rule Categories\n\nNone."),
            [".agents/context/index.md"] = ManagedRepositoryDocument($"{title} Context", project, "index", $"# {title} Context\n\n## Project-Wide Always Load\n\n- [Current State](./current-state.md)\n\n## Conditional and Historical Context\n\nNone."),
            [".agents/context/current-state.md"] = ManagedRepositoryDocument($"{title} Current State", project, "context", $"# {title} Current State\n\n## Primary Purpose\n\nUnknown - user input required.\n\n## End Goal / Success Condition\n\nUnknown - user input required.\n\n## Current Objective\n\nRepository Rule Vault scaffold initialized.")
        };
        var protectedPaths = new[] { ".agents/project.json", ".agents/index.md", ".agents/rules/index.md", ".agents/context/index.md", ".agents/context/current-state.md" };
        files[".agents/content-integrity.json"] = BuildRepositoryManifest(projectId, files, protectedPaths, now);

        foreach (var path in files.Keys)
        {
            await VerifyGateAsync(root, path, cancellationToken);
            var target = SafePath.ValidateRelative(root, path, SafePathProfile.PrivateConfig);
            TrustedFileSystem.EnsureSupported(target);
            if (File.Exists(target.FullPath!))
            {
                throw new VaultAuthoringException("REPOSITORY_AGENTS_EXISTS", "Repository .agents initialization will not overwrite an existing managed file. Read and update the existing project through normal commands.");
            }
        }

        var gitDirectory = Path.Combine(root, ".git");
        if (!Directory.Exists(gitDirectory) || File.GetAttributes(gitDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new VaultAuthoringException("REPOSITORY_LOCKS_UNSUPPORTED", "The repository has no safe local .git directory for initialization locks.");
        }

        var stateRoot = Path.Combine(gitDirectory, "rule-vault-locks");
        var locks = new List<ProtectedLock>();
        await using var repositoryTransaction = await ProtectedLock.AcquireAsync(
            stateRoot,
            RepositoryWriterTarget,
            waitTimeout: VaultIntegrityTransaction.WriterWaitTimeout,
            cancellationToken: cancellationToken);
        try
        {
            foreach (var path in files.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                locks.Add(await ProtectedLock.AcquireAsync(stateRoot, path, waitTimeout: VaultIntegrityTransaction.WriterWaitTimeout, cancellationToken: cancellationToken));
            }

            foreach (var path in files.Keys)
            {
                var target = SafePath.ValidateRelative(root, path, SafePathProfile.PrivateConfig);
                TrustedFileSystem.EnsureSupported(target);
                if (File.Exists(target.FullPath!))
                {
                    throw new VaultAuthoringException("REPOSITORY_AGENTS_EXISTS", "Repository .agents initialization will not overwrite an existing managed file. Read and update the existing project through normal commands.");
                }
            }

            var journal = new TransactionJournal(
                1,
                Guid.NewGuid().ToString("D"),
                locks[0].WriterId,
                TransactionState.Prepared,
                now,
                files.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new TransactionTarget(item.Key, "MISSING", CanonicalHash.Raw(Encoding.UTF8.GetBytes(item.Value))))
                    .ToArray());
            await JournalStore.WriteAsync(stateRoot, journal, cancellationToken);
            foreach (var file in files.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                await TrustedWriter.WriteTextAsync(root, file.Key, file.Value, null, SafePathProfile.PrivateConfig, cancellationToken);
            }
            await JournalStore.WriteAsync(stateRoot, journal with { State = TransactionState.Committed }, cancellationToken);
        }
        finally
        {
            foreach (var item in locks.AsEnumerable().Reverse()) { await item.DisposeAsync(); }
        }

        return new RepositoryAgentsInitializeResult(projectId, project, files.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(), "initialized-candidate");
    }

    private static void ValidateReadPath(string path)
    {
        var result = SafePath.ValidateRelative(Path.GetTempPath(), path, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(result);
        if (!path.StartsWith(".agents/", StringComparison.Ordinal) ||
            (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new VaultAuthoringException("REPOSITORY_AGENTS_PATH_DENIED", "Repository reads are limited to managed Markdown and deterministic JSON beneath .agents.");
        }
    }

    private static void ValidateWritePath(string path)
    {
        ValidateReadPath(path);
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path == ".agents/content-integrity.json")
        {
            throw new VaultAuthoringException("REPOSITORY_AGENTS_PATH_DENIED", "Repository writes accept managed .agents Markdown. Protected Markdown is paired with its current-branch manifest automatically; the manifest cannot be written directly.");
        }
    }

    private static async Task<string> InspectBranchIntegrityAsync(string root, string path, string content, CancellationToken cancellationToken)
    {
        const string manifestPath = ".agents/content-integrity.json";
        if (path == manifestPath)
        {
            return "manifest-data-only";
        }

        var manifestTarget = SafePath.ValidateRelative(root, manifestPath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(manifestTarget);
        if (!File.Exists(manifestTarget.FullPath!))
        {
            return "unprotected-current-branch-data";
        }

        await VerifyGateAsync(root, manifestPath, cancellationToken);
        JsonElement files;
        try
        {
            var manifestBytes = await TrustedFileSystem.ReadAllBytesAsync(root, manifestPath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            using var manifest = StrictJson.Parse(manifestBytes);
            ValidateRepositoryManifest(manifest.RootElement);
            files = manifest.RootElement.GetProperty("files").Clone();
        }
        catch (Exception exception) when (exception is StorageFormatException or VaultAuthoringException)
        {
            return "invalid-manifest-data-only";
        }

        var manifestRelative = path[".agents/".Length..];
        var matches = files.EnumerateArray().Where(entry =>
            entry.TryGetProperty("path", out var entryPath) &&
            entryPath.ValueKind == JsonValueKind.String &&
            (string.Equals(entryPath.GetString(), path, StringComparison.Ordinal) || string.Equals(entryPath.GetString(), manifestRelative, StringComparison.Ordinal))).ToArray();
        if (matches.Length == 0)
        {
            return "unprotected-current-branch-data";
        }
        if (matches.Length != 1 || !matches[0].TryGetProperty("sha256", out var expected) || expected.ValueKind != JsonValueKind.String)
        {
            throw new VaultAuthoringException("REPOSITORY_MANIFEST_INVALID", $"The current branch manifest has an invalid or duplicate entry for '{path}'.");
        }
        if (!string.Equals(CanonicalHash.Text(content), expected.GetString(), StringComparison.OrdinalIgnoreCase))
        {
            return "candidate-integrity-mismatch-data-only";
        }

        var status = await RunGitAsync(root, "status --porcelain=v1 -- " + QuoteForGitPath(path) + " " + QuoteForGitPath(manifestPath), cancellationToken);
        return string.IsNullOrWhiteSpace(status) ? "verified-current-branch" : "candidate-working-tree-data-only";
    }

    private static string ManagedRepositoryDocument(string title, string project, string kind, string body)
    {
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        return $"---\nfile_id: \"{Guid.NewGuid():D}\"\ntitle: \"{title.Replace("\"", "'", StringComparison.Ordinal)}\"\nproject: \"{project}\"\nkind: \"{kind}\"\nstatus: \"active\"\naliases: []\nhasInboundLinks: false\ncreated: \"{now}\"\nupdated: \"{now}\"\n---\n\n{body.TrimEnd()}\n";
    }

    private static string BuildRepositoryManifest(string projectId, IReadOnlyDictionary<string, string> files, IReadOnlyList<string> protectedPaths, DateTimeOffset now)
    {
        var entries = protectedPaths.OrderBy(path => path, StringComparer.Ordinal).Select(path => new
        {
            file_id = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? ManagedDocumentMetadataValidator.Parse(path, files[path], requireCompleteSchema: true).FileId
                : Guid.NewGuid().ToString("D"),
            path = path[".agents/".Length..],
            sha256 = CanonicalHash.Text(files[path]).ToUpperInvariant(),
            revision = 1,
            change_origin = "repository-initialization",
            changed_at = now.ToUniversalTime().ToString("O")
        });
        return JsonSerializer.Serialize(new
        {
            schema_version = 1,
            project_id = projectId,
            revision = 1,
            updated_at = now.ToUniversalTime().ToString("O"),
            files = entries
        }, IndentedJson) + "\n";
    }

    private static async Task<string> VerifyRepositoryRootAsync(string root, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new VaultAuthoringException("REPOSITORY_ROOT_INVALID", "Repository root must be absolute.");
        }

        var expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actual = await ResolveRepositoryRootAsync(root, cancellationToken);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultAuthoringException("REPOSITORY_ROOT_MISMATCH", "The supplied path is not the Git work-tree root.");
        }

        var rootAttributes = File.GetAttributes(actual);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new VaultAuthoringException("REPOSITORY_ROOT_REPARSE", "A reparse-point repository root is not accepted for .agents access.");
        }

        return actual;
    }

    private static async Task VerifyGateAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        // Git's index reports the object type without opening repository-controlled files.
        var listing = await RunGitAsync(root, "ls-files --stage -- " + QuoteForGitPath(relativePath), cancellationToken);
        var target = SafePath.ValidateRelative(root, relativePath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(target);
        var agentsRoot = Path.Combine(root, ".agents");
        if (Directory.Exists(agentsRoot) && File.GetAttributes(agentsRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new VaultAuthoringException("RV-SEC-001", ".agents is a reparse point. Replace it with a real repository directory before using the adapter.");
        }

        if (File.Exists(target.FullPath!))
        {
            var lines = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var mode = lines.Length == 1 ? lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
            if (mode is not ("100644" or "100755"))
            {
                throw new VaultAuthoringException("RV-SEC-001", $"'{relativePath}' is not an allowed regular Git repository file. Correct the Git object type before retrying.");
            }
        }
        else
        {
            // New files get a physical no-follow check before directory creation.
            var parent = Path.GetDirectoryName(target.FullPath!)!;
            var relativeParent = Path.GetRelativePath(root, parent).Replace('\\', '/');
            var parentCheck = SafePath.ValidateRelative(root, relativeParent, SafePathProfile.PrivateConfig, expectDirectory: Directory.Exists(parent));
            TrustedFileSystem.EnsureSupported(parentCheck);
        }
    }

    private sealed record BranchManifestUpdate(string Content, string RawSha256);

    private static async Task<BranchManifestUpdate?> PrepareBranchManifestUpdateAsync(
        string root,
        string path,
        string content,
        ManagedDocumentMetadata metadata,
        CancellationToken cancellationToken)
    {
        const string manifestPath = ".agents/content-integrity.json";
        var manifestTarget = SafePath.ValidateRelative(root, manifestPath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(manifestTarget);
        if (!File.Exists(manifestTarget.FullPath!))
        {
            if (metadata.RequiresIntegrityProtection)
            {
                throw new VaultAuthoringException("REPOSITORY_MANIFEST_REQUIRED", $"'{path}' is an index or mandatory/protected file, so a current-branch .agents integrity manifest is required.");
            }
            return null;
        }

        await VerifyGateAsync(root, manifestPath, cancellationToken);
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(root, manifestPath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var strict = StrictJson.Parse(bytes);
        ValidateRepositoryManifest(strict.RootElement);

        var rootNode = JsonNode.Parse(strict.RootElement.GetRawText())!.AsObject();
        var entries = rootNode["files"]!.AsArray();
        var manifestRelative = path[".agents/".Length..];
        var entry = entries.OfType<JsonObject>().SingleOrDefault(node => string.Equals(node["path"]?.GetValue<string>(), path, StringComparison.Ordinal) || string.Equals(node["path"]?.GetValue<string>(), manifestRelative, StringComparison.Ordinal));
        if (entry is null)
        {
            if (!metadata.RequiresIntegrityProtection)
            {
                return null;
            }

            entry = new JsonObject
            {
                ["file_id"] = metadata.FileId,
                ["path"] = manifestRelative,
                ["sha256"] = CanonicalHash.Text(content).ToUpperInvariant(),
                ["revision"] = 1,
                ["change_origin"] = "agent-authorized",
                ["changed_at"] = DateTimeOffset.UtcNow.ToString("O")
            };
            entries.Add(entry);
        }
        else
        {
            entry["sha256"] = CanonicalHash.Text(content).ToUpperInvariant();
            entry["revision"] = (entry["revision"]?.GetValue<int>() ?? 0) + 1;
            entry["change_origin"] = "agent-authorized";
            entry["changed_at"] = DateTimeOffset.UtcNow.ToString("O");
        }
        var ordered = entries.OfType<JsonObject>()
            .OrderBy(item => item["path"]?.GetValue<string>(), StringComparer.Ordinal)
            .Select(item => (JsonNode)item.DeepClone())
            .ToArray();
        entries.Clear();
        foreach (var item in ordered) { entries.Add(item); }
        if (rootNode["revision"] is not null)
        {
            rootNode["revision"] = (rootNode["revision"]?.GetValue<int>() ?? 0) + 1;
        }

        if (rootNode["updated_at"] is not null)
        {
            rootNode["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
        }

        return new BranchManifestUpdate(rootNode.ToJsonString(IndentedJson) + "\n", CanonicalHash.Raw(bytes));
    }

    private static bool ValidateRepositoryJsonSchema(string path, JsonElement root)
    {
        if (path == ".agents/content-integrity.json")
        {
            ValidateRepositoryManifest(root);
            return true;
        }

        if (path == ".agents/project.json")
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "schema_version", "project_id", "project_slug", "title", "storage_mode", "git_state", "updated_at"
            };
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
                !root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
                !root.TryGetProperty("project_id", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out _) ||
                !root.TryGetProperty("project_slug", out var slug) || slug.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(slug.GetString()) ||
                !root.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(title.GetString()) ||
                !root.TryGetProperty("storage_mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != "git-backed" ||
                !root.TryGetProperty("git_state", out var state) || state.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("updated_at", out var updated) || updated.ValueKind != JsonValueKind.String)
            {
                throw new VaultAuthoringException("REPOSITORY_METADATA_INVALID", "Repository project metadata does not match schema version 1.");
            }
            return true;
        }

        return false;
    }

    private static void ValidateRepositoryManifest(JsonElement root)
    {
        var allowedRoot = new HashSet<string>(StringComparer.Ordinal) { "schema_version", "project_id", "revision", "updated_at", "files" };
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => !allowedRoot.Contains(property.Name)) ||
            !root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
            !root.TryGetProperty("project_id", out var projectId) || projectId.ValueKind != JsonValueKind.String || !Guid.TryParse(projectId.GetString(), out _) ||
            !root.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number || revision.GetInt32() < 1 ||
            !root.TryGetProperty("updated_at", out var updated) || updated.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new VaultAuthoringException("REPOSITORY_MANIFEST_INVALID", "The current-branch .agents integrity manifest does not match schema version 1.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in files.EnumerateArray())
        {
            var allowedEntry = new HashSet<string>(StringComparer.Ordinal) { "file_id", "path", "sha256", "revision", "change_origin", "changed_at" };
            if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Any(property => !allowedEntry.Contains(property.Name)) ||
                !entry.TryGetProperty("file_id", out var fileId) || fileId.ValueKind != JsonValueKind.String || !Guid.TryParse(fileId.GetString(), out _) ||
                !entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()) || !paths.Add(path.GetString()!) ||
                !entry.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String || sha.GetString()?.Length != 64 || !sha.GetString()!.All(char.IsAsciiHexDigit) ||
                !entry.TryGetProperty("revision", out var entryRevision) || entryRevision.ValueKind != JsonValueKind.Number || entryRevision.GetInt32() < 1 ||
                !entry.TryGetProperty("change_origin", out var origin) || origin.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(origin.GetString()) ||
                !entry.TryGetProperty("changed_at", out var changed) || changed.ValueKind != JsonValueKind.String)
            {
                throw new VaultAuthoringException("REPOSITORY_MANIFEST_INVALID", "The current-branch .agents integrity manifest contains an invalid or duplicate file entry.");
            }
        }
    }

    private static async Task<WriteResult> WriteProtectedPairAsync(string root, RepositoryAgentsWriteRequest request, BranchManifestUpdate manifest, CancellationToken cancellationToken)
    {
        var gitDirectory = Path.Combine(root, ".git");
        if (!Directory.Exists(gitDirectory) || File.GetAttributes(gitDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new VaultAuthoringException("REPOSITORY_LOCKS_UNSUPPORTED", "The repository has no safe local .git directory for protected .agents writer locks.");
        }

        var stateRoot = Path.Combine(gitDirectory, "rule-vault-locks");
        var lockTargets = new[] { request.RelativePath, ".agents/content-integrity.json" }.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var locks = new List<ProtectedLock>();
        await using var repositoryTransaction = await ProtectedLock.AcquireAsync(
            stateRoot,
            RepositoryWriterTarget,
            waitTimeout: VaultIntegrityTransaction.WriterWaitTimeout,
            cancellationToken: cancellationToken);
        try
        {
            foreach (var lockTarget in lockTargets)
            {
                locks.Add(await ProtectedLock.AcquireAsync(stateRoot, lockTarget, waitTimeout: VaultIntegrityTransaction.WriterWaitTimeout, cancellationToken: cancellationToken));
            }

            await VerifyExpectedRawAsync(root, request.RelativePath, request.ExpectedRawSha256, cancellationToken);
            await VerifyExpectedRawAsync(root, ".agents/content-integrity.json", manifest.RawSha256, cancellationToken);
            var journal = new TransactionJournal(1, Guid.NewGuid().ToString("D"), locks[0].WriterId, TransactionState.Prepared, DateTimeOffset.UtcNow,
                [new TransactionTarget(request.RelativePath, request.ExpectedRawSha256 ?? "MISSING", CanonicalHash.Raw(Encoding.UTF8.GetBytes(request.Content))), new TransactionTarget(".agents/content-integrity.json", manifest.RawSha256, CanonicalHash.Raw(Encoding.UTF8.GetBytes(manifest.Content)))]);
            await JournalStore.WriteAsync(stateRoot, journal, cancellationToken);
            var write = await TrustedWriter.WriteTextAsync(root, request.RelativePath, request.Content, request.ExpectedRawSha256, SafePathProfile.PrivateConfig, cancellationToken);
            await TrustedWriter.WriteTextAsync(root, ".agents/content-integrity.json", manifest.Content, manifest.RawSha256, SafePathProfile.PrivateConfig, cancellationToken);
            await JournalStore.WriteAsync(stateRoot, journal with { State = TransactionState.Committed }, cancellationToken);
            return write;
        }
        finally
        {
            foreach (var item in locks.AsEnumerable().Reverse())
            {
                await item.DisposeAsync();
            }
        }
    }

    private static async Task VerifyExpectedRawAsync(string root, string relativePath, string? expectedRawSha256, CancellationToken cancellationToken)
    {
        var target = SafePath.ValidateRelative(root, relativePath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(target);
        var exists = File.Exists(target.FullPath!);
        if (!exists)
        {
            if (expectedRawSha256 is not null)
            {
                throw new WriteConflictException($"Write precondition failed for '{relativePath}'.");
            }
            return;
        }

        if (expectedRawSha256 is null)
        {
            throw new WriteConflictException($"Write precondition failed for '{relativePath}'.");
        }
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(root, relativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        if (!string.Equals(CanonicalHash.Raw(bytes), expectedRawSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new WriteConflictException($"Write precondition failed for '{relativePath}'.");
        }
    }

    private static async Task<RepositoryAgentsReadResult> ReadStableAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        var stateRoot = RepositoryStateRoot(root);
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            while (ProtectedLock.IsHeld(stateRoot, RepositoryWriterTarget))
            {
                if (DateTimeOffset.UtcNow - started >= VaultIntegrityTransaction.ReaderWaitTimeout)
                {
                    throw new WriteConflictException("A protected repository .agents update is still in progress. Retry after that update completes.");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 21)), cancellationToken);
            }

            var before = await RepositoryManifestHashAsync(root, cancellationToken);
            RepositoryAgentsReadResult? result = null;
            Exception? failure = null;
            try
            {
                result = await ReadCoreAsync(root, relativePath, cancellationToken);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            var after = await RepositoryManifestHashAsync(root, cancellationToken);
            var changed = !string.Equals(before, after, StringComparison.OrdinalIgnoreCase) || ProtectedLock.IsHeld(stateRoot, RepositoryWriterTarget);
            if (changed && DateTimeOffset.UtcNow - started < VaultIntegrityTransaction.ReaderWaitTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 21)), cancellationToken);
                continue;
            }
            if (changed)
            {
                throw new WriteConflictException("A protected repository .agents update did not produce a stable readable snapshot. Retry after the update completes.");
            }
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            return result!;
        }
    }

    private static async Task<string?> RepositoryManifestHashAsync(string root, CancellationToken cancellationToken)
    {
        const string manifestPath = ".agents/content-integrity.json";
        var target = SafePath.ValidateRelative(root, manifestPath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(target);
        return File.Exists(target.FullPath!)
            ? CanonicalHash.Raw(await TrustedFileSystem.ReadAllBytesAsync(root, manifestPath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken))
            : null;
    }

    private static string RepositoryStateRoot(string root)
    {
        var gitDirectory = Path.Combine(root, ".git");
        if (!Directory.Exists(gitDirectory) || File.GetAttributes(gitDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new VaultAuthoringException("REPOSITORY_LOCKS_UNSUPPORTED", "The repository has no safe local .git directory for repository transaction state.");
        }
        return Path.Combine(gitDirectory, "rule-vault-locks");
    }

    private static async Task<string> RunGitAsync(string root, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git", $"-C \"{root}\" {arguments}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new VaultAuthoringException("REPOSITORY_GATE_FAILED", string.IsNullOrWhiteSpace(error) ? "Git repository verification failed." : error.Trim());
        }

        return output;
    }

    private static string QuoteForGitPath(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
