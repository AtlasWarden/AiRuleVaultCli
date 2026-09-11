using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;
using RuleVault.Agents;
using RuleVault.Core;
using RuleVault.Storage;

namespace RuleVault.Cli;

/// <summary>
/// Opaque agent-facing state. Vault paths remain an implementation detail of the
/// registry resolver; callers receive only relative content identities and a
/// session basis digest.
/// </summary>
public sealed record AgentRegistrationResult(string SessionId, string FriendlyName, string Project, string Freshness, DateTimeOffset RegisteredAtUtc);
public sealed record AgentReadResult(string RelativePath, string Content, string RawSha256, string BasisHash, int BasisFileCount, int ReadTokens, bool RequiresAdditionalRefresh, IReadOnlyList<string> StalePaths);
public sealed record AgentWriteResult(string RelativePath, string RawSha256, int WriteTokens, IReadOnlyList<string> InvalidatedPaths);
public sealed record AgentProjectResult(string Project, IReadOnlyList<string> CreatedPaths, IReadOnlyList<string> InvalidatedPaths);
public sealed record AgentDailyResult(DailyInspection Daily, string BasisHash, int ReadTokens, bool RequiresAdditionalRefresh, IReadOnlyList<string> StalePaths);
public sealed record AgentLinksResult(InboundLinkInfo Links, string BasisHash, int ReadTokens, bool RequiresAdditionalRefresh, IReadOnlyList<string> StalePaths);
public sealed record AgentMutationResult(IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> InvalidatedPaths);
public sealed record AgentUsageOverview(int ActiveSessions, int StaleSessions, long ReadTokens, long WriteTokens, long TotalTokens, int TrackedFiles);
public sealed record AgentFileUsage(string RelativePath, long ReadTokens, long WriteTokens, int ReadCount, int WriteCount, IReadOnlyList<AgentFileReader> Readers);
public sealed record AgentFileReader(string SessionId, string FriendlyName, int ReadCount, int WriteCount, long ReadTokens, long WriteTokens);
public sealed record AgentSessionSummary(string SessionId, string FriendlyName, string AgentKind, string FolderContext, string Project, DateTimeOffset LastSeenAtUtc, string Freshness, string? BasisHash, int BasisFileCount, long ReadTokens, long WriteTokens);
public sealed record AgentUsageReport(AgentUsageOverview Overview, IReadOnlyList<AgentSessionSummary> Sessions, IReadOnlyList<AgentFileUsage> Files);

public sealed class AgentSessionException : Exception
{
    public AgentSessionException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

public static class AgentSessions
{
    private const string StateFile = "agent-sessions.json";
    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true };
    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    public static string ResolveConfigRoot(string? explicitConfigRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitConfigRoot))
        {
            return Path.GetFullPath(explicitConfigRoot);
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var cliDirectory = Directory.GetParent(executable);
            if (cliDirectory?.Name.Equals("cli", StringComparison.OrdinalIgnoreCase) == true && cliDirectory.Parent is not null)
            {
                return cliDirectory.Parent.FullName;
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Rule-Vault");
    }

    public static async Task<AgentRegistrationResult> RegisterAsync(string configRoot, string sessionId, string friendlyName, string folderContext, string project, string? agentKind, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id"); ValidateIdentity(friendlyName, "friendly name"); ValidateIdentity(folderContext, "folder context"); ValidateIdentity(project, "project");
        var now = DateTimeOffset.UtcNow;
        return await MutateAsync(configRoot, state =>
        {
            state.RemoveExpired(now);
            var session = state.Sessions.SingleOrDefault(item => item.SessionId.Equals(sessionId, StringComparison.Ordinal));
            if (session is null)
            {
                session = new AgentSessionState { SessionId = sessionId, FriendlyName = friendlyName, FolderContext = folderContext, Project = project, AgentKind = agentKind ?? "unknown", RegisteredAtUtc = now };
                state.Sessions.Add(session);
            }
            else
            {
                session.FriendlyName = friendlyName; session.FolderContext = folderContext; session.Project = project; session.AgentKind = agentKind ?? session.AgentKind;
            }

            session.LastSeenAtUtc = now;
            return new AgentRegistrationResult(session.SessionId, session.FriendlyName, session.Project, AgentSessionDocument.Freshness(session), session.RegisteredAtUtc);
        }, cancellationToken);
    }

    public static async Task<AgentReadResult> ReadAsync(string configRoot, string sessionId, string relativePath, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id");
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            var staleBeforeRead = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            var read = await VaultAuthoring.ReadAsync(selected.VaultRoot, relativePath, cancellationToken);
            var usage = session.Files.SingleOrDefault(item => item.RelativePath.Equals(relativePath, StringComparison.Ordinal));
            if (usage is null)
            {
                usage = new AgentFileState { RelativePath = relativePath };
                session.Files.Add(usage);
            }

            var tokens = Tokenizer.CountTokens(read.Content);
            usage.RawSha256 = read.RawSha256; usage.ReadCount++; usage.ReadTokens += tokens;
            session.ReadTokens += tokens; session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            var staleAfterRead = staleBeforeRead.Where(path => !path.Equals(relativePath, StringComparison.Ordinal)).ToArray();
            session.InvalidatedAtUtc = staleAfterRead.Length == 0 ? null : DateTimeOffset.UtcNow;
            var basis = AgentSessionDocument.BasisHash(session);
            return new AgentReadResult(read.RelativePath, read.Content, read.RawSha256, basis, session.Files.Count, tokens, staleAfterRead.Length > 0, staleAfterRead);
        }, cancellationToken);
    }

    public static async Task<AgentWriteResult> WriteAsync(string configRoot, string sessionId, string relativePath, string content, string? expectedRawSha256, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id");
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            var stale = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            if (stale.Count > 0)
            {
                session.InvalidatedAtUtc = DateTimeOffset.UtcNow;
                throw new AgentSessionException("AGENT_BASIS_STALE", "Previously read content changed. Refresh the reported relative paths before writing: " + string.Join(", ", stale));
            }

            var result = await VaultAuthoring.WriteAsync(new VaultWriteRequest(selected.VaultRoot, selected.ConfigRoot, relativePath, content, expectedRawSha256), cancellationToken);
            var changed = result.LinkTargetsChanged.Append(result.RelativePath).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            state.InvalidateReaders(changed);
            session.WriteTokens += Tokenizer.CountTokens(content); session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            var file = session.Files.SingleOrDefault(item => item.RelativePath.Equals(relativePath, StringComparison.Ordinal));
            if (file is null) { file = new AgentFileState { RelativePath = relativePath }; session.Files.Add(file); }
            file.WriteCount++; file.WriteTokens += Tokenizer.CountTokens(content);
            return new AgentWriteResult(result.RelativePath, result.RawSha256, Tokenizer.CountTokens(content), changed);
        }, cancellationToken);
    }

    public static async Task<AgentProjectResult> CreateProjectAsync(string configRoot, string sessionId, string slug, string title, string? purpose, string? endGoal, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId); await RequireFreshAsync(selected.VaultRoot, session, cancellationToken);
            var result = await VaultAuthoring.CreateProjectAsync(new ProjectCreateRequest(selected.VaultRoot, selected.ConfigRoot, slug, title, purpose, endGoal), cancellationToken);
            state.InvalidateReaders(result.CreatedPaths); session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AgentProjectResult(result.Project, result.CreatedPaths, result.CreatedPaths);
        }, cancellationToken);
    }

    public static async Task<AgentDailyResult> InspectDailyAsync(string configRoot, string sessionId, string project, DateOnly date, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            var staleBefore = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            var daily = await VaultAuthoring.InspectDailyAsync(selected.VaultRoot, project, date, cancellationToken);
            var tokens = TrackRead(state, session, daily.ActiveRelativePath, daily.ActiveRawSha256, daily.ActiveContent);
            var remaining = staleBefore.Where(path => !path.Equals(daily.ActiveRelativePath, StringComparison.Ordinal)).ToArray();
            session.InvalidatedAtUtc = remaining.Length == 0 ? null : DateTimeOffset.UtcNow;
            return new AgentDailyResult(daily, AgentSessionDocument.BasisHash(session), tokens, remaining.Length > 0, remaining);
        }, cancellationToken);
    }

    public static async Task<AgentLinksResult> InspectLinksAsync(string configRoot, string sessionId, string relativePath, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            var staleBefore = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            var read = await VaultAuthoring.ReadAsync(selected.VaultRoot, relativePath, cancellationToken);
            var links = await VaultAuthoring.InspectInboundLinksAsync(selected.VaultRoot, relativePath, cancellationToken);
            var tokens = TrackRead(state, session, read.RelativePath, read.RawSha256, read.Content);
            var remaining = staleBefore.Where(path => !path.Equals(read.RelativePath, StringComparison.Ordinal)).ToArray();
            session.InvalidatedAtUtc = remaining.Length == 0 ? null : DateTimeOffset.UtcNow;
            return new AgentLinksResult(links, AgentSessionDocument.BasisHash(session), tokens, remaining.Length > 0, remaining);
        }, cancellationToken);
    }

    public static async Task<AgentMutationResult> RolloverDailyAsync(string configRoot, string sessionId, string project, DateOnly date, string expectedRawSha256, bool promotionsComplete, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId); await RequireFreshAsync(selected.VaultRoot, session, cancellationToken);
            var changed = await VaultAuthoring.RolloverDailyAsync(selected.VaultRoot, project, date, expectedRawSha256, promotionsComplete, cancellationToken);
            state.InvalidateReaders(changed); session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AgentMutationResult(changed, changed);
        }, cancellationToken);
    }

    public static async Task<AgentMutationResult> DeleteAsync(string configRoot, string sessionId, string relativePath, string expectedRawSha256, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId); await RequireFreshAsync(selected.VaultRoot, session, cancellationToken);
            var changed = await VaultAuthoring.DeleteAsync(selected.VaultRoot, relativePath, expectedRawSha256, cancellationToken);
            state.InvalidateReaders(changed); session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AgentMutationResult(changed, changed);
        }, cancellationToken);
    }

    public static async Task<AgentUsageReport> UsageAsync(string configRoot, string? sessionId, CancellationToken cancellationToken = default) =>
        await MutateAsync(configRoot, async state =>
        {
            var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
            foreach (var session in state.Sessions)
            {
                var stale = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
                session.InvalidatedAtUtc = stale.Count == 0 ? null : session.InvalidatedAtUtc ?? DateTimeOffset.UtcNow;
            }

            state.RemoveExpired(DateTimeOffset.UtcNow);
            var selectedSessions = string.IsNullOrWhiteSpace(sessionId) ? state.Sessions : state.Sessions.Where(item => item.SessionId.Equals(sessionId, StringComparison.Ordinal)).ToList();
            if (!string.IsNullOrWhiteSpace(sessionId) && selectedSessions.Count == 0)
            {
                throw new AgentSessionException("AGENT_SESSION_NOT_FOUND", "No registered agent session has that session id.");
            }

            var summaries = selectedSessions.OrderBy(item => item.FriendlyName, StringComparer.Ordinal).Select(item => new AgentSessionSummary(item.SessionId, item.FriendlyName, item.AgentKind, item.FolderContext, item.Project, item.LastSeenAtUtc, AgentSessionDocument.Freshness(item), AgentSessionDocument.BasisHashOrNull(item), item.Files.Count, item.ReadTokens, item.WriteTokens)).ToArray();
            var files = selectedSessions.SelectMany(session => session.Files.Select(file => new { session, file }))
                .GroupBy(item => item.file.RelativePath, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new AgentFileUsage(group.Key, group.Sum(item => item.file.ReadTokens), group.Sum(item => item.file.WriteTokens), group.Sum(item => item.file.ReadCount), group.Sum(item => item.file.WriteCount), group.OrderBy(item => item.session.FriendlyName, StringComparer.Ordinal).Select(item => new AgentFileReader(item.session.SessionId, item.session.FriendlyName, item.file.ReadCount, item.file.WriteCount, item.file.ReadTokens, item.file.WriteTokens)).ToArray())).ToArray();
            return new AgentUsageReport(new AgentUsageOverview(summaries.Length, summaries.Count(item => item.Freshness == "stale"), summaries.Sum(item => item.ReadTokens), summaries.Sum(item => item.WriteTokens), summaries.Sum(item => item.ReadTokens + item.WriteTokens), files.Length), summaries, files);
        }, cancellationToken);

    public static async Task<int> ClearAsync(string configRoot, string? sessionId, bool all, CancellationToken cancellationToken = default) =>
        await MutateAsync(configRoot, state =>
        {
            if (all) { var count = state.Sessions.Count; state.Sessions.Clear(); return count; }
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new AgentSessionException("AGENT_CLEAR_INPUT_REQUIRED", "Provide --session-id or --all true.");
            }

            return state.Sessions.RemoveAll(item => item.SessionId.Equals(sessionId, StringComparison.Ordinal));
        }, cancellationToken);

    private static async Task<SelectedVault> ResolveSelectedVaultAsync(string configRoot, CancellationToken cancellationToken)
    {
        var bytes = await TrustedFileSystem.ReadAllBytesAsync(configRoot, "vault-registry.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var document = StrictJson.Parse(bytes);
        var root = document.RootElement;
        if (!root.TryGetProperty("default_vault_root", out var defaultRoot) || defaultRoot.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(defaultRoot.GetString()) ||
            !root.TryGetProperty("vaults", out var vaults) || vaults.ValueKind != JsonValueKind.Array)
        {
            throw new AgentSessionException("VAULT_REGISTRY_INVALID", "The selected-vault registry is missing required fields.");
        }

        var configuredRoot = Path.GetFullPath(defaultRoot.GetString()!);
        var entry = vaults.EnumerateArray().SingleOrDefault(item => item.TryGetProperty("vault_root", out var value) && value.ValueKind == JsonValueKind.String && Path.GetFullPath(value.GetString()!) == configuredRoot);
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("vault_id", out var registryId) || registryId.ValueKind != JsonValueKind.String || !entry.TryGetProperty("protected_content_manifest_sha256", out var registryHash) || registryHash.ValueKind != JsonValueKind.String)
        {
            throw new AgentSessionException("VAULT_REGISTRY_INVALID", "The selected-vault registry entry is incomplete or ambiguous.");
        }

        var inspection = await VaultInspector.InspectAsync(configuredRoot, cancellationToken);
        if (!string.Equals(inspection.VaultId, registryId.GetString(), StringComparison.OrdinalIgnoreCase) || !string.Equals(inspection.ContentIntegrityCanonicalSha256, registryHash.GetString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentSessionException("VAULT_REGISTRY_INTEGRITY_MISMATCH", "Selected private-vault identity or protected-content anchor did not verify.");
        }

        return new SelectedVault(configuredRoot, configRoot);
    }

    private static async Task<List<string>> FindStalePathsAsync(string vaultRoot, AgentSessionState session, CancellationToken cancellationToken)
    {
        var stale = new List<string>();
        foreach (var file in session.Files)
        {
            try
            {
                var current = await VaultAuthoring.ReadAsync(vaultRoot, file.RelativePath, cancellationToken);
                if (!string.Equals(current.RawSha256, file.RawSha256, StringComparison.OrdinalIgnoreCase))
                {
                    stale.Add(file.RelativePath);
                }
            }
            catch (Exception exception) when (exception is VaultAuthoringException or SafePathException or StorageFormatException or FileNotFoundException)
            {
                stale.Add(file.RelativePath);
            }
        }
        return stale.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private static async Task RequireFreshAsync(string vaultRoot, AgentSessionState session, CancellationToken cancellationToken)
    {
        var stale = await FindStalePathsAsync(vaultRoot, session, cancellationToken);
        if (stale.Count > 0)
        {
            session.InvalidatedAtUtc = DateTimeOffset.UtcNow;
            throw new AgentSessionException("AGENT_BASIS_STALE", "Previously read content changed. Refresh the reported relative paths before continuing: " + string.Join(", ", stale));
        }
    }

    private static int TrackRead(AgentSessionDocument state, AgentSessionState session, string relativePath, string rawSha256, string content)
    {
        var file = session.Files.SingleOrDefault(item => item.RelativePath.Equals(relativePath, StringComparison.Ordinal));
        if (file is null) { file = new AgentFileState { RelativePath = relativePath }; session.Files.Add(file); }
        var tokens = Tokenizer.CountTokens(content);
        file.RawSha256 = rawSha256; file.ReadCount++; file.ReadTokens += tokens;
        session.ReadTokens += tokens; session.LastSeenAtUtc = DateTimeOffset.UtcNow;
        return tokens;
    }

    private static async Task<T> MutateAsync<T>(string configRoot, Func<AgentSessionDocument, T> action, CancellationToken cancellationToken) =>
        await MutateAsync(configRoot, state => Task.FromResult(action(state)), cancellationToken);

    private static async Task<T> MutateAsync<T>(string configRoot, Func<AgentSessionDocument, Task<T>> action, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(configRoot);
        await using var writeLock = await ProtectedLock.AcquireAsync(configRoot, StateFile, cancellationToken: cancellationToken);
        var (state, rawHash) = await LoadAsync(configRoot, cancellationToken);
        state.RemoveExpired(DateTimeOffset.UtcNow);
        var result = await action(state);
        await writeLock.VerifyOwnershipAsync(cancellationToken);
        state.SchemaVersion = 1; state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var content = JsonSerializer.Serialize(state, StateJson) + "\n";
        await TrustedWriter.WriteTextAsync(configRoot, StateFile, content, rawHash, SafePathProfile.PrivateConfig, cancellationToken);
        return result;
    }

    private static async Task<(AgentSessionDocument State, string? RawHash)> LoadAsync(string configRoot, CancellationToken cancellationToken)
    {
        var path = SafePath.ValidateRelative(configRoot, StateFile, SafePathProfile.PrivateConfig); TrustedFileSystem.EnsureSupported(path);
        if (!File.Exists(path.FullPath!))
        {
            return (new AgentSessionDocument(), null);
        }

        var bytes = await TrustedFileSystem.ReadAllBytesAsync(configRoot, StateFile, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        try
        {
            var state = StrictJson.Deserialize<AgentSessionDocument>(bytes);
            if (state.SchemaVersion != 1 || state.Sessions.Any(session => string.IsNullOrWhiteSpace(session.SessionId) || session.Files.GroupBy(file => file.RelativePath, StringComparer.Ordinal).Any(group => group.Count() != 1)))
            {
                throw new AgentSessionException("AGENT_SESSION_STATE_INVALID", "Agent session state does not match the supported schema.");
            }

            return (state, CanonicalHash.Raw(bytes));
        }
        catch (StorageFormatException exception) { throw new AgentSessionException("AGENT_SESSION_STATE_INVALID", exception.Message); }
    }

    private static void ValidateIdentity(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new AgentSessionException("AGENT_IDENTITY_INVALID", $"The {label} must be non-empty, at most 256 characters, and contain no control characters.");
        }
    }

    private sealed record SelectedVault(string VaultRoot, string ConfigRoot);
    public sealed class AgentSessionDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public List<AgentSessionState> Sessions { get; set; } = [];
        public AgentSessionState Require(string id) => Sessions.SingleOrDefault(item => item.SessionId.Equals(id, StringComparison.Ordinal)) ?? throw new AgentSessionException("AGENT_SESSION_NOT_FOUND", "Register the agent session before requesting vault content.");
        public void RemoveExpired(DateTimeOffset now) => Sessions.RemoveAll(item => now - item.LastSeenAtUtc > TimeSpan.FromDays(3));
        public static string Freshness(AgentSessionState session) => session.InvalidatedAtUtc is null ? "current" : "stale";
        public static string BasisHash(AgentSessionState session) => CanonicalHash.Raw(Encoding.UTF8.GetBytes(string.Join("\n", session.Files.OrderBy(item => item.RelativePath, StringComparer.Ordinal).Select(item => item.RelativePath + "\t" + item.RawSha256))));
        public static string? BasisHashOrNull(AgentSessionState session) => session.Files.Count == 0 ? null : BasisHash(session);
        public void InvalidateReaders(IReadOnlyList<string> changed)
        {
            var set = changed.ToHashSet(StringComparer.Ordinal);
            foreach (var session in Sessions.Where(session => session.Files.Any(file => set.Contains(file.RelativePath))))
            {
                session.InvalidatedAtUtc = DateTimeOffset.UtcNow;
            }
        }
    }
    public sealed class AgentSessionState
    {
        public string SessionId { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
        public string AgentKind { get; set; } = "unknown";
        public string FolderContext { get; set; } = string.Empty;
        public string Project { get; set; } = string.Empty;
        public DateTimeOffset RegisteredAtUtc { get; set; }
        public DateTimeOffset LastSeenAtUtc { get; set; }
        public DateTimeOffset? InvalidatedAtUtc { get; set; }
        public long ReadTokens { get; set; }
        public long WriteTokens { get; set; }
        public List<AgentFileState> Files { get; set; } = [];
    }
    public sealed class AgentFileState
    {
        public string RelativePath { get; set; } = string.Empty;
        public string RawSha256 { get; set; } = string.Empty;
        public int ReadCount { get; set; }
        public int WriteCount { get; set; }
        public long ReadTokens { get; set; }
        public long WriteTokens { get; set; }
    }
}
