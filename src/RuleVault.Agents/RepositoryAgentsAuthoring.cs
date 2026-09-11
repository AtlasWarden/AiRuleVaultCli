using System.Diagnostics;
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

public static class RepositoryAgentsAuthoring
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    public static async Task<RepositoryAgentsWriteResult> WriteAsync(RepositoryAgentsWriteRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePath(request.RelativePath);
        var repositoryRoot = await VerifyRepositoryRootAsync(request.RepositoryRoot, cancellationToken);
        await VerifyGateAsync(repositoryRoot, request.RelativePath, cancellationToken);
        var target = SafePath.ValidateRelative(repositoryRoot, request.RelativePath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(target);
        var exists = File.Exists(target.FullPath!);
        if (exists && string.IsNullOrWhiteSpace(request.ExpectedRawSha256))
        {
            throw new VaultAuthoringException("REPOSITORY_WRITE_PRECONDITION_REQUIRED", "Read the repository .agents file through the adapter and supply its current raw SHA-256 before writing.");
        }

        var protectedUpdate = await PrepareBranchManifestUpdateAsync(repositoryRoot, request.RelativePath, request.Content, cancellationToken);
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
        ValidatePath(relativePath);
        var root = await VerifyRepositoryRootAsync(repositoryRoot, cancellationToken);
        await VerifyGateAsync(root, relativePath, cancellationToken);
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(root, relativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var content = CanonicalHash.DecodeUtf8(bytes);
        var metadataStatus = "not-present";
        if (content.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end < 0)
            {
                metadataStatus = "invalid-data-only";
            }
            else
            {
                try { _ = StrictYaml.ParseFrontmatter(content[4..end]); metadataStatus = "deterministic"; }
                catch (StorageFormatException) { metadataStatus = "invalid-data-only"; }
            }
        }
        return new RepositoryAgentsReadResult(relativePath, content, CanonicalHash.Raw(bytes), metadataStatus, "data-only-never-executed");
    }

    private static void ValidatePath(string path)
    {
        var result = SafePath.ValidateRelative(Path.GetTempPath(), path, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(result);
        if (!path.StartsWith(".agents/", StringComparison.Ordinal) || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path is ".agents/index.md" or ".agents/content-integrity.json")
        {
            throw new VaultAuthoringException("REPOSITORY_AGENTS_PATH_DENIED", "The repository adapter writes ordinary .agents Markdown only. Protected .agents indexes and integrity metadata require their dedicated lifecycle operation.");
        }
    }

    private static async Task<string> VerifyRepositoryRootAsync(string root, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new VaultAuthoringException("REPOSITORY_ROOT_INVALID", "Repository root must be absolute.");
        }

        var reported = (await RunGitAsync(root, "rev-parse --show-toplevel", cancellationToken)).Trim();
        var expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actual = Path.GetFullPath(reported).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

    private static async Task<BranchManifestUpdate?> PrepareBranchManifestUpdateAsync(string root, string path, string content, CancellationToken cancellationToken)
    {
        const string manifestPath = ".agents/content-integrity.json";
        var manifestTarget = SafePath.ValidateRelative(root, manifestPath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(manifestTarget);
        if (!File.Exists(manifestTarget.FullPath!))
        {
            return null;
        }

        await VerifyGateAsync(root, manifestPath, cancellationToken);
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(root, manifestPath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var strict = StrictJson.Parse(bytes);
        if (strict.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || !strict.RootElement.TryGetProperty("files", out var files) || files.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new VaultAuthoringException("REPOSITORY_MANIFEST_INVALID", "The current branch .agents integrity manifest has no files array.");
        }

        var rootNode = JsonNode.Parse(strict.RootElement.GetRawText())!.AsObject();
        var entries = rootNode["files"]!.AsArray();
        var manifestRelative = path[".agents/".Length..];
        var entry = entries.OfType<JsonObject>().SingleOrDefault(node => string.Equals(node["path"]?.GetValue<string>(), path, StringComparison.Ordinal) || string.Equals(node["path"]?.GetValue<string>(), manifestRelative, StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        entry["sha256"] = CanonicalHash.Text(content).ToUpperInvariant();
        entry["revision"] = (entry["revision"]?.GetValue<int>() ?? 0) + 1;
        entry["change_origin"] = "agent-authorized";
        entry["changed_at"] = DateTimeOffset.UtcNow.ToString("O");
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
        try
        {
            foreach (var lockTarget in lockTargets)
            {
                locks.Add(await ProtectedLock.AcquireAsync(stateRoot, lockTarget, cancellationToken: cancellationToken));
            }

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
