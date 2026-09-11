using System.Text;
using System.Text.Json;
using RuleVault.Agents;
using RuleVault.Storage;
using Xunit;

namespace RuleVault.IntegrationTests;

public sealed class VaultAuthoringTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    [Fact]
    public async Task ProtectedWriteReanchorsManifestAndRegistry()
    {
        var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            var before = await VaultAuthoring.ReadAsync(fixture.Vault, "index.md", TestContext.Current.CancellationToken);
            var replacement = Fixture.Managed("Vault Index", "global", "index", "# Rule Vault Index\n\n## Projects\n\nUpdated by an explicit protected mutation.\n");
            // Keep the immutable ID of an existing document when changing it.
            replacement = replacement.Replace(Fixture.LastCreatedId, Fixture.RootIndexId, StringComparison.Ordinal);
            var result = await VaultAuthoring.WriteAsync(new VaultWriteRequest(fixture.Vault, fixture.Config, "index.md", replacement, before.RawSha256), TestContext.Current.CancellationToken);

            Assert.True(result.IntegrityUpdated);
            var manifest = await File.ReadAllTextAsync(Path.Combine(fixture.Vault, ".vault-system", "content-integrity.json"), TestContext.Current.CancellationToken);
            using var manifestJson = StrictJson.Parse(Encoding.UTF8.GetBytes(manifest));
            Assert.Equal(CanonicalHash.Text(replacement).ToUpperInvariant(), manifestJson.RootElement.GetProperty("files")[0].GetProperty("sha256").GetString());
            var registry = await File.ReadAllTextAsync(Path.Combine(fixture.Config, "vault-registry.json"), TestContext.Current.CancellationToken);
            using var registryJson = StrictJson.Parse(Encoding.UTF8.GetBytes(registry));
            Assert.Equal(CanonicalHash.Text(manifest).ToUpperInvariant(), registryJson.RootElement.GetProperty("vaults")[0].GetProperty("protected_content_manifest_sha256").GetString());
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task ProjectCreationAndDailyRolloverRequireExplicitPromotionGate()
    {
        var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            var created = await VaultAuthoring.CreateProjectAsync(new ProjectCreateRequest(fixture.Vault, fixture.Config, "sample-project", "Sample Project", null, null), TestContext.Current.CancellationToken);
            Assert.Contains("projects/sample-project/context/current-state.md", created.CreatedPaths);
            var first = await VaultAuthoring.InspectDailyAsync(fixture.Vault, "sample-project", DateOnly.FromDateTime(DateTime.Now).AddDays(1), TestContext.Current.CancellationToken);
            Assert.True(first.RolloverRequired);
            var blocked = await Assert.ThrowsAsync<VaultAuthoringException>(() => VaultAuthoring.RolloverDailyAsync(fixture.Vault, "sample-project", first.ActiveDate.AddDays(1), first.ActiveRawSha256, false, TestContext.Current.CancellationToken));
            Assert.Equal("PROMOTIONS_CONFIRMATION_REQUIRED", blocked.Code);
            var changed = await VaultAuthoring.RolloverDailyAsync(fixture.Vault, "sample-project", first.ActiveDate.AddDays(1), first.ActiveRawSha256, true, TestContext.Current.CancellationToken);
            Assert.Contains(changed, path => path.EndsWith("daily/index.md", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(fixture.Vault, "projects", "sample-project", "daily", "archive", $"{first.ActiveDate:yyyy}", $"{first.ActiveDate:MMM}", $"{first.ActiveDate:dd}.md")));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task ManagedLinkWritesMaintainInboundMetadata()
    {
        var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            var targetPath = "projects/p/context/target.md";
            await VaultAuthoring.WriteAsync(new VaultWriteRequest(fixture.Vault, null, targetPath, Fixture.Managed("Target", "p", "context", "# Target\n"), null), TestContext.Current.CancellationToken);
            var sourcePath = "projects/p/context/source.md";
            await VaultAuthoring.WriteAsync(new VaultWriteRequest(fixture.Vault, null, sourcePath, Fixture.Managed("Source", "p", "context", "# Source\n\n[Target](./target.md)\n"), null), TestContext.Current.CancellationToken);
            var target = await VaultAuthoring.ReadAsync(fixture.Vault, targetPath, TestContext.Current.CancellationToken);
            Assert.Contains("hasInboundLinks: true", target.Content, StringComparison.Ordinal);
            var id = RegexId(target.Content);
            var sidecar = await File.ReadAllTextAsync(Path.Combine(fixture.Vault, ".vault-system", "references", id + ".json"), TestContext.Current.CancellationToken);
            Assert.Contains(sourcePath, sidecar, StringComparison.Ordinal);
            var links = await VaultAuthoring.InspectInboundLinksAsync(fixture.Vault, targetPath, TestContext.Current.CancellationToken);
            Assert.Equal([sourcePath], links.InboundSources);
            var blocked = await Assert.ThrowsAsync<VaultAuthoringException>(() => VaultAuthoring.DeleteAsync(fixture.Vault, targetPath, target.RawSha256, TestContext.Current.CancellationToken));
            Assert.Equal("INBOUND_LINKS_REQUIRE_UPDATE", blocked.Code);
            var source = await VaultAuthoring.ReadAsync(fixture.Vault, sourcePath, TestContext.Current.CancellationToken);
            await VaultAuthoring.WriteAsync(new VaultWriteRequest(fixture.Vault, null, sourcePath, source.Content.Replace("[Target](./target.md)\n", string.Empty, StringComparison.Ordinal), source.RawSha256), TestContext.Current.CancellationToken);
            var unlinked = await VaultAuthoring.ReadAsync(fixture.Vault, targetPath, TestContext.Current.CancellationToken);
            var deleted = await VaultAuthoring.DeleteAsync(fixture.Vault, targetPath, unlinked.RawSha256, TestContext.Current.CancellationToken);
            Assert.Contains(targetPath, deleted);
            Assert.False(File.Exists(Path.Combine(fixture.Vault, targetPath.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task GlobalDailyStreamIsCreatedLazilyForWorkWithoutAProject()
    {
        var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            var daily = await VaultAuthoring.InspectDailyAsync(fixture.Vault, "global", new DateOnly(2026, 9, 10), TestContext.Current.CancellationToken);
            Assert.Equal("global/daily/active/2026-09-10.md", daily.ActiveRelativePath);
            Assert.True(File.Exists(Path.Combine(fixture.Vault, "global", "daily", "index.md")));
            Assert.Contains("# Global Daily Notes", await File.ReadAllTextAsync(Path.Combine(fixture.Vault, "global", "daily", "index.md"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task OpaqueAgentSessionsInvalidateReadersAndReportPerFileTokenUsage()
    {
        var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "thread-a", "--name", "Author", "--folder", "C:/work/project-a", "--project", "project-a", "--agent-kind", "codex");
            await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "thread-b", "--name", "Reviewer", "--folder", "C:/work/project-a", "--project", "project-a", "--agent-kind", "claude");
            using var authorRead = await Cli("agent", "read", "--config-root", fixture.Config, "--session-id", "thread-a", "--path", "index.md");
            using var reviewerRead = await Cli("agent", "read", "--config-root", fixture.Config, "--session-id", "thread-b", "--path", "index.md");
            var authorData = authorRead.RootElement.GetProperty("data");
            var reviewerData = reviewerRead.RootElement.GetProperty("data");
            Assert.True(authorData.GetProperty("read_tokens").GetInt32() > 0);
            var updated = authorData.GetProperty("content").GetString()!.Replace("## Projects\n", "## Projects\n\nSession-authored update.\n", StringComparison.Ordinal);
            await Cli("agent", "write", "--config-root", fixture.Config, "--session-id", "thread-a", "--path", "index.md", "--expected-raw-sha256", authorData.GetProperty("raw_sha256").GetString()!, "--content", updated);

            using var stale = await Cli("agent", "usage", "--config-root", fixture.Config, "--session-id", "thread-b");
            Assert.Equal("stale", stale.RootElement.GetProperty("data").GetProperty("sessions")[0].GetProperty("freshness").GetString());
            using var refresh = await Cli("agent", "read", "--config-root", fixture.Config, "--session-id", "thread-b", "--path", "index.md");
            Assert.NotEqual(reviewerData.GetProperty("basis_hash").GetString(), refresh.RootElement.GetProperty("data").GetProperty("basis_hash").GetString());
            using var detail = await Cli("agent", "usage", "--config-root", fixture.Config, "--detail", "files");
            var file = detail.RootElement.GetProperty("data").GetProperty("files")[0];
            Assert.Equal("index.md", file.GetProperty("relative_path").GetString());
            Assert.True(file.GetProperty("read_tokens").GetInt64() > 0);
            Assert.True(file.GetProperty("write_tokens").GetInt64() > 0);
            Assert.DoesNotContain(fixture.Vault, detail.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task AgentSessionsCanBeClearedWithoutTouchingVaultContent()
    {
        var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "thread-clear", "--name", "Clearable", "--folder", "C:/work/project-a", "--project", "project-a", "--agent-kind", "continue");
            var before = await File.ReadAllTextAsync(Path.Combine(fixture.Vault, "index.md"), TestContext.Current.CancellationToken);
            using var clearOne = await Cli("agent", "clear", "--config-root", fixture.Config, "--session-id", "thread-clear");
            Assert.Equal(1, clearOne.RootElement.GetProperty("data").GetProperty("removed").GetInt32());
            Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(fixture.Vault, "index.md"), TestContext.Current.CancellationToken));
            using var clearAll = await Cli("agent", "clear", "--config-root", fixture.Config, "--all", "true");
            Assert.Equal(0, clearAll.RootElement.GetProperty("data").GetProperty("removed").GetInt32());
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task RepositoryAgentsAdapterAllowsRegularTrackedMarkdownAndRejectsSystemMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init");
            await Git(root, "config user.email fixture@example.invalid");
            await Git(root, "config user.name Fixture");
            Directory.CreateDirectory(Path.Combine(root, ".agents"));
            var path = Path.Combine(root, ".agents", "roles.md");
            await File.WriteAllTextAsync(path, "before", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, ".agents", "content-integrity.json"), "{\"schema_version\":1,\"branch_marker\":\"intentionally-divergent-branch-data\",\"files\":[]}", TestContext.Current.CancellationToken);
            await Git(root, "add -- .agents/roles.md .agents/content-integrity.json");
            var read = await RepositoryAgentsAuthoring.ReadAsync(root, ".agents/roles.md", TestContext.Current.CancellationToken);
            var write = await RepositoryAgentsAuthoring.WriteAsync(new RepositoryAgentsWriteRequest(root, ".agents/roles.md", "after", read.RawSha256), TestContext.Current.CancellationToken);
            Assert.False(write.Created);
            Assert.Equal("after", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var denied = await Assert.ThrowsAsync<VaultAuthoringException>(() => RepositoryAgentsAuthoring.WriteAsync(new RepositoryAgentsWriteRequest(root, ".agents/content-integrity.json", "{}", null), TestContext.Current.CancellationToken));
            Assert.Equal("REPOSITORY_AGENTS_PATH_DENIED", denied.Code);
        }
        finally { DeleteFixture(root); }
    }

    [Fact]
    public async Task RepositoryProtectedFileUpdatesItsCurrentBranchManifestUnderLocalLocks()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init"); await Git(root, "config user.email fixture@example.invalid"); await Git(root, "config user.name Fixture");
            Directory.CreateDirectory(Path.Combine(root, ".agents"));
            await File.WriteAllTextAsync(Path.Combine(root, ".agents", "rules.md"), "before", TestContext.Current.CancellationToken);
            var manifest = $"{{\"schema_version\":1,\"revision\":1,\"files\":[{{\"path\":\"rules.md\",\"sha256\":\"{CanonicalHash.Text("before").ToUpperInvariant()}\",\"revision\":1,\"change_origin\":\"installation\",\"changed_at\":\"2026-01-01T00:00:00Z\"}}]}}";
            await File.WriteAllTextAsync(Path.Combine(root, ".agents", "content-integrity.json"), manifest, TestContext.Current.CancellationToken);
            await Git(root, "add -- .agents/rules.md .agents/content-integrity.json");
            var read = await RepositoryAgentsAuthoring.ReadAsync(root, ".agents/rules.md", TestContext.Current.CancellationToken);
            var result = await RepositoryAgentsAuthoring.WriteAsync(new RepositoryAgentsWriteRequest(root, ".agents/rules.md", "after", read.RawSha256), TestContext.Current.CancellationToken);
            Assert.True(result.BranchManifestUpdated);
            var updated = await File.ReadAllTextAsync(Path.Combine(root, ".agents", "content-integrity.json"), TestContext.Current.CancellationToken);
            Assert.Contains(CanonicalHash.Text("after").ToUpperInvariant(), updated, StringComparison.Ordinal);
        }
        finally { DeleteFixture(root); }
    }

    private static string RegexId(string content) => System.Text.RegularExpressions.Regex.Match(content, "file_id: \\\"(?<id>[0-9a-f-]{36})").Groups["id"].Value;

    private static async Task Git(string root, string arguments)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("git", $"-C \"{root}\" {arguments}") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.Start(); var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, error);
    }

    private static async Task<JsonDocument> Cli(params string[] arguments)
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RuleVault.Cli", "RuleVault.Cli.csproj"));
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("run"); process.StartInfo.ArgumentList.Add("--project"); process.StartInfo.ArgumentList.Add(project); process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add("Release"); process.StartInfo.ArgumentList.Add("--no-build"); process.StartInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.ArgumentList.Add("--format"); process.StartInfo.ArgumentList.Add("json");
        process.Start(); var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, error + output);
        return JsonDocument.Parse(output);
    }

    private static void DeleteFixture(string root)
    {
        for (var attempt = 0; attempt < 3 && Directory.Exists(root); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, true);
            }
            catch (UnauthorizedAccessException) when (attempt < 2) { Thread.Sleep(50); }
        }
        Assert.False(Directory.Exists(root), "Git fixture cleanup did not complete.");
    }

    private sealed class Fixture : IDisposable
    {
        public const string VaultId = "00000000-0000-0000-0000-000000000001";
        public static string LastCreatedId { get; private set; } = string.Empty;
        public static string RootIndexId { get; private set; } = string.Empty;
        public string Root { get; }
        public string Vault { get; }
        public string Config { get; }
        private Fixture(string root) { Root = root; Vault = Path.Combine(root, "vault"); Config = Path.Combine(root, "config"); }

        public static async Task<Fixture> CreateAsync(CancellationToken cancellationToken)
        {
            var fixture = new Fixture(Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.Combine(fixture.Vault, ".vault-system")); Directory.CreateDirectory(fixture.Config);
            var index = Managed("Rule Vault Index", "global", "index", "# Rule Vault Index\n\n## Projects\n\n"); RootIndexId = LastCreatedId;
            var manifest = JsonSerializer.Serialize(new { schema_version = 1, vault_id = VaultId, revision = 1, updated_at = "2026-01-01T00:00:00Z", files = new[] { new { file_id = RootIndexId, path = "index.md", sha256 = CanonicalHash.Text(index).ToUpperInvariant(), revision = 1, change_origin = "installation", changed_at = "2026-01-01T00:00:00Z" } } }, IndentedJson) + "\n";
            var registry = JsonSerializer.Serialize(new { schema_version = 1, default_vault_root = fixture.Vault, vaults = new[] { new { vault_id = VaultId, vault_root = fixture.Vault, applied_guide_sha256 = new string('A', 64), protected_content_manifest_sha256 = CanonicalHash.Text(manifest).ToUpperInvariant() } } }, IndentedJson) + "\n";
            await File.WriteAllTextAsync(Path.Combine(fixture.Vault, ".vault-system", "vault.json"), $"{{\"schema_version\":1,\"vault_id\":\"{VaultId}\"}}", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixture.Vault, ".vault-system", "content-integrity.json"), manifest, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixture.Vault, "index.md"), index, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixture.Config, "vault-registry.json"), registry, cancellationToken);
            return fixture;
        }

        public static string Managed(string title, string project, string kind, string body)
        {
            LastCreatedId = Guid.NewGuid().ToString("D");
            return $"---\nfile_id: \"{LastCreatedId}\"\ntitle: \"{title}\"\nproject: \"{project}\"\nkind: \"{kind}\"\nstatus: \"active\"\naliases: []\nhasInboundLinks: false\n---\n\n{body}";
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
