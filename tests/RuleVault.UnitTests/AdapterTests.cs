using RuleVault.Agents;
using Xunit;

namespace RuleVault.UnitTests;

public sealed class AdapterTests
{
    [Fact]
    public void Adp001DiscoveryIsBoundedAndDoesNotExecuteCandidates()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".codex"));
            File.WriteAllText(Path.Combine(root, ".codex", "AGENTS.md"), "fixture");

            var discovery = BuiltInAdapters.Discover(BuiltInAdapters.Get("codex-cli"), root);

            Assert.False(discovery.ExecutableExecuted);
            Assert.Single(discovery.ExistingCandidates);
            Assert.EndsWith(Path.Combine(".codex", "AGENTS.md"), discovery.ExistingCandidates[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Adp002OwnedBlockPreservesExternalBytesAndIsIdempotent()
    {
        var bootstrap = OwnedMarkdownBlock.Bootstrap("codex-cli", "rv");
        var source = "before\n";
        var added = OwnedMarkdownBlock.Apply(source, "codex-cli", bootstrap, OwnedBlockAction.Add);
        var repeated = OwnedMarkdownBlock.Apply(added.Content!, "codex-cli", bootstrap, OwnedBlockAction.Update);
        var removed = OwnedMarkdownBlock.Apply(added.Content!, "codex-cli", bootstrap, OwnedBlockAction.Remove);

        Assert.True(added.Changed);
        Assert.False(repeated.Changed);
        Assert.Equal("before\n", removed.Content);
    }

    [Fact]
    public void Adp003ModifiedOrDuplicateBlocksConflict()
    {
        var bootstrap = OwnedMarkdownBlock.Bootstrap("codex-cli", "rv");
        var single = OwnedMarkdownBlock.Apply("", "codex-cli", bootstrap, OwnedBlockAction.Add).Content!;
        var modified = single.Replace(bootstrap, "user changed", StringComparison.Ordinal);
        var duplicate = single + single;

        Assert.Equal("ADAPTER_BLOCK_MODIFIED", OwnedMarkdownBlock.Apply(modified, "codex-cli", bootstrap, OwnedBlockAction.Remove).Code);
        Assert.Equal("ADAPTER_BLOCK_CONFLICT", OwnedMarkdownBlock.Apply(duplicate, "codex-cli", bootstrap, OwnedBlockAction.Remove).Code);
    }

    [Fact]
    public void Adp006AndAdp008CapabilitiesRemainHonest()
    {
        var capabilities = AgentCapabilities.Unverified(BuiltInAdapters.Get("codex-cli"));

        Assert.Contains(capabilities, capability => capability.Name == "startup_delivery" && capability.Status == "unverified");
        Assert.Contains(capabilities, capability => capability.Name == "os_filesystem_isolation" && capability.Status == "unsupported");
    }

    [Fact]
    public void Adp007BootstrapStaysSmallAndRetainsBoundary()
    {
        var bootstrap = OwnedMarkdownBlock.Bootstrap("claude-code", "rv");

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(bootstrap) <= 2000);
        Assert.Contains("required agent context startup", bootstrap, StringComparison.Ordinal);
        Assert.Contains("does not permit bypassing", bootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapDiscoveryAndApplyAreBoundedIdempotentAndPreserveUnrelatedText()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".codex"));
            Directory.CreateDirectory(Path.Combine(root, ".claude"));
            Directory.CreateDirectory(Path.Combine(root, ".gemini"));
            await File.WriteAllTextAsync(Path.Combine(root, ".codex", "AGENTS.md"), "Rule Vault adapter: codex-cli. Use the AI-Rule-Vault descriptor.", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, ".claude", "CLAUDE.md"), "# Team instructions\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, ".gemini", "GEMINI.md"), "Rule Vault appears in prose but this file has unrelated user material.", TestContext.Current.CancellationToken);

            var found = await AgentBootstrapManager.DiscoverAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal("legacy-replace", found.Single(item => item.AdapterId == "codex-cli").Status);
            Assert.Equal("ready-add", found.Single(item => item.AdapterId == "claude-code").Status);
            Assert.Equal("legacy-ambiguous", found.Single(item => item.AdapterId == "gemini-cli").Status);

            var first = await AgentBootstrapManager.ApplyAsync(root, "C:/fixture/config/rule-vault-cli.json", null, true, TestContext.Current.CancellationToken);
            Assert.Contains(first, item => item.AdapterId == "codex-cli" && item.Status == "LEGACY_REPLACED" && item.Changed);
            Assert.Contains(first, item => item.AdapterId == "claude-code" && item.Changed);
            Assert.Contains(first, item => item.AdapterId == "gemini-cli" && item.Status == "skipped-legacy-ambiguous" && !item.Changed);
            var codex = await File.ReadAllTextAsync(Path.Combine(root, ".codex", "AGENTS.md"), TestContext.Current.CancellationToken);
            var claude = await File.ReadAllTextAsync(Path.Combine(root, ".claude", "CLAUDE.md"), TestContext.Current.CancellationToken);
            Assert.Equal(1, Count(codex, "<!-- rule-vault:begin"));
            Assert.Contains("# Team instructions", claude, StringComparison.Ordinal);
            Assert.Equal(1, Count(claude, "<!-- rule-vault:begin"));

            var second = await AgentBootstrapManager.ApplyAsync(root, "C:/fixture/config/rule-vault-cli.json", null, true, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(second, item => item.Changed && item.AdapterId is "codex-cli" or "claude-code");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Run001ThroughRun010LaunchChecksCapabilitiesAndCleansOwnedPacket()
    {
        var root = CreateTempDirectory();
        try
        {
            var packet = new RuleVault.Core.ContextPacket("body", "hash", 4, 4, 1, [], []);
            var runner = new FakeRunner();
            var launcher = new SupervisedLauncher(runner);
            var registration = new AgentRegistration("codex-cli", Path.Combine(root, "fake agent.exe"), true, "hash");

            var strict = await launcher.LaunchAsync(
                new AgentLaunchRequest(registration, packet, ["quoted value", "ユニコード"], ["os_filesystem_isolation"]),
                root,
                TestContext.Current.CancellationToken);
            var result = await launcher.LaunchAsync(
                new AgentLaunchRequest(registration, packet, ["quoted value", "ユニコード"], ["process_tree_cleanup"]),
                root,
                TestContext.Current.CancellationToken);

            Assert.Equal("REQUIRED_CAPABILITY_UNSUPPORTED", strict.Code);
            Assert.Equal("OK", result.Code);
            Assert.Equal(["quoted value", "ユニコード"], runner.Arguments);
            Assert.False(File.Exists(runner.PacketPath!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AgentWriteIsMediatedForManagedVaultContentAndCannotTouchSystemPaths()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".vault-system"));
            await File.WriteAllTextAsync(Path.Combine(root, ".vault-system", "vault.json"), "{\"schema_version\":1,\"vault_id\":\"00000000-0000-0000-0000-000000000001\"}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, ".vault-system", "content-integrity.json"), "{\"schema_version\":1,\"vault_id\":\"00000000-0000-0000-0000-000000000001\",\"files\":[]}", TestContext.Current.CancellationToken);

            var created = await AgentVaultWriter.WriteAsync(new AgentVaultWriteRequest(
                root, "codex-cli", "projects/fixture/context/result.md", Managed("Fixture result"), null), TestContext.Current.CancellationToken);

            Assert.True(created.Created);
            await Assert.ThrowsAsync<AgentVaultWriteException>(() => AgentVaultWriter.WriteAsync(new AgentVaultWriteRequest(
                root, "codex-cli", ".vault-system/write-safety.md", Managed("not allowed"), null), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<AgentVaultWriteException>(() => AgentVaultWriter.WriteAsync(new AgentVaultWriteRequest(
                root, "codex-cli", ".agents/AGENTS.md", Managed("not allowed"), null), TestContext.Current.CancellationToken));
            Assert.Contains("Fixture result", await File.ReadAllTextAsync(Path.Combine(root, "projects", "fixture", "context", "result.md"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static int Count(string content, string value)
    {
        var count = 0; var offset = 0;
        while ((offset = content.IndexOf(value, offset, StringComparison.Ordinal)) >= 0) { count++; offset += value.Length; }
        return count;
    }

    private static string Managed(string title) => $"---\nfile_id: \"{Guid.NewGuid():D}\"\ntitle: \"{title}\"\nproject: \"fixture\"\nkind: \"context\"\nstatus: \"active\"\naliases: []\nhasInboundLinks: false\n---\n\n# {title}\n";

    private sealed class FakeRunner : IAgentProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public string? PacketPath { get; private set; }

        public Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, string packetPath, CancellationToken cancellationToken)
        {
            Arguments = arguments.ToArray();
            PacketPath = packetPath;
            Assert.True(File.Exists(packetPath));
            return Task.FromResult(0);
        }
    }
}
