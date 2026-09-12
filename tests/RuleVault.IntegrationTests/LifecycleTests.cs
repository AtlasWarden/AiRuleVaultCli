using RuleVault.Installation;
using RuleVault.Storage;
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

    [Fact]
    public async Task IntegrityRepairArchivesDivergenceAndRestoresPackageBeforeUpdate()
    {
        var root = CreateTempDirectory();
        var config = Path.Combine(root, "config");
        var vault = Path.Combine(root, "vault");
        var package = CreatePackage(root, "# Current trusted runtime\n");
        const string vaultId = "00000000-0000-0000-0000-000000000001";
        var now = DateTimeOffset.Parse("2026-09-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var install = await LifecyclePlanner.CreateNewInstallAsync(config, vault, vaultId, now, package, new string('A', 64), TestContext.Current.CancellationToken);
            await LifecycleApplier.ApplyAsync(install, [], now, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(vault, "global", "core-runtime.md"), "# Divergent protected runtime\n", TestContext.Current.CancellationToken);

            var registryPath = Path.Combine(config, "vault-registry.json");
            var registry = await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken);
            var document = System.Text.Json.Nodes.JsonNode.Parse(registry)!.AsObject();
            document["vaults"]![0]!["protected_content_manifest_sha256"] = new string('0', 64);
            await File.WriteAllTextAsync(registryPath, document.ToJsonString(new() { WriteIndented = true }) + "\n", TestContext.Current.CancellationToken);

            var repair = await LifecyclePlanner.CreateIntegrityRepairAsync(config, vault, vaultId, package, "restore-package", now.AddMinutes(1), TestContext.Current.CancellationToken);
            Assert.False(repair.Diagnosis.ManifestAnchorMatches);
            Assert.Contains(repair.Diagnosis.ProtectedFiles, item => item.RelativePath == "global/core-runtime.md" && item.RepairAction == "archived-and-restored-package");
            await Assert.ThrowsAsync<LifecycleException>(() => LifecycleApplier.ApplyAsync(repair.Plan, [], now.AddMinutes(1), TestContext.Current.CancellationToken));

            var decision = new LifecycleDecision(repair.Plan.RequiredDecisions.Single(), repair.Plan.PlanSha256, repair.Plan.PlanSha256, "accepted");
            await LifecycleApplier.ApplyAsync(repair.Plan, [decision], now.AddMinutes(1), TestContext.Current.CancellationToken);

            Assert.Equal("# Current trusted runtime\n", await File.ReadAllTextAsync(Path.Combine(vault, "global", "core-runtime.md"), TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(vault, repair.ArchiveRoot.Replace('/', Path.DirectorySeparatorChar), "divergent", "global", "core-runtime.md")));
            var manifest = await File.ReadAllTextAsync(Path.Combine(vault, ".vault-system", "content-integrity.json"), TestContext.Current.CancellationToken);
            var repairedRegistry = await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken);
            Assert.Contains(CanonicalHash.Text(manifest).ToUpperInvariant(), repairedRegistry, StringComparison.Ordinal);

            var update = await LifecyclePlanner.CreateAdditiveUpdateAsync(config, vault, vaultId, package, new string('B', 64), now.AddMinutes(2), TestContext.Current.CancellationToken);
            await LifecycleApplier.ApplyAsync(update.Plan, [], now.AddMinutes(2), TestContext.Current.CancellationToken);
            Assert.Equal(0, update.PreservedDivergentFiles);
            Assert.True(update.ReadMetrics.CacheHits >= 3, "Update planning should reuse the manifest-verified content and registry snapshots.");
            Assert.Equal(5, update.ReadMetrics.PhysicalReads);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IntegrityRepairCanAcceptCurrentProtectedBytesWithoutBypassingChecks()
    {
        var root = CreateTempDirectory();
        var config = Path.Combine(root, "config");
        var vault = Path.Combine(root, "vault");
        var package = CreatePackage(root, "# Original runtime\n");
        const string vaultId = "00000000-0000-0000-0000-000000000001";
        var now = DateTimeOffset.Parse("2026-09-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var install = await LifecyclePlanner.CreateNewInstallAsync(config, vault, vaultId, now, package, new string('A', 64), TestContext.Current.CancellationToken);
            await LifecycleApplier.ApplyAsync(install, [], now, TestContext.Current.CancellationToken);
            const string accepted = "# User-approved protected runtime\n";
            await File.WriteAllTextAsync(Path.Combine(vault, "global", "core-runtime.md"), accepted, TestContext.Current.CancellationToken);

            var blocked = await Assert.ThrowsAsync<LifecycleException>(() => LifecyclePlanner.CreateAdditiveUpdateAsync(config, vault, vaultId, package, new string('A', 64), now.AddSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal("INTEGRITY_HASH_MISMATCH", blocked.Code);

            var repair = await LifecyclePlanner.CreateIntegrityRepairAsync(config, vault, vaultId, package, "accept-current", now.AddMinutes(1), TestContext.Current.CancellationToken);
            var decision = new LifecycleDecision(repair.Plan.RequiredDecisions.Single(), repair.Plan.PlanSha256, repair.Plan.PlanSha256, "accepted");
            await LifecycleApplier.ApplyAsync(repair.Plan, [decision], now.AddMinutes(1), TestContext.Current.CancellationToken);

            Assert.Equal(accepted, await File.ReadAllTextAsync(Path.Combine(vault, "global", "core-runtime.md"), TestContext.Current.CancellationToken));
            var manifest = await File.ReadAllTextAsync(Path.Combine(vault, ".vault-system", "content-integrity.json"), TestContext.Current.CancellationToken);
            Assert.Contains(CanonicalHash.Text(accepted).ToUpperInvariant(), manifest, StringComparison.Ordinal);
            Assert.Contains("user-approved-integrity-repair", manifest, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateEnrollsLegacyMandatoryIndexesEvenWhenGuideIsAlreadyCurrent()
    {
        var root = CreateTempDirectory();
        var config = Path.Combine(root, "config");
        var vault = Path.Combine(root, "vault");
        var package = CreatePackage(root, "# Runtime\n");
        const string vaultId = "00000000-0000-0000-0000-000000000001";
        var guide = new string('A', 64);
        var now = DateTimeOffset.Parse("2026-09-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var install = await LifecyclePlanner.CreateNewInstallAsync(config, vault, vaultId, now, package, guide, TestContext.Current.CancellationToken);
            await LifecycleApplier.ApplyAsync(install, [], now, TestContext.Current.CancellationToken);
            var legacyIndex = Path.Combine(vault, "projects", "legacy", "index.md");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyIndex)!);
            const string legacyContent = "---\nfile_id: \"00000000-0000-0000-0000-000000000777\"\ntitle: \"Legacy Project\"\nproject: \"legacy\"\nkind: \"index\"\nstatus: \"active\"\naliases: []\nhasInboundLinks: false\ncreated: \"2026-01-01 00:00:00 UTC\"\n---\n\n# Legacy project index\n";
            await File.WriteAllTextAsync(legacyIndex, legacyContent, TestContext.Current.CancellationToken);

            var update = await LifecyclePlanner.CreateAdditiveUpdateAsync(config, vault, vaultId, package, guide, now.AddMinutes(1), TestContext.Current.CancellationToken);
            Assert.Contains(update.Plan.Operations, operation => operation.RelativePath == ".vault-system/content-integrity.json" && operation.Kind == LifecycleOperationKind.Replace);
            await LifecycleApplier.ApplyAsync(update.Plan, [], now.AddMinutes(1), TestContext.Current.CancellationToken);
            var manifest = await File.ReadAllTextAsync(Path.Combine(vault, ".vault-system", "content-integrity.json"), TestContext.Current.CancellationToken);
            Assert.Contains("projects/legacy/index.md", manifest, StringComparison.Ordinal);
            Assert.Equal(legacyContent, await File.ReadAllTextAsync(legacyIndex, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreatePackage(string root, string coreRuntime)
    {
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(Path.Combine(package, "runtime-payload", "global"));
        File.WriteAllText(Path.Combine(package, "runtime-payload", "index.md"), "# Rule Vault Index\n");
        File.WriteAllText(Path.Combine(package, "runtime-payload", "global", "core-runtime.md"), coreRuntime);
        File.WriteAllText(Path.Combine(package, "migrations.json"), "{\"schema_version\":1,\"rules\":[]}");
        return package;
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
