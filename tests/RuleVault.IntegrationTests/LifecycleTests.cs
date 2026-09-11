using RuleVault.Installation;
using Xunit;

namespace RuleVault.IntegrationTests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task Mig001AndMig002NewInstallIsIdempotent()
    {
        var root = CreateTempDirectory();
        var config = Path.Combine(root, "config");
        var vault = Path.Combine(root, "vault");
        try
        {
            var now = DateTimeOffset.Parse("2026-09-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            var plan = await LifecyclePlanner.CreateNewInstallAsync(config, vault, "00000000-0000-0000-0000-000000000001", now, cancellationToken: TestContext.Current.CancellationToken);
            var first = await LifecycleApplier.ApplyAsync(plan, [], now, TestContext.Current.CancellationToken);
            var core = Path.Combine(vault, "global", "core-runtime.md");
            var before = File.GetLastWriteTimeUtc(core);
            var second = await LifecycleApplier.ApplyAsync(plan, [], now, TestContext.Current.CancellationToken);

            Assert.Equal(5, first.Count);
            Assert.Equal(5, second.Count);
            Assert.Equal(before, File.GetLastWriteTimeUtc(core));
            Assert.True(File.Exists(Path.Combine(config, "vault-registry.json")));
            Assert.True(File.Exists(Path.Combine(vault, ".vault-system", "content-integrity.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Mig004AndMig015FutureOrTamperedPlanBlocks()
    {
        var now = DateTimeOffset.Parse("2026-09-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var plan = await LifecyclePlanner.CreateNewInstallAsync("C:/temp/config", "C:/temp/vault", "00000000-0000-0000-0000-000000000001", now, cancellationToken: TestContext.Current.CancellationToken);

        var future = plan with { SchemaVersion = 2 };
        var tampered = plan with { VaultRoot = "C:/other" };

        Assert.Equal("PLAN_SCHEMA_UNSUPPORTED", LifecyclePlanner.Validate(future, now, [], future.VaultId, future.ConfigRoot, future.VaultRoot).Code);
        Assert.Equal("PLAN_STALE", LifecyclePlanner.Validate(tampered, now, [], plan.VaultId, plan.ConfigRoot, plan.VaultRoot).Code);
    }

    [Fact]
    public void Mig006ThroughMig014PreservationAndDecisionsFailClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var operation = new LifecycleOperation(
            "op-1",
            LifecycleOperationKind.Preserve,
            "custom.md",
            "unknown",
            null,
            "unknown",
            ["RV-WRITE-001"]);
        var plan = LifecyclePlanner.CreatePreservationPlan(
            LifecycleMode.Migrate,
            "C:/temp/config",
            "C:/temp/vault",
            "00000000-0000-0000-0000-000000000001",
            [operation],
            ["choose-custom-content"],
            now);

        var missing = LifecyclePlanner.Validate(plan, now, [], plan.VaultId, plan.ConfigRoot, plan.VaultRoot);
        var answer = new LifecycleDecision("choose-custom-content", plan.PlanSha256, "unknown", "preserve");
        var accepted = LifecyclePlanner.Validate(plan, now, [answer], plan.VaultId, plan.ConfigRoot, plan.VaultRoot);

        Assert.False(missing.Allowed);
        Assert.Equal("DECISION_REQUIRED", missing.Code);
        Assert.True(accepted.Allowed);
        Assert.Equal(LifecycleOperationKind.Preserve, plan.Operations[0].Kind);
    }

    [Fact]
    public void Mig016DecisionCannotBindToAnotherPlan()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = LifecyclePlanner.CreatePreservationPlan(
            LifecycleMode.Update,
            "C:/temp/config",
            "C:/temp/vault",
            "00000000-0000-0000-0000-000000000001",
            [],
            ["decision"],
            now);
        var foreign = new LifecycleDecision("decision", "other", "before", "yes");

        Assert.Equal("DECISION_REQUIRED", LifecyclePlanner.Validate(plan, now, [foreign], plan.VaultId, plan.ConfigRoot, plan.VaultRoot).Code);
    }

    [Fact]
    public void Mig018AndMig019UninstallPreservesVaultAndScriptsAreInert()
    {
        var uninstall = LifecyclePlanner.CreatePreservationPlan(
            LifecycleMode.Uninstall,
            "C:/temp/config",
            "C:/temp/vault",
            "00000000-0000-0000-0000-000000000001",
            [new LifecycleOperation("op", LifecycleOperationKind.Preserve, "history/keep.md", "hash", null, "user", [])],
            [],
            DateTimeOffset.UtcNow);

        Assert.Equal(LifecycleMode.Uninstall, uninstall.Mode);
        Assert.Equal(LifecycleOperationKind.Preserve, uninstall.Operations[0].Kind);
        Assert.DoesNotContain(uninstall.Operations, operation => operation.RelativePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
