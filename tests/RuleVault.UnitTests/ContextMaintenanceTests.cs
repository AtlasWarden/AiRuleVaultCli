using RuleVault.Core;
using Xunit;

namespace RuleVault.UnitTests;

public sealed class ContextMaintenanceTests
{
    [Fact]
    public void Ctx009RejectsStaleAndTamperedCache()
    {
        var descriptor = TaskDescriptor.Create("project-a", TaskOperation.Read);
        var cache = new ContextCacheRecord(1, descriptor.Fingerprint(), "source-a", "v1", TaskDescriptor.Sha256("body"), "body");

        Assert.True(ContextCache.IsUsable(cache, descriptor.Fingerprint(), "source-a", "v1", out var valid));
        Assert.Null(valid);
        Assert.False(ContextCache.IsUsable(cache, descriptor.Fingerprint(), "source-b", "v1", out var stale));
        Assert.Equal("CACHE_PROVENANCE_MISMATCH", stale!.Code);
        Assert.False(ContextCache.IsUsable(cache with { Body = "tampered" }, descriptor.Fingerprint(), "source-a", "v1", out var tampered));
        Assert.Equal("CACHE_TAMPERED", tampered!.Code);
    }

    [Fact]
    public void Ctx010ReportsExactWatchCapabilityHonestly()
    {
        var capability = ExactFileWatchProbe.Probe();

        Assert.False(capability.Supported);
        Assert.Equal("NATIVE_EXACT_WATCH_UNAVAILABLE", capability.Code);
    }

    [Fact]
    public void Ctx011RequiresSupportedRestartAfterContextLoss()
    {
        var result = SessionFreshness.OnHostContextLoss();

        Assert.True(result.RequiresRestart);
        Assert.Equal("SESSION_REINJECTION_REQUIRED", result.Code);
    }

    [Fact]
    public void Mem001AndMem005PreservePreconditionsAndRequireDisposition()
    {
        var target = new MemoryTarget(
            "file-1",
            "global/rule.md",
            "before",
            "protected-rule",
            DateTimeOffset.Parse("2026-09-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        var plan = MemoryPlanner.Create(target, "proposed", "before", semanticConflict: true);

        Assert.Equal("before", plan.Target.ExpectedRawSha256);
        Assert.True(plan.RequiresDisposition);
        Assert.Equal("SEMANTIC_CONFLICT_REQUIRES_DISPOSITION", plan.DispositionReason);
        Assert.Equal(
            "MEMORY_PRECONDITION_MISMATCH",
            Assert.Throws<ContextCatalogException>(() => MemoryPlanner.Create(target, "proposed", "other", false)).Code);
    }

    [Fact]
    public void Mem002AndMem003PreserveIdentityAndRenderDeterministicIndex()
    {
        var routeA = Route("b");
        var routeB = Route("a");
        var first = ReadableIndexGenerator.RenderRoutes([routeA, routeB]);
        var second = ReadableIndexGenerator.RenderRoutes([routeB, routeA]);

        Assert.Equal(first, second);
        Assert.Contains("a -> context/a.md", first, StringComparison.Ordinal);
        Assert.Contains("b -> context/b.md", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Mem004ReadOnlyPlanningDoesNotInventContinuityWrites()
    {
        var target = new MemoryTarget("file-1", "daily/state.md", "before", "state", DateTimeOffset.UtcNow);

        var plan = MemoryPlanner.Create(target, "state body", "before", semanticConflict: false);

        Assert.False(plan.RequiresDisposition);
        Assert.Equal(target.CreatedUtc, plan.Target.CreatedUtc);
        Assert.Equal("file-1", plan.Target.FileId);
    }

    private static ContextRoute Route(string id) => new(
        id,
        $"file-{id}",
        $"context/{id}.md",
        "rule",
        RouteScope.Global,
        LoadPolicy.Always,
        [],
        [TaskOperation.Read],
        [],
        0,
        [],
        ContextAudience.Private);
}
