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
public sealed record AgentRegistrationResult(string SessionId, string FriendlyName, string Project, string Freshness, DateTimeOffset RegisteredAtUtc, bool StartupRequired, IReadOnlyList<string> RequiredStartupSequence);
public sealed record AgentContextResult(ContextPacket Packet, string TaskFingerprint, string BasisHash, int BasisFileCount, int ReadTokens, bool StartupComplete);
public sealed record AgentReadResult(string RelativePath, string Content, string RawSha256, string BasisHash, int BasisFileCount, int ReadTokens, bool RequiresAdditionalRefresh, IReadOnlyList<string> StalePaths);
public sealed record AgentBatchReadResult(IReadOnlyList<VaultReadResult> Files, string BasisHash, int BasisFileCount, int ReadTokens, bool RequiresAdditionalRefresh, IReadOnlyList<string> StalePaths);
public sealed record AgentWriteResult(string RelativePath, string RawSha256, int WriteTokens, IReadOnlyList<string> InvalidatedPaths);
public sealed record AgentRepositoryReadResult(string RelativePath, string Content, string RawSha256, string MetadataStatus, string ContentHandling, string BasisHash, int ReadTokens, bool RequiresAdditionalRefresh, IReadOnlyList<string> StalePaths);
public sealed record AgentRepositoryWriteResult(string RelativePath, string RawSha256, bool BranchManifestUpdated, int WriteTokens, IReadOnlyList<string> InvalidatedPaths);
public sealed record AgentRepositoryInitialization(RepositoryAgentsInitializeResult Repository, IReadOnlyList<string> InvalidatedPaths);
public sealed record AgentProjectResult(string Project, string ProjectId, IReadOnlyList<string> CreatedPaths, IReadOnlyList<string> InvalidatedPaths);
public sealed record AgentProjectInspection(ProjectInspection Project, string BasisHash, int ReadTokens, bool RequiresAdditionalRefresh, IReadOnlyList<string> StalePaths);
public sealed record AgentDailyResult(DailyInspection Daily, string BasisHash, int ReadTokens, bool RequiresAdditionalRefresh, IReadOnlyList<string> StalePaths);
public sealed record AgentLinksResult(InboundLinkInfo Links, string BasisHash, int ReadTokens, bool RequiresAdditionalRefresh, IReadOnlyList<string> StalePaths);
public sealed record AgentMutationResult(IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> InvalidatedPaths);
public sealed record AgentUsageOverview(int ActiveSessions, int StaleSessions, long ReadTokens, long WriteTokens, long TotalTokens, int TrackedFiles);
public sealed record AgentFileUsage(string RelativePath, long ReadTokens, long WriteTokens, int ReadCount, int WriteCount, IReadOnlyList<AgentFileReader> Readers);
public sealed record AgentFileReader(string SessionId, string FriendlyName, int ReadCount, int WriteCount, long ReadTokens, long WriteTokens);
public sealed record AgentSessionSummary(string SessionId, string FriendlyName, string AgentKind, string FolderContext, string Project, DateTimeOffset LastSeenAtUtc, string Freshness, string StartupStatus, string? BasisHash, int BasisFileCount, long ReadTokens, long WriteTokens);
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
                var scopeChanged = !string.Equals(session.FolderContext, folderContext, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(session.Project, project, StringComparison.Ordinal);
                session.FriendlyName = friendlyName; session.FolderContext = folderContext; session.Project = project; session.AgentKind = agentKind ?? session.AgentKind;
                if (scopeChanged)
                {
                    session.Files.Clear();
                    session.InvalidatedAtUtc = null;
                }
            }

            // Registration is the beginning of a new agent startup contract even when
            // a caller deliberately reuses an earlier session identifier.
            session.StartupCompletedAtUtc = null;
            session.StartupTaskFingerprint = null;
            session.StartupOperation = null;
            session.StartupSubjects.Clear();
            session.StartupPaths.Clear();
            session.LastSeenAtUtc = now;
            return new AgentRegistrationResult(
                session.SessionId,
                session.FriendlyName,
                session.Project,
                AgentSessionDocument.Freshness(session),
                session.RegisteredAtUtc,
                session.StartupCompletedAtUtc is null,
                ["Call agent capabilities.", "Register the session.", "Call agent context with the task operation, subjects, and repository-relative paths before any other agent operation."]);
        }, cancellationToken);
    }

    public static async Task<AgentContextResult> BuildContextAsync(
        string configRoot,
        string sessionId,
        TaskOperation operation,
        IReadOnlyList<string> subjects,
        IReadOnlyList<string> paths,
        ContextAudience audience,
        bool includeHistory,
        int optionalBudgetChars,
        int? maxTotalChars,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id");
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            var descriptor = TaskDescriptor.Create(session.Project, operation, subjects, paths, audience, includeHistory, optionalBudgetChars);
            var context = await VaultInspector.BuildTaskPacketAsync(selected.VaultRoot, descriptor, maxTotalChars, cancellationToken);
            var tokens = 0;
            foreach (var file in session.Files)
            {
                file.InBasis = false;
            }
            foreach (var file in context.Files)
            {
                tokens += TrackRead(session, file.RelativePath, file.RawSha256, file.Content);
            }
            session.StartupCompletedAtUtc = DateTimeOffset.UtcNow;
            session.StartupTaskFingerprint = descriptor.Fingerprint();
            session.StartupOperation = operation == TaskOperation.MaintainVault ? "maintain-vault" : operation.ToString().ToLowerInvariant();
            session.StartupSubjects = descriptor.Subjects.ToList();
            session.StartupPaths = descriptor.Paths.ToList();
            session.InvalidatedAtUtc = null;
            session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AgentContextResult(context.Packet, descriptor.Fingerprint(), AgentSessionDocument.BasisHash(session), AgentSessionDocument.BasisFileCount(session), tokens, true);
        }, cancellationToken);
    }

    public static async Task<AgentReadResult> ReadAsync(string configRoot, string sessionId, string relativePath, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id");
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            RequireTaskContext(session);
            var staleBeforeRead = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            var read = await VaultAuthoring.ReadAsync(selected.VaultRoot, relativePath, cancellationToken);
            var tokens = TrackRead(session, read.RelativePath, read.RawSha256, read.Content);
            var staleAfterRead = staleBeforeRead.Where(path => !path.Equals(relativePath, StringComparison.Ordinal)).ToArray();
            session.InvalidatedAtUtc = staleAfterRead.Length == 0 ? null : DateTimeOffset.UtcNow;
            var basis = AgentSessionDocument.BasisHash(session);
            return new AgentReadResult(read.RelativePath, read.Content, read.RawSha256, basis, AgentSessionDocument.BasisFileCount(session), tokens, staleAfterRead.Length > 0, staleAfterRead);
        }, cancellationToken);
    }

    public static async Task<AgentBatchReadResult> ReadManyAsync(string configRoot, string sessionId, IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id");
        if (relativePaths.Count == 0 || relativePaths.Any(string.IsNullOrWhiteSpace))
        {
            throw new AgentSessionException("AGENT_READ_INPUT_REQUIRED", "At least one non-empty vault-relative path is required.");
        }

        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            RequireTaskContext(session);
            var staleBeforeRead = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            var reads = await VaultAuthoring.ReadManyAsync(selected.VaultRoot, relativePaths, cancellationToken);
            var totalTokens = 0;
            foreach (var read in reads)
            {
                totalTokens += TrackRead(session, read.RelativePath, read.RawSha256, read.Content);
            }

            var refreshed = reads.Select(read => read.RelativePath).ToHashSet(StringComparer.Ordinal);
            var staleAfterRead = staleBeforeRead.Where(path => !refreshed.Contains(path)).ToArray();
            session.InvalidatedAtUtc = staleAfterRead.Length == 0 ? null : DateTimeOffset.UtcNow;
            return new AgentBatchReadResult(reads, AgentSessionDocument.BasisHash(session), AgentSessionDocument.BasisFileCount(session), totalTokens, staleAfterRead.Length > 0, staleAfterRead);
        }, cancellationToken);
    }

    public static async Task<AgentWriteResult> WriteAsync(string configRoot, string sessionId, string relativePath, string content, string? expectedRawSha256, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id");
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            RequireTaskContext(session, writeRequired: true);
            RequirePrivatePathScope(session, relativePath);
            var stale = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            if (stale.Count > 0)
            {
                session.InvalidatedAtUtc = DateTimeOffset.UtcNow;
                throw new AgentSessionException("AGENT_BASIS_STALE", "Previously read content changed. Refresh the reported relative paths before writing: " + string.Join(", ", stale));
            }

            var result = await VaultAuthoring.WriteAsync(new VaultWriteRequest(selected.VaultRoot, selected.ConfigRoot, relativePath, content, expectedRawSha256), cancellationToken);
            var changed = result.LinkTargetsChanged.Append(result.RelativePath).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            state.InvalidateReaders(changed);
            var writeTokens = Tokenizer.CountTokens(content);
            session.WriteTokens += writeTokens; session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            var file = session.Files.SingleOrDefault(item => item.RelativePath.Equals(relativePath, StringComparison.Ordinal));
            if (file is null) { file = new AgentFileState { RelativePath = relativePath }; session.Files.Add(file); }
            file.RawSha256 = result.RawSha256;
            file.InBasis = true;
            file.WriteCount++; file.WriteTokens += writeTokens;
            session.InvalidatedAtUtc = changed.Any(path => !path.Equals(relativePath, StringComparison.Ordinal) && session.Files.Any(item => item.InBasis && item.RelativePath.Equals(path, StringComparison.Ordinal)))
                ? DateTimeOffset.UtcNow
                : null;
            return new AgentWriteResult(result.RelativePath, result.RawSha256, writeTokens, changed);
        }, cancellationToken);
    }

    public static async Task<AgentRepositoryReadResult> ReadRepositoryAsync(string configRoot, string sessionId, string relativePath, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id");
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            RequireTaskContext(session);
            RequireRepositoryScope(session);
            var staleBeforeRead = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            var repositoryRoot = await RepositoryAgentsAuthoring.ResolveRepositoryRootAsync(session.FolderContext, cancellationToken);
            var read = await RepositoryAgentsAuthoring.ReadAsync(repositoryRoot, relativePath, cancellationToken);
            var trackedPath = RepositoryTrackedPath(relativePath);
            var tokens = TrackRead(session, trackedPath, read.RawSha256, read.Content);
            var staleAfterRead = staleBeforeRead.Where(path => !path.Equals(trackedPath, StringComparison.Ordinal)).ToArray();
            session.InvalidatedAtUtc = staleAfterRead.Length == 0 ? null : DateTimeOffset.UtcNow;
            return new AgentRepositoryReadResult(
                read.RelativePath,
                read.Content,
                read.RawSha256,
                read.MetadataStatus,
                read.ContentHandling,
                AgentSessionDocument.BasisHash(session),
                tokens,
                staleAfterRead.Length > 0,
                staleAfterRead);
        }, cancellationToken);
    }

    public static async Task<AgentRepositoryWriteResult> WriteRepositoryAsync(string configRoot, string sessionId, string relativePath, string content, string? expectedRawSha256, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id");
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            RequireTaskContext(session, writeRequired: true);
            RequireRepositoryScope(session);
            await RequireFreshAsync(selected.VaultRoot, session, cancellationToken);
            var repositoryRoot = await RepositoryAgentsAuthoring.ResolveRepositoryRootAsync(session.FolderContext, cancellationToken);
            var result = await RepositoryAgentsAuthoring.WriteAsync(new RepositoryAgentsWriteRequest(repositoryRoot, relativePath, content, expectedRawSha256), cancellationToken);
            var trackedPath = RepositoryTrackedPath(relativePath);
            state.InvalidateReaders([trackedPath]);
            var tokens = Tokenizer.CountTokens(content);
            session.WriteTokens += tokens;
            session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            var file = session.Files.SingleOrDefault(item => item.RelativePath.Equals(trackedPath, StringComparison.Ordinal));
            if (file is null)
            {
                file = new AgentFileState { RelativePath = trackedPath };
                session.Files.Add(file);
            }
            file.RawSha256 = result.RawSha256;
            file.InBasis = true;
            file.WriteCount++;
            file.WriteTokens += tokens;
            session.InvalidatedAtUtc = null;
            return new AgentRepositoryWriteResult(result.RelativePath, result.RawSha256, result.BranchManifestUpdated, tokens, [trackedPath]);
        }, cancellationToken);
    }

    public static async Task<AgentRepositoryInitialization> InitializeRepositoryAsync(string configRoot, string sessionId, string project, string title, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, "session id");
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            RequireTaskContext(session, writeRequired: true);
            RequireRepositoryScope(session);
            await RequireFreshAsync(selected.VaultRoot, session, cancellationToken);
            var privateProject = await VaultAuthoring.InspectProjectAsync(selected.VaultRoot, project, cancellationToken);
            var result = await RepositoryAgentsAuthoring.InitializeAsync(session.FolderContext, privateProject.ProjectId, project, title, cancellationToken);
            var changed = result.CreatedPaths.Select(RepositoryTrackedPath).ToArray();
            state.InvalidateReaders(changed);
            session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AgentRepositoryInitialization(result, changed);
        }, cancellationToken);
    }

    public static async Task<AgentProjectResult> CreateProjectAsync(string configRoot, string sessionId, string slug, string title, string? purpose, string? endGoal, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId); RequireTaskContext(session, writeRequired: true); RequireAnySubject(session, ["project", "repository", "git"], "project creation"); await RequireFreshAsync(selected.VaultRoot, session, cancellationToken);
            var result = await VaultAuthoring.CreateProjectAsync(new ProjectCreateRequest(selected.VaultRoot, selected.ConfigRoot, slug, title, purpose, endGoal), cancellationToken);
            state.InvalidateReaders(result.ChangedPaths); session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AgentProjectResult(result.Project, result.ProjectId, result.CreatedPaths, result.ChangedPaths);
        }, cancellationToken);
    }

    public static async Task<AgentProjectInspection> InspectProjectAsync(string configRoot, string sessionId, string project, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            RequireTaskContext(session);
            RequireAnySubject(session, ["project", "repository", "git"], "project inspection");
            var staleBefore = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            var inspection = await VaultAuthoring.InspectProjectAsync(selected.VaultRoot, project, cancellationToken);
            var relativePath = $"projects/{project}/project.json";
            var rendered = JsonSerializer.Serialize(inspection);
            var tokens = TrackRead(session, relativePath, inspection.RawSha256, rendered);
            var remaining = staleBefore.Where(path => !path.Equals(relativePath, StringComparison.Ordinal)).ToArray();
            session.InvalidatedAtUtc = remaining.Length == 0 ? null : DateTimeOffset.UtcNow;
            return new AgentProjectInspection(inspection, AgentSessionDocument.BasisHash(session), tokens, remaining.Length > 0, remaining);
        }, cancellationToken);
    }

    public static async Task<AgentDailyResult> InspectDailyAsync(string configRoot, string sessionId, string project, DateOnly date, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId);
            RequireTaskContext(session);
            RequireAnySubject(session, ["daily", "continuity", "handoff"], "daily inspection");
            var staleBefore = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            var daily = await VaultAuthoring.InspectDailyAsync(selected.VaultRoot, project, date, selected.ConfigRoot, cancellationToken);
            var tokens = TrackRead(session, daily.ActiveRelativePath, daily.ActiveRawSha256, daily.ActiveContent);
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
            RequireTaskContext(session, writeRequired: true);
            var staleBefore = await FindStalePathsAsync(selected.VaultRoot, session, cancellationToken);
            var inspection = await VaultAuthoring.InspectInboundLinksWithReadAsync(selected.VaultRoot, relativePath, cancellationToken);
            var tokens = TrackRead(session, inspection.File.RelativePath, inspection.File.RawSha256, inspection.File.Content);
            var remaining = staleBefore.Where(path => !path.Equals(inspection.File.RelativePath, StringComparison.Ordinal)).ToArray();
            session.InvalidatedAtUtc = remaining.Length == 0 ? null : DateTimeOffset.UtcNow;
            return new AgentLinksResult(inspection.Links, AgentSessionDocument.BasisHash(session), tokens, remaining.Length > 0, remaining);
        }, cancellationToken);
    }

    public static async Task<AgentMutationResult> RolloverDailyAsync(string configRoot, string sessionId, string project, DateOnly date, string expectedRawSha256, bool promotionsComplete, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId); RequireTaskContext(session, writeRequired: true); RequireAnySubject(session, ["daily", "continuity", "handoff"], "daily rollover"); await RequireFreshAsync(selected.VaultRoot, session, cancellationToken);
            var changed = await VaultAuthoring.RolloverDailyAsync(selected.VaultRoot, selected.ConfigRoot, project, date, expectedRawSha256, promotionsComplete, cancellationToken);
            state.InvalidateReaders(changed); session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            return new AgentMutationResult(changed, changed);
        }, cancellationToken);
    }

    public static async Task<AgentMutationResult> DeleteAsync(string configRoot, string sessionId, string relativePath, string expectedRawSha256, CancellationToken cancellationToken = default)
    {
        var selected = await ResolveSelectedVaultAsync(configRoot, cancellationToken);
        return await MutateAsync(configRoot, async state =>
        {
            var session = state.Require(sessionId); RequireTaskContext(session, writeRequired: true); RequirePrivatePathScope(session, relativePath); await RequireFreshAsync(selected.VaultRoot, session, cancellationToken);
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

            var summaries = selectedSessions.OrderBy(item => item.FriendlyName, StringComparer.Ordinal).Select(item => new AgentSessionSummary(item.SessionId, item.FriendlyName, item.AgentKind, item.FolderContext, item.Project, item.LastSeenAtUtc, AgentSessionDocument.Freshness(item), item.StartupCompletedAtUtc is null ? "required" : "complete", AgentSessionDocument.BasisHashOrNull(item), AgentSessionDocument.BasisFileCount(item), item.ReadTokens, item.WriteTokens)).ToArray();
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

        var configuredRoot = NormalizeRoot(defaultRoot.GetString()!);
        var matches = new List<JsonElement>();
        foreach (var item in vaults.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("vault_root", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new AgentSessionException("VAULT_REGISTRY_INVALID", "The selected-vault registry contains an invalid vault entry.");
            }
            if (string.Equals(NormalizeRoot(value.GetString()!), configuredRoot, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(item);
            }
        }
        if (matches.Count != 1)
        {
            throw new AgentSessionException("VAULT_REGISTRY_INVALID", "The selected-vault registry entry is missing or ambiguous.");
        }
        var entry = matches[0];
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("vault_id", out var registryId) || registryId.ValueKind != JsonValueKind.String || !entry.TryGetProperty("protected_content_manifest_sha256", out var registryHash) || registryHash.ValueKind != JsonValueKind.String)
        {
            throw new AgentSessionException("VAULT_REGISTRY_INVALID", "The selected-vault registry entry is incomplete or ambiguous.");
        }

        var inspection = await VaultInspector.InspectIdentityAsync(configuredRoot, cancellationToken);
        if (!string.Equals(inspection.VaultId, registryId.GetString(), StringComparison.OrdinalIgnoreCase) || !string.Equals(inspection.ContentIntegrityCanonicalSha256, registryHash.GetString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentSessionException("VAULT_REGISTRY_INTEGRITY_MISMATCH", "Selected private-vault identity or protected-content anchor did not verify.");
        }

        return new SelectedVault(configuredRoot, configRoot);
    }

    private static string NormalizeRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static async Task<List<string>> FindStalePathsAsync(string? vaultRoot, AgentSessionState session, CancellationToken cancellationToken)
    {
        var stale = new List<string>();
        foreach (var file in session.Files.Where(item => item.InBasis))
        {
            try
            {
                string currentHash;
                if (file.RelativePath.StartsWith("repository:", StringComparison.Ordinal))
                {
                    var repositoryRoot = await RepositoryAgentsAuthoring.ResolveRepositoryRootAsync(session.FolderContext, cancellationToken);
                    var current = await RepositoryAgentsAuthoring.ReadAsync(repositoryRoot, file.RelativePath["repository:".Length..], cancellationToken);
                    currentHash = current.RawSha256;
                }
                else
                {
                    if (vaultRoot is null) { throw new InvalidOperationException("Private-vault root is required for a mixed session freshness check."); }
                    if (file.RelativePath.EndsWith("/project.json", StringComparison.Ordinal))
                    {
                        currentHash = CanonicalHash.Raw(await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, file.RelativePath, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken));
                    }
                    else
                    {
                        var current = await VaultAuthoring.ReadAsync(vaultRoot, file.RelativePath, cancellationToken);
                        currentHash = current.RawSha256;
                    }
                }

                if (!string.Equals(currentHash, file.RawSha256, StringComparison.OrdinalIgnoreCase))
                {
                    stale.Add(file.RelativePath);
                }
            }
            catch (Exception exception) when (exception is VaultAuthoringException or SafePathException or StorageFormatException or FileNotFoundException or System.ComponentModel.Win32Exception)
            {
                // The Windows handle-relative reader surfaces a disappeared final path as a
                // Win32Exception. A missing or replaced basis file is stale session data, not
                // a CLI process failure. The next explicit read still runs the full trust gate.
                stale.Add(file.RelativePath);
            }
        }
        return stale.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private static async Task RequireFreshAsync(string? vaultRoot, AgentSessionState session, CancellationToken cancellationToken)
    {
        var stale = await FindStalePathsAsync(vaultRoot, session, cancellationToken);
        if (stale.Count > 0)
        {
            session.InvalidatedAtUtc = DateTimeOffset.UtcNow;
            throw new AgentSessionException("AGENT_BASIS_STALE", "Previously read content changed. Refresh the reported relative paths before continuing: " + string.Join(", ", stale));
        }
    }

    private static void RequireTaskContext(AgentSessionState session, bool writeRequired = false)
    {
        if (session.StartupCompletedAtUtc is null || string.IsNullOrWhiteSpace(session.StartupTaskFingerprint))
        {
            throw new AgentSessionException("AGENT_STARTUP_REQUIRED", "Call agent context with the current task operation, subjects, and repository-relative paths before using other agent operations.");
        }
        if (writeRequired && session.StartupOperation is not ("edit" or "release" or "maintain-vault"))
        {
            throw new AgentSessionException("AGENT_CONTEXT_OPERATION_REQUIRED", "The loaded context does not cover writes. Call agent context with operation edit, release, or maintain-vault before continuing.");
        }
    }

    private static void RequireAnySubject(AgentSessionState session, IReadOnlyList<string> subjects, string operation)
    {
        if (!session.StartupSubjects.Intersect(subjects, StringComparer.Ordinal).Any())
        {
            throw new AgentSessionException("AGENT_CONTEXT_SCOPE_REQUIRED", $"The loaded context does not cover {operation}. Call agent context again with one of these subjects: {string.Join(", ", subjects)}.");
        }
    }

    private static void RequireRepositoryScope(AgentSessionState session)
    {
        if (session.StartupPaths.Any(path => path.StartsWith(".agents/", StringComparison.Ordinal)))
        {
            return;
        }
        RequireAnySubject(session, ["repository", "git", "project"], "repository .agents access");
    }

    private static void RequirePrivatePathScope(AgentSessionState session, string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Length >= 4 && segments[0] == "projects" && segments[2] == "daily")
        {
            RequireAnySubject(session, ["daily", "continuity", "handoff"], "daily-note authoring");
        }
        if (segments.Length >= 3 && segments[0] == "global" && segments[1] == "daily")
        {
            RequireAnySubject(session, ["daily", "continuity", "handoff"], "global daily-note authoring");
        }
        if (segments.Length >= 5 && segments[0] == "projects" && segments[2] is "rules" or "context")
        {
            RequireAnySubject(session, [segments[3]], $"the '{segments[3]}' subject");
        }
    }

    private static string RepositoryTrackedPath(string relativePath) => "repository:" + relativePath.Replace('\\', '/');

    private static int TrackRead(AgentSessionState session, string relativePath, string rawSha256, string content)
    {
        var file = session.Files.SingleOrDefault(item => item.RelativePath.Equals(relativePath, StringComparison.Ordinal));
        if (file is null) { file = new AgentFileState { RelativePath = relativePath }; session.Files.Add(file); }
        var tokens = Tokenizer.CountTokens(content);
        file.RawSha256 = rawSha256; file.ReadCount++; file.ReadTokens += tokens;
        file.InBasis = true;
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
            if (state.SchemaVersion != 1 || state.Sessions.Any(session => string.IsNullOrWhiteSpace(session.SessionId) || session.Files is null || session.StartupSubjects is null || session.StartupPaths is null || session.Files.GroupBy(file => file.RelativePath, StringComparer.Ordinal).Any(group => group.Count() != 1)))
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
        public static int BasisFileCount(AgentSessionState session) => session.Files.Count(item => item.InBasis);
        public static string BasisHash(AgentSessionState session) => CanonicalHash.Raw(Encoding.UTF8.GetBytes(string.Join("\n", session.Files.Where(item => item.InBasis).OrderBy(item => item.RelativePath, StringComparer.Ordinal).Select(item => item.RelativePath + "\t" + item.RawSha256))));
        public static string? BasisHashOrNull(AgentSessionState session) => BasisFileCount(session) == 0 ? null : BasisHash(session);
        public void InvalidateReaders(IReadOnlyList<string> changed)
        {
            var set = changed.ToHashSet(StringComparer.Ordinal);
            foreach (var session in Sessions.Where(session => session.Files.Any(file => file.InBasis && set.Contains(file.RelativePath))))
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
        public DateTimeOffset? StartupCompletedAtUtc { get; set; }
        public string? StartupTaskFingerprint { get; set; }
        public string? StartupOperation { get; set; }
        public List<string> StartupSubjects { get; set; } = [];
        public List<string> StartupPaths { get; set; } = [];
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
        public bool InBasis { get; set; } = true;
    }
}
