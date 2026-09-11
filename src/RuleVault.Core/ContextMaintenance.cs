using System.Text;

namespace RuleVault.Core;

public sealed record ContextCacheRecord(
    int SchemaVersion,
    string DescriptorFingerprint,
    string SourceGenerationFingerprint,
    string RenderVersion,
    string BodySha256,
    string Body);

public static class ContextCache
{
    public static bool IsUsable(
        ContextCacheRecord cache,
        string descriptorFingerprint,
        string sourceGenerationFingerprint,
        string renderVersion,
        out ContextDiagnostic? diagnostic)
    {
        if (cache.SchemaVersion != 1 || cache.DescriptorFingerprint != descriptorFingerprint ||
            cache.SourceGenerationFingerprint != sourceGenerationFingerprint ||
            cache.RenderVersion != renderVersion)
        {
            diagnostic = new ContextDiagnostic("CACHE_PROVENANCE_MISMATCH", "Cache provenance does not match the current verified inputs.");
            return false;
        }

        if (!string.Equals(TaskDescriptor.Sha256(cache.Body), cache.BodySha256, StringComparison.Ordinal))
        {
            diagnostic = new ContextDiagnostic("CACHE_TAMPERED", "Cache body hash does not match its recorded value.");
            return false;
        }

        diagnostic = null;
        return true;
    }
}

public sealed record WatchCapability(bool Supported, string Code, string Detail);

public static class ExactFileWatchProbe
{
    public static WatchCapability Probe() => new(
        false,
        "NATIVE_EXACT_WATCH_UNAVAILABLE",
        "This build does not claim a native exact-file subscription; directory watcher fallback is intentionally disabled.");
}

public sealed record SessionFreshnessResult(bool RequiresRestart, string Code, string Detail);

public static class SessionFreshness
{
    public static SessionFreshnessResult OnHostContextLoss() => new(
        true,
        "SESSION_REINJECTION_REQUIRED",
        "Host context loss requires a supported reinjection or restart; a canary does not establish compliance.");
}

public sealed record MemoryTarget(
    string FileId,
    string RelativePath,
    string ExpectedRawSha256,
    string Classification,
    DateTimeOffset CreatedUtc);

public sealed record MemoryChangePlan(
    string PlanId,
    MemoryTarget Target,
    string ProposedMarkdown,
    string ProposedRawSha256,
    bool RequiresDisposition,
    string? DispositionReason);

public static class MemoryPlanner
{
    public static MemoryChangePlan Create(
        MemoryTarget target,
        string proposedMarkdown,
        string expectedBeforeRawSha256,
        bool semanticConflict)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposedMarkdown);
        if (!string.Equals(target.ExpectedRawSha256, expectedBeforeRawSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContextCatalogException("MEMORY_PRECONDITION_MISMATCH", "The supplied before-hash does not match the selected target.");
        }

        return new MemoryChangePlan(
            Guid.NewGuid().ToString("D"),
            target,
            proposedMarkdown,
            TaskDescriptor.Sha256(proposedMarkdown),
            semanticConflict,
            semanticConflict ? "SEMANTIC_CONFLICT_REQUIRES_DISPOSITION" : null);
    }
}

public static class ReadableIndexGenerator
{
    public static string RenderRoutes(IEnumerable<ContextRoute> routes)
    {
        var builder = new StringBuilder("# Context route index\n\n");
        foreach (var route in routes.OrderBy(route => route.RouteId, StringComparer.Ordinal))
        {
            builder.Append("- ")
                .Append(route.RouteId)
                .Append(" -> ")
                .Append(route.RelativePath)
                .Append(" (")
                .Append(route.LoadPolicy.ToString().ToLowerInvariant())
                .AppendLine(")");
        }

        return builder.ToString();
    }
}
