using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuleVault.Storage;

namespace RuleVault.Agents;

/// <summary>
/// The mediated authoring surface for private-vault content.  It deliberately
/// does not infer policy or write arbitrary host paths: an agent supplies the
/// exact content, optimistic-concurrency hash, and (for a protected file) the
/// explicit configuration root that anchors the integrity manifest.
/// </summary>
public sealed record VaultWriteRequest(string VaultRoot, string? ConfigRoot, string RelativePath, string Content, string? ExpectedRawSha256);
public sealed record VaultReadResult(string RelativePath, string Content, string RawSha256, bool IsIntegrityProtected);
public sealed record VaultWriteResult(string RelativePath, string RawSha256, bool Created, bool IntegrityUpdated, IReadOnlyList<string> LinkTargetsChanged);
public sealed record InboundLinkInfo(string RelativePath, string FileId, bool HasInboundLinks, IReadOnlyList<string> InboundSources);
public sealed record DailyInspection(string Project, string ActiveRelativePath, string ActiveRawSha256, string ActiveContent, DateOnly ActiveDate, bool RolloverRequired);
public sealed record ProjectCreateRequest(string VaultRoot, string ConfigRoot, string Slug, string Title, string? Purpose, string? EndGoal);
public sealed record ProjectCreateResult(string Project, IReadOnlyList<string> CreatedPaths, string RootIndexRawSha256);

public sealed class VaultAuthoringException : Exception
{
    public VaultAuthoringException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

public static class VaultAuthoring
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly Regex Link = new(@"(?<!\!)\[[^\]]*\]\((?<target>[^\s\)#]+)(?:#[^)\s]*)?(?:\s+[^)]*)?\)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FileId = new("^file_id:\\s*[\\\"']?(?<id>[0-9a-fA-F-]{36})[\\\"']?\\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex InboundFlag = new(@"^hasInboundLinks:\s*(true|false)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<VaultReadResult> ReadAsync(string vaultRoot, string relativePath, CancellationToken cancellationToken = default)
    {
        await VerifyVaultAsync(vaultRoot, cancellationToken);
        EnsureAuthorablePath(relativePath);
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, relativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        return new VaultReadResult(relativePath, CanonicalHash.DecodeUtf8(bytes), CanonicalHash.Raw(bytes), await IsProtectedAsync(vaultRoot, relativePath, cancellationToken));
    }

    public static async Task<VaultWriteResult> WriteAsync(VaultWriteRequest request, CancellationToken cancellationToken = default)
    {
        await VerifyVaultAsync(request.VaultRoot, cancellationToken);
        EnsureAuthorablePath(request.RelativePath);
        ValidateMarkdown(request.RelativePath, request.Content);

        var target = SafePath.ValidateRelative(request.VaultRoot, request.RelativePath, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(target);
        var exists = File.Exists(target.FullPath!);
        var previous = exists ? await TrustedFileSystem.ReadTextAsync(request.VaultRoot, request.RelativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken) : null;
        var previousRaw = exists ? CanonicalHash.Raw(await TrustedFileSystem.ReadAllBytesAsync(request.VaultRoot, request.RelativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken)) : "MISSING";
        if (!string.Equals(previousRaw, request.ExpectedRawSha256, StringComparison.OrdinalIgnoreCase) && (exists || request.ExpectedRawSha256 is not null))
        {
            throw new VaultAuthoringException("WRITE_PRECONDITION_FAILED", $"Read '{request.RelativePath}' again and supply its current raw SHA-256 before writing.");
        }

        var oldLinks = previous is null ? new HashSet<string>(StringComparer.Ordinal) : ResolveManagedLinks(request.VaultRoot, request.RelativePath, previous);
        var newLinks = ResolveManagedLinks(request.VaultRoot, request.RelativePath, request.Content);
        var changedLinks = oldLinks.Where(link => !newLinks.Contains(link)).Concat(newLinks.Where(link => !oldLinks.Contains(link))).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var protectedFile = await IsProtectedAsync(request.VaultRoot, request.RelativePath, cancellationToken);
        if (protectedFile && string.IsNullOrWhiteSpace(request.ConfigRoot))
        {
            throw new VaultAuthoringException("PROTECTED_WRITE_CONFIG_REQUIRED", "Writing integrity-protected content requires --config-root so the manifest and registry anchor can be updated together.");
        }

        var writes = new List<Mutation> { new(request.VaultRoot, request.RelativePath, request.Content, previousRaw) };
        foreach (var linkTarget in changedLinks)
        {
            var inbound = await ReadInboundAsync(request.VaultRoot, linkTarget, cancellationToken);
            if (newLinks.Contains(linkTarget))
            {
                inbound.Add(request.RelativePath);
            }
            else
            {
                inbound.Remove(request.RelativePath);
            }

            var targetContent = await TrustedFileSystem.ReadTextAsync(request.VaultRoot, linkTarget, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            var targetRaw = CanonicalHash.Raw(await TrustedFileSystem.ReadAllBytesAsync(request.VaultRoot, linkTarget, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken));
            var updatedTarget = SetInboundFlag(targetContent, inbound.Count > 0);
            writes.Add(new Mutation(request.VaultRoot, linkTarget, updatedTarget, targetRaw));
            var id = GetFileId(targetContent, linkTarget);
            var sidecar = $".vault-system/references/{id}.json";
            var sidecarTarget = SafePath.ValidateRelative(request.VaultRoot, sidecar, SafePathProfile.PrivateConfig);
            TrustedFileSystem.EnsureSupported(sidecarTarget);
            var sidecarBefore = File.Exists(sidecarTarget.FullPath!)
                ? CanonicalHash.Raw(await TrustedFileSystem.ReadAllBytesAsync(request.VaultRoot, sidecar, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken)) : "MISSING";
            // Empty sidecars are retained as a recovery record only while this transaction is active; a later link repair removes them.
            var sidecarContent = JsonSerializer.Serialize(new { schema_version = 1, file_id = id, inbound = inbound.OrderBy(value => value, StringComparer.Ordinal).ToArray() }, IndentedJson) + "\n";
            writes.Add(new Mutation(request.VaultRoot, sidecar, sidecarContent, sidecarBefore));
        }

        if (protectedFile)
        {
            var manifestMutation = await BuildManifestAndRegistryMutationsAsync(request.VaultRoot, request.ConfigRoot!, request.RelativePath, request.Content, cancellationToken);
            writes.AddRange(manifestMutation);
        }

        await ApplyBatchAsync(request.VaultRoot, writes, cancellationToken);
        var afterBytes = await TrustedFileSystem.ReadAllBytesAsync(request.VaultRoot, request.RelativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        return new VaultWriteResult(request.RelativePath, CanonicalHash.Raw(afterBytes), !exists, protectedFile, changedLinks);
    }

    public static async Task<InboundLinkInfo> InspectInboundLinksAsync(string vaultRoot, string relativePath, CancellationToken cancellationToken = default)
    {
        await VerifyVaultAsync(vaultRoot, cancellationToken);
        EnsureAuthorablePath(relativePath);
        var content = await TrustedFileSystem.ReadTextAsync(vaultRoot, relativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var id = GetFileId(content, relativePath);
        var inbound = await ReadInboundAsync(vaultRoot, relativePath, cancellationToken);
        var hasInbound = InboundFlag.Match(content) is { Success: true } match && string.Equals(match.Groups[1].Value, "true", StringComparison.Ordinal);
        if (hasInbound != (inbound.Count > 0))
        {
            throw new VaultAuthoringException("LINK_METADATA_INCONSISTENT", $"'{relativePath}' has inconsistent hasInboundLinks metadata; repair it before move or deletion.");
        }

        return new InboundLinkInfo(relativePath, id, hasInbound, inbound.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    public static async Task<IReadOnlyList<string>> DeleteAsync(string vaultRoot, string relativePath, string expectedRawSha256, CancellationToken cancellationToken = default)
    {
        await VerifyVaultAsync(vaultRoot, cancellationToken);
        EnsureAuthorablePath(relativePath);
        if (await IsProtectedAsync(vaultRoot, relativePath, cancellationToken))
        {
            throw new VaultAuthoringException("PROTECTED_DELETE_REQUIRES_LIFECYCLE", "Protected runtime content cannot be deleted through agent authoring.");
        }

        var info = await InspectInboundLinksAsync(vaultRoot, relativePath, cancellationToken);
        if (info.InboundSources.Count > 0)
        {
            throw new VaultAuthoringException("INBOUND_LINKS_REQUIRE_UPDATE", $"'{relativePath}' has inbound links from: {string.Join(", ", info.InboundSources)}. Update or remove those links before deletion.");
        }

        var bytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, relativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        if (!string.Equals(CanonicalHash.Raw(bytes), expectedRawSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultAuthoringException("WRITE_PRECONDITION_FAILED", $"Read '{relativePath}' again before deletion.");
        }

        var writes = new List<Mutation> { new(vaultRoot, relativePath, null, expectedRawSha256) };
        var sidecar = $".vault-system/references/{info.FileId}.json";
        var sidecarTarget = SafePath.ValidateRelative(vaultRoot, sidecar, SafePathProfile.PrivateConfig); TrustedFileSystem.EnsureSupported(sidecarTarget);
        if (File.Exists(sidecarTarget.FullPath!))
        {
            writes.Add(new Mutation(vaultRoot, sidecar, null, CanonicalHash.Raw(await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, sidecar, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken))));
        }

        await ApplyBatchAsync(vaultRoot, writes, cancellationToken);
        return writes.Select(write => write.Path).ToArray();
    }

    public static async Task<ProjectCreateResult> CreateProjectAsync(ProjectCreateRequest request, CancellationToken cancellationToken = default)
    {
        await VerifyVaultAsync(request.VaultRoot, cancellationToken);
        if (!Regex.IsMatch(request.Slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant))
        {
            throw new VaultAuthoringException("PROJECT_SLUG_INVALID", "Project slug must use lowercase letters, digits, and single hyphens.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new VaultAuthoringException("PROJECT_TITLE_REQUIRED", "Project title is required.");
        }

        var rootIndex = await ReadAsync(request.VaultRoot, "index.md", cancellationToken);
        if (!rootIndex.IsIntegrityProtected)
        {
            throw new VaultAuthoringException("ROOT_INDEX_NOT_PROTECTED", "Project creation requires a verified protected root index.");
        }

        if (!rootIndex.Content.Contains("## Projects", StringComparison.Ordinal))
        {
            throw new VaultAuthoringException("ROOT_INDEX_STRUCTURE_UNKNOWN", "Root index has no Projects section; preserve it and add the project through a reviewed vault write.");
        }

        var basePath = $"projects/{request.Slug}";
        var sample = $"{basePath}/index.md";
        var sampleTarget = SafePath.ValidateRelative(request.VaultRoot, sample, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(sampleTarget);
        if (File.Exists(sampleTarget.FullPath!))
        {
            throw new VaultAuthoringException("PROJECT_EXISTS", $"Project '{request.Slug}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var purpose = string.IsNullOrWhiteSpace(request.Purpose) ? "Unknown - user input required." : request.Purpose.Trim();
        var goal = string.IsNullOrWhiteSpace(request.EndGoal) ? "Unknown - user input required." : request.EndGoal.Trim();
        var date = DateOnly.FromDateTime(DateTime.Now);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{basePath}/index.md"] = ProjectRouter(request.Slug, request.Title, purpose),
            [$"{basePath}/rules/index.md"] = ManagedDocument($"{request.Title} Rules", request.Slug, "index", $"# {request.Title} Rules\n\n## Project-Wide Always Load\n\nNone.\n\n## Rule Categories\n\nCreate a broad subject category before adding a rule.", now),
            [$"{basePath}/context/index.md"] = ManagedDocument($"{request.Title} Context", request.Slug, "index", $"# {request.Title} Context\n\n## Project-Wide Always Load\n\n- [Current State](./current-state.md)  - Current purpose, goal, verified state, and open work.\n\n## Conditional and Historical Context\n\nNone.", now),
            [$"{basePath}/context/current-state.md"] = ManagedDocument($"{request.Title} Current State", request.Slug, "context", $"# {request.Title} Current State\n\n## Purpose\n\n{purpose}\n\n## End Goal\n\n{goal}\n\n## Current Verified State\n\nProject scaffold created; user input is required where marked.\n", now),
            [$"{basePath}/daily/index.md"] = DailyIndex(request.Slug, request.Title, date, now),
            [$"{basePath}/daily/active/{date:yyyy-MM-dd}.md"] = DailyNote(request.Slug, request.Title, date, now),
            [$"{basePath}/daily/archive/index.md"] = ManagedDocument($"{request.Title} Daily Archive", request.Slug, "index", "# Daily Archive\n\nNo archived daily notes.", now)
        };
        var projectLink = $"- [{request.Title}](./projects/{request.Slug}/index.md)  -  Active project.\n";
        var rootUpdate = rootIndex.Content.Replace("## Projects", "## Projects\n\n" + projectLink, StringComparison.Ordinal);
        var writes = files.Select(pair => new Mutation(request.VaultRoot, pair.Key, pair.Value, "MISSING")).ToList();
        writes.Add(new Mutation(request.VaultRoot, "index.md", rootUpdate, rootIndex.RawSha256));
        writes.AddRange(await BuildManifestAndRegistryMutationsAsync(request.VaultRoot, request.ConfigRoot, "index.md", rootUpdate, cancellationToken));
        await ApplyBatchAsync(request.VaultRoot, writes, cancellationToken);
        var current = await TrustedFileSystem.ReadAllBytesAsync(request.VaultRoot, "index.md", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        return new ProjectCreateResult(request.Slug, files.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(), CanonicalHash.Raw(current));
    }

    public static async Task<DailyInspection> InspectDailyAsync(string vaultRoot, string project, DateOnly localDate, CancellationToken cancellationToken = default)
    {
        await VerifyVaultAsync(vaultRoot, cancellationToken);
        var dailyBase = DailyBase(project);
        var pointer = $"{dailyBase}/daily/index.md";
        var pointerTarget = SafePath.ValidateRelative(vaultRoot, pointer, SafePathProfile.PrivateConfig);
        TrustedFileSystem.EnsureSupported(pointerTarget);
        if (string.Equals(project, "global", StringComparison.Ordinal) && !File.Exists(pointerTarget.FullPath!))
        {
            await InitializeGlobalDailyAsync(vaultRoot, localDate, cancellationToken);
        }
        var content = await TrustedFileSystem.ReadTextAsync(vaultRoot, pointer, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var match = Regex.Match(content, @"\./active/(?<date>\d{4}-\d{2}-\d{2})\.md", RegexOptions.CultureInvariant);
        if (!match.Success || !DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", out var activeDate))
        {
            throw new VaultAuthoringException("DAILY_POINTER_INVALID", $"Daily pointer '{pointer}' does not identify one active ISO-date note.");
        }

        var active = $"{dailyBase}/daily/active/{activeDate:yyyy-MM-dd}.md";
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, active, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        return new DailyInspection(project, active, CanonicalHash.Raw(bytes), CanonicalHash.DecodeUtf8(bytes), activeDate, activeDate != localDate);
    }

    public static async Task<IReadOnlyList<string>> RolloverDailyAsync(string vaultRoot, string project, DateOnly localDate, string expectedActiveRawSha256, bool promotionsComplete, CancellationToken cancellationToken = default)
    {
        if (!promotionsComplete)
        {
            throw new VaultAuthoringException("PROMOTIONS_CONFIRMATION_REQUIRED", "Read the entire previous daily note, make explicit rule/context/current-state promotions, then pass --promotions-complete true.");
        }

        var daily = await InspectDailyAsync(vaultRoot, project, localDate, cancellationToken);
        if (!daily.RolloverRequired)
        {
            return [];
        }

        if (!string.Equals(daily.ActiveRawSha256, expectedActiveRawSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultAuthoringException("DAILY_NOTE_CHANGED", "The active daily note changed; reread it and complete promotions against the current content.");
        }

        var title = project;
        var dailyBase = DailyBase(project);
        var archive = $"{dailyBase}/daily/archive/{daily.ActiveDate:yyyy}/{daily.ActiveDate:MMM}/{daily.ActiveDate:dd}.md";
        var archiveIndex = $"{dailyBase}/daily/archive/index.md";
        var yearIndex = $"{dailyBase}/daily/archive/{daily.ActiveDate:yyyy}/index.md";
        var monthIndex = $"{dailyBase}/daily/archive/{daily.ActiveDate:yyyy}/{daily.ActiveDate:MMM}/index.md";
        var pointer = $"{dailyBase}/daily/index.md";
        var pointerContent = await TrustedFileSystem.ReadTextAsync(vaultRoot, pointer, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var pointerRaw = CanonicalHash.Raw(await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, pointer, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken));
        var newPointer = new Regex(@"\./active/\d{4}-\d{2}-\d{2}\.md", RegexOptions.CultureInvariant).Replace(pointerContent, $"./active/{localDate:yyyy-MM-dd}.md", 1);
        var archiveRaw = await RawOrMissingAsync(vaultRoot, archiveIndex, cancellationToken);
        var yearRaw = await RawOrMissingAsync(vaultRoot, yearIndex, cancellationToken);
        var monthRaw = await RawOrMissingAsync(vaultRoot, monthIndex, cancellationToken);
        var archiveContent = archiveRaw == "MISSING" ? ManagedDocument($"{project} Daily Archive", project, "index", "# Daily Archive", DateTimeOffset.UtcNow) : EnsureBullet(await TryReadAsync(vaultRoot, archiveIndex, cancellationToken), $"- [{daily.ActiveDate:yyyy}](./{daily.ActiveDate:yyyy}/index.md)");
        var yearContent = yearRaw == "MISSING" ? ManagedDocument($"{project} {daily.ActiveDate:yyyy} Daily Archive", project, "index", $"# {daily.ActiveDate:yyyy}", DateTimeOffset.UtcNow) : EnsureBullet(await TryReadAsync(vaultRoot, yearIndex, cancellationToken), $"- [{daily.ActiveDate:MMM}](./{daily.ActiveDate:MMM}/index.md)");
        var monthContent = monthRaw == "MISSING" ? ManagedDocument($"{project} {daily.ActiveDate:MMM} {daily.ActiveDate:yyyy} Daily Archive", project, "index", $"# {daily.ActiveDate:MMM} {daily.ActiveDate:yyyy}", DateTimeOffset.UtcNow) : EnsureBullet(await TryReadAsync(vaultRoot, monthIndex, cancellationToken), $"- [{daily.ActiveDate:dd}](./{daily.ActiveDate:dd}.md)  - Archived daily handoff.");
        archiveContent = EnsureBullet(archiveContent, $"- [{daily.ActiveDate:yyyy}](./{daily.ActiveDate:yyyy}/index.md)");
        yearContent = EnsureBullet(yearContent, $"- [{daily.ActiveDate:MMM}](./{daily.ActiveDate:MMM}/index.md)");
        monthContent = EnsureBullet(monthContent, $"- [{daily.ActiveDate:dd}](./{daily.ActiveDate:dd}.md)  - Archived daily handoff.");
        var writes = new List<Mutation>
        {
            new(vaultRoot, archive, daily.ActiveContent, "MISSING"),
            new(vaultRoot, $"{dailyBase}/daily/active/{localDate:yyyy-MM-dd}.md", DailyNote(project, title, localDate, DateTimeOffset.UtcNow), "MISSING"),
            new(vaultRoot, pointer, newPointer, pointerRaw),
            new(vaultRoot, archiveIndex, archiveContent, archiveRaw),
            new(vaultRoot, yearIndex, yearContent, yearRaw),
            new(vaultRoot, monthIndex, monthContent, monthRaw),
            new(vaultRoot, daily.ActiveRelativePath, null, daily.ActiveRawSha256)
        };
        await ApplyBatchAsync(vaultRoot, writes, cancellationToken);
        return writes.Select(write => write.Path).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private sealed record Mutation(string Root, string Path, string? Content, string BeforeRawSha256);

    private static async Task ApplyBatchAsync(string vaultRoot, IReadOnlyList<Mutation> mutations, CancellationToken cancellationToken)
    {
        var duplicate = mutations.GroupBy(m => $"{Path.GetFullPath(m.Root)}::{m.Path}", StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new VaultAuthoringException("MUTATION_DUPLICATE_TARGET", $"One operation tried to change '{duplicate.Key}' more than once.");
        }

        var locks = new List<ProtectedLock>();
        var stateRoot = Path.Combine(vaultRoot, ".vault-system");
        try
        {
            foreach (var target in mutations.OrderBy(m => m.Root, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Path, StringComparer.Ordinal))
            {
                locks.Add(await ProtectedLock.AcquireAsync(stateRoot, $"{Path.GetFullPath(target.Root)}::{target.Path}", cancellationToken: cancellationToken));
            }

            var transaction = new TransactionJournal(1, Guid.NewGuid().ToString("D"), locks[0].WriterId, TransactionState.Prepared, DateTimeOffset.UtcNow,
                mutations.Select(m => new TransactionTarget($"{Path.GetFullPath(m.Root)}::{m.Path}", m.BeforeRawSha256, m.Content is null ? "DELETE" : CanonicalHash.Raw(Encoding.UTF8.GetBytes(m.Content)))).ToArray());
            await JournalStore.WriteAsync(stateRoot, transaction, cancellationToken);
            foreach (var mutation in mutations)
            {
                if (mutation.Content is null)
                {
                    await DeleteFileAsync(mutation.Root, mutation.Path, mutation.BeforeRawSha256, cancellationToken);
                }
                else
                {
                    await TrustedWriter.WriteTextAsync(mutation.Root, mutation.Path, mutation.Content, mutation.BeforeRawSha256 == "MISSING" ? null : mutation.BeforeRawSha256, SafePathProfile.PrivateConfig, cancellationToken);
                }
            }
            await JournalStore.WriteAsync(stateRoot, transaction with { State = TransactionState.Committed }, cancellationToken);
        }
        finally
        {
            foreach (var item in locks.AsEnumerable().Reverse())
            {
                await item.DisposeAsync();
            }
        }
    }

    private static async Task DeleteFileAsync(string root, string path, string expected, CancellationToken cancellationToken)
    {
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(root, path, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        if (!string.Equals(CanonicalHash.Raw(bytes), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new WriteConflictException($"Write precondition failed for '{path}'.");
        }

        var target = SafePath.ValidateRelative(root, path, SafePathProfile.PrivateConfig); TrustedFileSystem.EnsureSupported(target);
        File.Delete(target.FullPath!);
    }

    private static async Task<IReadOnlyList<Mutation>> BuildManifestAndRegistryMutationsAsync(string vaultRoot, string configRoot, string path, string content, CancellationToken cancellationToken)
    {
        var manifestPath = ".vault-system/content-integrity.json";
        var manifestBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, manifestPath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var strict = StrictJson.Parse(manifestBytes);
        var root = JsonNode.Parse(strict.RootElement.GetRawText())!.AsObject();
        var files = root["files"]?.AsArray() ?? throw new VaultAuthoringException("INTEGRITY_MANIFEST_INVALID", "Integrity manifest has no files array.");
        var entry = files.OfType<JsonObject>().SingleOrDefault(node => string.Equals(node["path"]?.GetValue<string>(), path, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new VaultAuthoringException("INTEGRITY_ENTRY_MISSING", $"Protected path '{path}' is absent from the integrity manifest.");
        }

        entry["sha256"] = CanonicalHash.Text(content).ToUpperInvariant();
        entry["revision"] = (entry["revision"]?.GetValue<int>() ?? 0) + 1;
        entry["change_origin"] = "agent";
        entry["changed_at"] = DateTimeOffset.UtcNow.ToString("O");
        root["revision"] = (root["revision"]?.GetValue<int>() ?? 0) + 1;
        root["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
        var manifest = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var vaultId = root["vault_id"]?.GetValue<string>() ?? throw new VaultAuthoringException("INTEGRITY_MANIFEST_INVALID", "Integrity manifest has no vault_id.");
        var registryPath = "vault-registry.json";
        var registryBytes = await TrustedFileSystem.ReadAllBytesAsync(configRoot, registryPath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var registryStrict = StrictJson.Parse(registryBytes);
        var registry = JsonNode.Parse(registryStrict.RootElement.GetRawText())!.AsObject();
        var entries = registry["vaults"]?.AsArray() ?? throw new VaultAuthoringException("REGISTRY_INVALID", "Registry has no vaults array.");
        var normalizedRoot = Path.GetFullPath(vaultRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var registryEntry = entries.OfType<JsonObject>().SingleOrDefault(node => string.Equals(node["vault_id"]?.GetValue<string>(), vaultId, StringComparison.OrdinalIgnoreCase));
        if (registryEntry is null || !string.Equals(Path.GetFullPath(registryEntry["vault_root"]?.GetValue<string>() ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultAuthoringException("REGISTRY_ROOT_MISMATCH", "Registry does not anchor this exact vault root; protected content was not changed.");
        }

        registryEntry["protected_content_manifest_sha256"] = CanonicalHash.Text(manifest).ToUpperInvariant();
        registryEntry["last_verified_at"] = DateTimeOffset.UtcNow.ToString("O");
        registry["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
        var registryText = registry.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        return [new Mutation(vaultRoot, manifestPath, manifest, CanonicalHash.Raw(manifestBytes)), new Mutation(configRoot, registryPath, registryText, CanonicalHash.Raw(registryBytes))];
    }

    private static async Task<bool> IsProtectedAsync(string root, string path, CancellationToken cancellationToken)
    {
        var manifest = await TrustedFileSystem.ReadAllBytesAsync(root, ".vault-system/content-integrity.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var document = StrictJson.Parse(manifest);
        return document.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.EnumerateArray().Any(entry => entry.TryGetProperty("path", out var value) && string.Equals(value.GetString(), path, StringComparison.Ordinal));
    }

    private static async Task VerifyVaultAsync(string vaultRoot, CancellationToken cancellationToken)
    {
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, ".vault-system/vault.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var metadata = StrictJson.Parse(bytes);
        if (!metadata.RootElement.TryGetProperty("vault_id", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out _))
        {
            throw new VaultAuthoringException("VAULT_METADATA_INVALID", "Selected vault metadata has no valid immutable vault_id.");
        }
    }

    private static void EnsureAuthorablePath(string path)
    {
        var validation = SafePath.ValidateRelative(Path.GetTempPath(), path, SafePathProfile.PrivateConfig); TrustedFileSystem.EnsureSupported(validation);
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.StartsWith(".vault-system/", StringComparison.Ordinal) || path.StartsWith(".agents/", StringComparison.Ordinal))
        {
            throw new VaultAuthoringException("VAULT_AUTHOR_PATH_DENIED", "Vault authoring supports managed Markdown outside .vault-system and repository .agents. Use the dedicated repository adapter for .agents.");
        }
    }

    private static void ValidateMarkdown(string path, string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new VaultAuthoringException("MANAGED_FRONTMATTER_REQUIRED", $"'{path}' must begin with deterministic managed frontmatter.");
        }

        var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal); if (end < 0)
        {
            throw new VaultAuthoringException("MANAGED_FRONTMATTER_REQUIRED", $"'{path}' has no closing managed frontmatter delimiter.");
        }

        var metadata = StrictYaml.ParseFrontmatter(content[4..end]);
        foreach (var key in new[] { "file_id", "title", "kind", "status", "aliases", "hasInboundLinks" })
        {
            if (!metadata.ContainsKey(key))
            {
                throw new VaultAuthoringException("MANAGED_FRONTMATTER_INVALID", $"'{path}' is missing frontmatter key '{key}'.");
            }
        }

        if (metadata["file_id"] is not string id || !Guid.TryParse(id, out _))
        {
            throw new VaultAuthoringException("MANAGED_FRONTMATTER_INVALID", $"'{path}' has no UUID file_id.");
        }

        if (metadata["hasInboundLinks"] is not bool)
        {
            throw new VaultAuthoringException("MANAGED_FRONTMATTER_INVALID", $"'{path}' has an invalid hasInboundLinks value.");
        }
    }

    private static HashSet<string> ResolveManagedLinks(string root, string source, string content)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Link.Matches(content))
        {
            var value = match.Groups["target"].Value;
            if (value.Contains(':') || value.StartsWith('/') || value.StartsWith('#'))
            {
                continue;
            }

            var candidate = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(source.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty, value.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || !relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(candidate))
            {
                throw new VaultAuthoringException("MANAGED_LINK_TARGET_MISSING", $"Managed link target '{value}' from '{source}' does not exist.");
            }

            EnsureAuthorablePath(relative); result.Add(relative);
        }
        return result;
    }

    private static async Task<HashSet<string>> ReadInboundAsync(string root, string target, CancellationToken cancellationToken)
    {
        var content = await TrustedFileSystem.ReadTextAsync(root, target, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var id = GetFileId(content, target); var sidecar = $".vault-system/references/{id}.json";
        var location = SafePath.ValidateRelative(root, sidecar, SafePathProfile.PrivateConfig); TrustedFileSystem.EnsureSupported(location);
        if (!File.Exists(location.FullPath!))
        {
            return new(StringComparer.Ordinal);
        }

        var bytes = await TrustedFileSystem.ReadAllBytesAsync(root, sidecar, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var document = StrictJson.Parse(bytes);
        if (!document.RootElement.TryGetProperty("inbound", out var inbound) || inbound.ValueKind != JsonValueKind.Array)
        {
            throw new VaultAuthoringException("REFERENCE_SIDECAR_INVALID", $"Reference sidecar for '{target}' is invalid.");
        }

        return inbound.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static string GetFileId(string content, string path)
    {
        var match = FileId.Match(content); if (!match.Success || !Guid.TryParse(match.Groups["id"].Value, out _))
        {
            throw new VaultAuthoringException("MANAGED_FRONTMATTER_INVALID", $"'{path}' has no usable file_id.");
        }

        return match.Groups["id"].Value.ToLowerInvariant();
    }
    private static string SetInboundFlag(string content, bool value)
    {
        if (!InboundFlag.IsMatch(content))
        {
            throw new VaultAuthoringException("MANAGED_FRONTMATTER_INVALID", "Linked target has no hasInboundLinks flag.");
        }

        return InboundFlag.Replace(content, $"hasInboundLinks: {value.ToString().ToLowerInvariant()}", 1);
    }
    private static string ManagedDocument(string title, string project, string kind, string body, DateTimeOffset now) => $"---\nfile_id: \"{Guid.NewGuid():D}\"\ntitle: \"{title.Replace("\"", "'")}\"\nproject: \"{project}\"\nkind: \"{kind}\"\nstatus: \"active\"\naliases: []\nhasInboundLinks: false\ncreated: \"{now.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC\"\nupdated: \"{now.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC\"\n---\n\n{body.TrimEnd()}\n";
    private static string ProjectRouter(string slug, string title, string purpose) => ManagedDocument(title, slug, "index", $"# {title}\n\nStatus: active\n\nPurpose: {purpose}\n\nWrite Safety: standard\n\n## File Categories\n\n- [Rules](./rules/index.md)  - Normative project behavior.\n- [Context](./context/index.md)  - Durable project knowledge.\n- [Daily](./daily/index.md)  - Current handoff and archived history.", DateTimeOffset.UtcNow);
    private static string DailyIndex(string project, string title, DateOnly date, DateTimeOffset now) => ManagedDocument($"{title} Daily Notes", project, "index", $"# {title} Daily Notes\n\n## Current Active Note\n\nPolicy: `always` for active work in this project.\n\n[{date:yyyy-MM-dd}](./active/{date:yyyy-MM-dd}.md)\n\n## Archive\n\n[Archive Index](./archive/index.md)", now);
    private static string DailyNote(string project, string title, DateOnly date, DateTimeOffset now) => ManagedDocument($"{date:yyyy-MM-dd} - {title}", project, "daily", $"# {date:yyyy-MM-dd} - {title}\n\n## Current Handoff\n\n- Objective:\n- Current state:\n- Next action:\n- Blockers:\n- Relevant rules:\n- Relevant context:\n\n## Sessions\n", now);
    private static string DailyBase(string project) => string.Equals(project, "global", StringComparison.Ordinal) ? "global" : $"projects/{project}";
    private static async Task InitializeGlobalDailyAsync(string vaultRoot, DateOnly localDate, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var writes = new[]
        {
            new Mutation(vaultRoot, "global/daily/index.md", DailyIndex("global", "Global", localDate, now), "MISSING"),
            new Mutation(vaultRoot, $"global/daily/active/{localDate:yyyy-MM-dd}.md", DailyNote("global", "Global", localDate, now), "MISSING"),
            new Mutation(vaultRoot, "global/daily/archive/index.md", ManagedDocument("Global Daily Archive", "global", "index", "# Global Daily Archive\n\nNo archived daily notes.", now), "MISSING")
        };
        await ApplyBatchAsync(vaultRoot, writes, cancellationToken);
    }
    private static async Task<string> TryReadAsync(string root, string path, CancellationToken ct) { var target = SafePath.ValidateRelative(root, path, SafePathProfile.PrivateConfig); TrustedFileSystem.EnsureSupported(target); return File.Exists(target.FullPath!) ? await TrustedFileSystem.ReadTextAsync(root, path, SafePathProfile.PrivateConfig, cancellationToken: ct) : "# Daily Archive\n"; }
    private static async Task<string> RawOrMissingAsync(string root, string path, CancellationToken ct) { var target = SafePath.ValidateRelative(root, path, SafePathProfile.PrivateConfig); TrustedFileSystem.EnsureSupported(target); return File.Exists(target.FullPath!) ? CanonicalHash.Raw(await TrustedFileSystem.ReadAllBytesAsync(root, path, SafePathProfile.PrivateConfig, cancellationToken: ct)) : "MISSING"; }
    private static string EnsureBullet(string content, string bullet) => content.Contains(bullet, StringComparison.Ordinal) ? content : content.TrimEnd() + "\n\n" + bullet + "\n";
}
