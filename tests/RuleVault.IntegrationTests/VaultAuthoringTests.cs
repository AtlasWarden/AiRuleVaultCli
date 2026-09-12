using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleVault.Agents;
using RuleVault.Cli;
using RuleVault.Core;
using RuleVault.Storage;
using Xunit;

namespace RuleVault.IntegrationTests;

public sealed class VaultAuthoringTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string[] AllOperations = ["read", "edit", "test", "review", "release", "maintain-vault"];
    private static readonly string[] RootIndexRequirement = ["root-index"];

    [Fact]
    public async Task HelpListsAvailableCommandsInPlainLanguage()
    {
        using var result = await Cli("help");
        var data = result.RootElement.GetProperty("data");
        Assert.Equal("rulevault", data.GetProperty("executable").GetString());
        Assert.Contains(data.GetProperty("commands").EnumerateArray(), command =>
            command.GetProperty("name").GetString() == "agent context" &&
            command.GetProperty("summary").GetString()!.Contains("rules and project information", StringComparison.Ordinal) &&
            command.GetProperty("group").GetString() == "Commands for AI agents");
        Assert.Contains(data.GetProperty("commands").EnumerateArray(), command =>
            command.GetProperty("name").GetString() == "agent status" &&
            command.GetProperty("group").GetString() == "Commands for people");
    }

    [Fact]
    public async Task CommandHelpDoesNotRunTheCommand()
    {
        using var result = await Cli("agent", "context", "--help");
        Assert.Equal("OK", result.RootElement.GetProperty("code").GetString());
        Assert.Equal("agent context", result.RootElement.GetProperty("data").GetProperty("topic").GetString());
        Assert.StartsWith("rulevault agent context", result.RootElement.GetProperty("data").GetProperty("commands")[0].GetProperty("usage").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownHelpTopicReturnsAHelpfulError()
    {
        using var result = await CliExpectExit(2, "help", "not-a-command");
        Assert.Equal("HELP_COMMAND_NOT_FOUND", result.RootElement.GetProperty("code").GetString());
        Assert.Contains("rulevault help", result.RootElement.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCommandProducesStructuredAndReadableOutput()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "rulevault-command-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyRoot);
        var missingConfig = Path.Combine(emptyRoot, "missing-config");
        var commands = new (string Name, string[] Arguments)[]
        {
            ("help", ["help"]),
            ("version", ["version"]),
            ("agent capabilities", ["agent", "capabilities"]),
            ("capabilities", ["capabilities"]),
            ("agent doctor", ["agent", "doctor", "--config-root", missingConfig]),
            ("status", ["status", "--config-root", missingConfig]),
            ("doctor", ["doctor", "--config-root", missingConfig]),
            ("agent register", ["agent", "register"]),
            ("agent context", ["agent", "context"]),
            ("agent read", ["agent", "read"]),
            ("agent write", ["agent", "write"]),
            ("agent repository initialize", ["agent", "repository", "initialize"]),
            ("agent repository read", ["agent", "repository", "read"]),
            ("agent repository write", ["agent", "repository", "write"]),
            ("agent project create", ["agent", "project", "create"]),
            ("agent project inspect", ["agent", "project", "inspect"]),
            ("agent daily inspect", ["agent", "daily", "inspect"]),
            ("agent daily rollover", ["agent", "daily", "rollover"]),
            ("agent links", ["agent", "links"]),
            ("agent delete", ["agent", "delete"]),
            ("agent status", ["agent", "status", "--config-root", missingConfig]),
            ("agent usage", ["agent", "usage", "--config-root", missingConfig]),
            ("agent list", ["agent", "list", "--config-root", missingConfig]),
            ("agent clear", ["agent", "clear"]),
            ("vault inspect", ["vault", "inspect"]),
            ("vault status", ["vault", "status"]),
            ("vault identity", ["vault", "identity"]),
            ("vault read", ["vault", "read"]),
            ("vault write", ["vault", "write"]),
            ("vault links", ["vault", "links"]),
            ("vault delete", ["vault", "delete"]),
            ("context", ["context"]),
            ("project create", ["project", "create"]),
            ("daily inspect", ["daily", "inspect"]),
            ("daily rollover", ["daily", "rollover"]),
            ("repository agents read", ["repository", "agents", "read"]),
            ("repository agents write", ["repository", "agents", "write"]),
            ("agents discover", ["agents", "discover", "--home-root", emptyRoot]),
            ("agents bootstrap discover", ["agents", "bootstrap", "discover", "--home-root", emptyRoot]),
            ("agents bootstrap apply", ["agents", "bootstrap", "apply"]),
            ("agents write", ["agents", "write"]),
            ("install plan", ["install", "plan"]),
            ("update plan", ["update", "plan"]),
            ("repair plan", ["repair", "plan"]),
            ("plan show", ["plan", "show"]),
            ("plan apply", ["plan", "apply"])
        };

        var failures = new List<string>();
        try
        {
            foreach (var command in commands)
            {
                var textResult = await RunCli(command.Arguments, forceJson: false);
                if (string.IsNullOrWhiteSpace(textResult.Output) || textResult.Output.TrimStart().StartsWith('{') ||
                    (textResult.Output + textResult.Error).Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{command.Name}: default text was empty, raw JSON, or unhandled ({textResult.ExitCode}).");
                }

                var jsonResult = await RunCli([.. command.Arguments, "--format", "json"], forceJson: false);
                try
                {
                    using var json = JsonDocument.Parse(jsonResult.Output);
                    if (string.IsNullOrWhiteSpace(json.RootElement.GetProperty("code").GetString()))
                    {
                        failures.Add($"{command.Name}: JSON had no response code.");
                    }
                }
                catch (Exception exception)
                {
                    failures.Add($"{command.Name}: invalid JSON ({exception.Message}).");
                }

                var tableResult = await RunCli([.. command.Arguments, "--format", "table"], forceJson: false);
                if (!tableResult.Output.Contains('|', StringComparison.Ordinal) || !tableResult.Output.Contains('+', StringComparison.Ordinal) ||
                    (tableResult.Output + tableResult.Error).Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{command.Name}: table output was missing or unhandled ({tableResult.ExitCode}).");
                }
            }

            var version = await RunCli(["version"], forceJson: false);
            if (!version.Output.Contains("CLI Version: 0.1.0", StringComparison.Ordinal))
            {
                failures.Add("version: default text did not contain the actual CLI version.");
            }
        }
        finally
        {
            Directory.Delete(emptyRoot);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task TwoHundredAgentsRegisterAndLoadContextWithoutAGlobalSessionLock()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        var sessionIds = Enumerable.Range(0, 200).Select(index => $"parallel-session-{index:D3}").ToArray();

        var registrations = await Task.WhenAll(sessionIds.Select(sessionId => AgentSessions.RegisterAsync(
            fixture.Config,
            sessionId,
            $"Agent {sessionId}",
            fixture.Root,
            "global",
            "synthetic",
            TestContext.Current.CancellationToken)));
        Assert.Equal(200, registrations.Length);

        var contexts = await Task.WhenAll(sessionIds.Select(sessionId => AgentSessions.BuildContextAsync(
            fixture.Config,
            sessionId,
            TaskOperation.Read,
            ["memory"],
            [],
            ContextAudience.Private,
            false,
            0,
            null,
            cancellationToken: TestContext.Current.CancellationToken)));

        Assert.All(contexts, context => Assert.True(context.StartupComplete));
        Assert.Equal(200, Directory.EnumerateFiles(Path.Combine(fixture.Config, "agent-sessions"), "*.json", SearchOption.TopDirectoryOnly).Count());
        Assert.False(File.Exists(Path.Combine(fixture.Config, "agent-sessions.json")));
    }

    [Fact]
    public async Task AgentDoctorDistinguishesCurrentStaleViewAndStableMismatchWithoutDisclosingVault()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        var current = await AgentSessions.DiagnoseViewAsync(fixture.Config, TestContext.Current.CancellationToken);
        Assert.Equal("current", current.Status);
        Assert.True(current.ContextMayProceed);
        Assert.DoesNotContain(fixture.Vault, JsonSerializer.Serialize(current), StringComparison.OrdinalIgnoreCase);
        using var currentCommand = await Cli("agent", "doctor", "--config-root", fixture.Config);
        Assert.Equal("current", currentCommand.RootElement.GetProperty("data").GetProperty("status").GetString());
        using var registration = await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "doctor-session", "--name", "Doctor Agent", "--folder", fixture.Root, "--project", "global");

        var registryPath = Path.Combine(fixture.Config, "vault-registry.json");
        var registry = JsonNode.Parse(await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken))!.AsObject();
        registry["vaults"]![0]!["protected_content_manifest_sha256"] = new string('0', 64);
        await File.WriteAllTextAsync(registryPath, registry.ToJsonString(IndentedJson) + "\n", TestContext.Current.CancellationToken);
        var stale = await AgentSessions.DiagnoseViewAsync(fixture.Config, TestContext.Current.CancellationToken);
        Assert.Equal("likely-stale-registry-view", stale.Status);
        Assert.False(stale.ContextMayProceed);
        using var staleCommand = await CliExpectExit(5, "agent", "doctor", "--config-root", fixture.Config);
        Assert.Equal("VAULT_REGISTRY_VIEW_STALE", staleCommand.RootElement.GetProperty("code").GetString());
        using var blockedContext = await CliExpectExit(5, "agent", "context", "--config-root", fixture.Config, "--session-id", "doctor-session", "--operation", "read", "--subjects", "memory");
        Assert.Equal("VAULT_REGISTRY_VIEW_STALE", blockedContext.RootElement.GetProperty("code").GetString());
        Assert.Equal("likely-stale-registry-view", blockedContext.RootElement.GetProperty("data").GetProperty("status").GetString());

        registry["vaults"]![0]!["last_verified_at"] = "2030-01-01T00:00:00Z";
        await File.WriteAllTextAsync(registryPath, registry.ToJsonString(IndentedJson) + "\n", TestContext.Current.CancellationToken);
        var mismatch = await AgentSessions.DiagnoseViewAsync(fixture.Config, TestContext.Current.CancellationToken);
        Assert.Equal("integrity-mismatch", mismatch.Status);
        Assert.False(mismatch.ContextMayProceed);
    }

    [Fact]
    public async Task ConcurrentProtectedWritersCannotLeaveManifestAndRegistryOutOfSync()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        var original = await VaultAuthoring.ReadAsync(fixture.Vault, "index.md", TestContext.Current.CancellationToken);
        var firstContent = original.Content.Replace("## Projects", "First writer\n\n## Projects", StringComparison.Ordinal);
        var secondContent = original.Content.Replace("## Projects", "Second writer\n\n## Projects", StringComparison.Ordinal);

        var attempts = await Task.WhenAll(
            CaptureWriteAsync(new VaultWriteRequest(fixture.Vault, fixture.Config, "index.md", firstContent, original.RawSha256)),
            CaptureWriteAsync(new VaultWriteRequest(fixture.Vault, fixture.Config, "index.md", secondContent, original.RawSha256)));

        Assert.Single(attempts, result => result is not null);
        var finalRead = await VaultAuthoring.ReadAsync(fixture.Vault, "index.md", TestContext.Current.CancellationToken);
        Assert.True(finalRead.Content == firstContent || finalRead.Content == secondContent);
        var manifest = await File.ReadAllTextAsync(Path.Combine(fixture.Vault, ".vault-system", "content-integrity.json"), TestContext.Current.CancellationToken);
        var registry = await File.ReadAllTextAsync(Path.Combine(fixture.Config, "vault-registry.json"), TestContext.Current.CancellationToken);
        Assert.Contains(CanonicalHash.Text(finalRead.Content).ToUpperInvariant(), manifest, StringComparison.Ordinal);
        Assert.Contains(CanonicalHash.Text(manifest).ToUpperInvariant(), registry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneHundredContextReadersWaitForAProtectedWriterAndThenComplete()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        var sessionIds = Enumerable.Range(0, 100).Select(index => $"waiting-session-{index:D3}").ToArray();
        await Task.WhenAll(sessionIds.Select(sessionId => AgentSessions.RegisterAsync(
            fixture.Config,
            sessionId,
            $"Waiting agent {sessionId}",
            fixture.Root,
            "global",
            "synthetic",
            TestContext.Current.CancellationToken)));

        var writer = await VaultIntegrityTransaction.AcquireWriterAsync(fixture.Vault, TestContext.Current.CancellationToken);
        Task<AgentContextResult>[] readers = [];
        try
        {
            readers = sessionIds.Select(sessionId => AgentSessions.BuildContextAsync(
                fixture.Config,
                sessionId,
                TaskOperation.Read,
                ["memory"],
                [],
                ContextAudience.Private,
                false,
                0,
                null,
                cancellationToken: TestContext.Current.CancellationToken)).ToArray();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.All(readers, reader => Assert.False(reader.IsCompleted));
        }
        finally
        {
            await writer.DisposeAsync();
        }

        var contexts = await Task.WhenAll(readers);
        Assert.All(contexts, context => Assert.True(context.StartupComplete));
    }

    [Fact]
    public async Task OneHundredAgentsCanImportTheSameNewGitBackedProject()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        await Git(fixture.Root, "init");
        await Git(fixture.Root, "config user.email fixture@example.invalid");
        await Git(fixture.Root, "config user.name Fixture");
        var projectId = Guid.NewGuid().ToString("D");
        await RepositoryAgentsAuthoring.InitializeAsync(
            fixture.Root,
            projectId,
            "shared-clone",
            "Shared Clone",
            TestContext.Current.CancellationToken);
        await Git(fixture.Root, "add -- .agents");
        await Git(fixture.Root, "commit -m initial-agents");

        var sessionIds = Enumerable.Range(0, 100).Select(index => $"clone-session-{index:D3}").ToArray();
        await Task.WhenAll(sessionIds.Select(sessionId => AgentSessions.RegisterAsync(
            fixture.Config,
            sessionId,
            $"Clone agent {sessionId}",
            fixture.Root,
            "shared-clone",
            "synthetic",
            TestContext.Current.CancellationToken)));

        var contexts = await Task.WhenAll(sessionIds.Select(sessionId => AgentSessions.BuildContextAsync(
            fixture.Config,
            sessionId,
            TaskOperation.Read,
            ["project"],
            [".agents/project.json"],
            ContextAudience.Private,
            false,
            0,
            null,
            cancellationToken: TestContext.Current.CancellationToken)));

        Assert.All(contexts, context => Assert.True(context.StartupComplete));
        var imported = await VaultAuthoring.InspectProjectAsync(fixture.Vault, "shared-clone", TestContext.Current.CancellationToken);
        Assert.Equal(projectId, imported.ProjectId, ignoreCase: true);
    }

    private static async Task<VaultWriteResult?> CaptureWriteAsync(VaultWriteRequest request)
    {
        try
        {
            return await VaultAuthoring.WriteAsync(request, TestContext.Current.CancellationToken);
        }
        catch (Exception exception) when (exception is WriteConflictException or VaultAuthoringException { Code: "WRITE_PRECONDITION_FAILED" })
        {
            return null;
        }
    }
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
            var indexEntry = manifestJson.RootElement.GetProperty("files").EnumerateArray().Single(entry => entry.GetProperty("path").GetString() == "index.md");
            Assert.Equal(CanonicalHash.Text(replacement).ToUpperInvariant(), indexEntry.GetProperty("sha256").GetString());
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
            var first = await VaultAuthoring.InspectDailyAsync(fixture.Vault, "sample-project", DateOnly.FromDateTime(DateTime.Now).AddDays(1), fixture.Config, TestContext.Current.CancellationToken);
            Assert.True(first.RolloverRequired);
            var blocked = await Assert.ThrowsAsync<VaultAuthoringException>(() => VaultAuthoring.RolloverDailyAsync(fixture.Vault, fixture.Config, "sample-project", first.ActiveDate.AddDays(1), first.ActiveRawSha256, false, TestContext.Current.CancellationToken));
            Assert.Equal("PROMOTIONS_CONFIRMATION_REQUIRED", blocked.Code);
            var changed = await VaultAuthoring.RolloverDailyAsync(fixture.Vault, fixture.Config, "sample-project", first.ActiveDate.AddDays(1), first.ActiveRawSha256, true, TestContext.Current.CancellationToken);
            Assert.Contains(changed, path => path.EndsWith("daily/index.md", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(fixture.Vault, "projects", "sample-project", "daily", "archive", $"{first.ActiveDate:yyyy}", $"{first.ActiveDate:MMM}", $"{first.ActiveDate:dd}.md")));
            using var manifest = StrictJson.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture.Vault, ".vault-system", "content-integrity.json"), TestContext.Current.CancellationToken));
            var protectedPaths = manifest.RootElement.GetProperty("files").EnumerateArray().Select(entry => entry.GetProperty("path").GetString()).ToArray();
            Assert.DoesNotContain(first.ActiveRelativePath, protectedPaths);
            Assert.DoesNotContain($"projects/sample-project/daily/active/{first.ActiveDate.AddDays(1):yyyy-MM-dd}.md", protectedPaths);
            Assert.Contains($"projects/sample-project/daily/archive/{first.ActiveDate:yyyy}/{first.ActiveDate:MMM}/index.md", protectedPaths);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task AgentStartupRequiresDeterministicContextAndRegistrationAlwaysResetsIt()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        using var registration = await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "startup-session", "--name", "Startup Agent", "--folder", fixture.Root, "--project", "global", "--agent-kind", "codex");
        Assert.True(registration.RootElement.GetProperty("data").GetProperty("startup_required").GetBoolean());
        using var blocked = await CliExpectExit(4, "agent", "read", "--config-root", fixture.Config, "--session-id", "startup-session", "--path", "index.md");
        Assert.Equal("AGENT_STARTUP_REQUIRED", blocked.RootElement.GetProperty("code").GetString());

        using var context = await Cli("agent", "context", "--config-root", fixture.Config, "--session-id", "startup-session", "--operation", "read");
        var packet = context.RootElement.GetProperty("data").GetProperty("packet");
        Assert.True(context.RootElement.GetProperty("data").GetProperty("startup_complete").GetBoolean());
        Assert.Contains(packet.GetProperty("segments").EnumerateArray(), segment => segment.GetProperty("route_id").GetString() == "root-index");
        Assert.Contains(packet.GetProperty("segments").EnumerateArray(), segment => segment.GetProperty("route_id").GetString() == "vault-discovery-and-integrity");
        Assert.Contains("# Rule Vault Index", packet.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.All(packet.GetProperty("segments").EnumerateArray(), segment => Assert.False(segment.TryGetProperty("body", out _)));
        Assert.True(context.RootElement.GetProperty("token_usage").GetProperty("serialized_tokens_excluding_usage").GetInt32() > 0);
        var receipt = packet.GetProperty("body_sha256").GetString()!;
        using var reused = await Cli("agent", "context", "--config-root", fixture.Config, "--session-id", "startup-session", "--operation", "read", "--known-context-sha256", receipt);
        Assert.Equal("unchanged", reused.RootElement.GetProperty("data").GetProperty("delivery").GetString());
        Assert.Equal("", reused.RootElement.GetProperty("data").GetProperty("packet").GetProperty("body").GetString());
        Assert.Equal(0, reused.RootElement.GetProperty("data").GetProperty("read_tokens").GetInt32());
        using var noReceipt = await Cli("agent", "context", "--config-root", fixture.Config, "--session-id", "startup-session", "--operation", "read");
        Assert.Equal("full", noReceipt.RootElement.GetProperty("data").GetProperty("delivery").GetString());
        var writeContent = Fixture.Managed("Blocked Write", "global", "context", "# Blocked Write\n");
        using var writeBlocked = await CliExpectExit(4, "agent", "write", "--config-root", fixture.Config, "--session-id", "startup-session", "--path", "global/blocked.md", "--content", writeContent);
        Assert.Equal("AGENT_CONTEXT_OPERATION_REQUIRED", writeBlocked.RootElement.GetProperty("code").GetString());
        using var editContext = await Cli("agent", "context", "--config-root", fixture.Config, "--session-id", "startup-session", "--operation", "edit");
        using var memoryScopeBlocked = await CliExpectExit(4, "agent", "write", "--config-root", fixture.Config, "--session-id", "startup-session", "--path", "global/blocked.md", "--content", writeContent);
        Assert.Equal("AGENT_CONTEXT_SCOPE_REQUIRED", memoryScopeBlocked.RootElement.GetProperty("code").GetString());
        using var scopeBlocked = await CliExpectExit(4, "agent", "project", "create", "--config-root", fixture.Config, "--session-id", "startup-session", "--slug", "scope-project", "--title", "Scope Project");
        Assert.Equal("AGENT_CONTEXT_SCOPE_REQUIRED", scopeBlocked.RootElement.GetProperty("code").GetString());

        using var repeatedRegistration = await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "startup-session", "--name", "Startup Agent", "--folder", fixture.Root, "--project", "global", "--agent-kind", "codex");
        Assert.True(repeatedRegistration.RootElement.GetProperty("data").GetProperty("startup_required").GetBoolean());
        using var blockedAgain = await CliExpectExit(4, "agent", "read", "--config-root", fixture.Config, "--session-id", "startup-session", "--path", "index.md");
        Assert.Equal("AGENT_STARTUP_REQUIRED", blockedAgain.RootElement.GetProperty("code").GetString());
        using var restart = await Cli("agent", "context", "--config-root", fixture.Config, "--session-id", "startup-session", "--operation", "read", "--known-context-sha256", receipt);
        Assert.Equal("full", restart.RootElement.GetProperty("data").GetProperty("delivery").GetString());
        using var changedTask = await Cli("agent", "context", "--config-root", fixture.Config, "--session-id", "startup-session", "--operation", "edit", "--known-context-sha256", receipt);
        Assert.Equal("full", changedTask.RootElement.GetProperty("data").GetProperty("delivery").GetString());
        await File.AppendAllTextAsync(Path.Combine(fixture.Vault, "index.md"), "\nUnverified alteration.\n", TestContext.Current.CancellationToken);
        using var tampered = await CliExpectExit(4, "agent", "context", "--config-root", fixture.Config, "--session-id", "startup-session", "--operation", "edit", "--known-context-sha256", changedTask.RootElement.GetProperty("data").GetProperty("packet").GetProperty("body_sha256").GetString()!);
        Assert.Equal("blocked", tampered.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task NewMandatoryPrivateDocumentIsEnrolledInIntegrityManifest()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        await VaultAuthoring.CreateProjectAsync(new ProjectCreateRequest(fixture.Vault, fixture.Config, "p", "Project P", null, null), TestContext.Current.CancellationToken);
        var content = Fixture.Managed("Mandatory Rules", "p", "rule", "# Mandatory Rules\n")
            .Replace("hasInboundLinks: false\n", "hasInboundLinks: false\nload_policy: \"always\"\n", StringComparison.Ordinal);
        var result = await VaultAuthoring.WriteAsync(new VaultWriteRequest(fixture.Vault, fixture.Config, "projects/p/rules/mandatory.md", content, null), TestContext.Current.CancellationToken);
        Assert.True(result.IntegrityUpdated);
        using var manifest = StrictJson.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture.Vault, ".vault-system", "content-integrity.json"), TestContext.Current.CancellationToken));
        var entry = manifest.RootElement.GetProperty("files").EnumerateArray().Single(item => item.GetProperty("path").GetString() == "projects/p/rules/mandatory.md");
        Assert.Equal(CanonicalHash.Text(content).ToUpperInvariant(), entry.GetProperty("sha256").GetString());
        using var registration = await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "mandatory-session", "--name", "Mandatory Reader", "--folder", fixture.Root, "--project", "p");
        using var context = await Cli("agent", "context", "--config-root", fixture.Config, "--session-id", "mandatory-session", "--operation", "read");
        Assert.Contains("# Mandatory Rules", context.RootElement.GetProperty("data").GetProperty("packet").GetProperty("body").GetString(), StringComparison.Ordinal);
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
            var daily = await VaultAuthoring.InspectDailyAsync(fixture.Vault, "global", new DateOnly(2026, 9, 10), fixture.Config, TestContext.Current.CancellationToken);
            Assert.Equal("global/daily/active/2026-09-10.md", daily.ActiveRelativePath);
            Assert.True(File.Exists(Path.Combine(fixture.Vault, "global", "daily", "index.md")));
            Assert.Contains("# Global Daily Notes", await File.ReadAllTextAsync(Path.Combine(fixture.Vault, "global", "daily", "index.md"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task UsageMarksArchivedDailyBasisStaleInsteadOfCrashing()
    {
        var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            await VaultAuthoring.CreateProjectAsync(new ProjectCreateRequest(fixture.Vault, fixture.Config, "daily-project", "Daily Project", null, null), TestContext.Current.CancellationToken);
            await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "daily-session", "--name", "Daily Agent", "--folder", fixture.Root, "--project", "daily-project", "--agent-kind", "codex");
            await AgentContext(fixture.Config, "daily-session", "maintain-vault", "daily");
            var tomorrow = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
            var tomorrowText = tomorrow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            using var inspection = await Cli("agent", "daily", "inspect", "--config-root", fixture.Config, "--session-id", "daily-session", "--project", "daily-project", "--date", tomorrowText);
            var expected = inspection.RootElement.GetProperty("data").GetProperty("daily").GetProperty("active_raw_sha256").GetString()!;

            await Cli("agent", "daily", "rollover", "--config-root", fixture.Config, "--session-id", "daily-session", "--project", "daily-project", "--date", tomorrowText, "--expected-raw-sha256", expected, "--promotions-complete", "true");
            using var usage = await Cli("agent", "usage", "--config-root", fixture.Config, "--session-id", "daily-session");

            var sessions = usage.RootElement.GetProperty("data").GetProperty("sessions");
            Assert.Equal(1, sessions.GetArrayLength());
            Assert.Equal("stale", sessions[0].GetProperty("freshness").GetString());
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
            await AgentContext(fixture.Config, "thread-a", "edit");
            await AgentContext(fixture.Config, "thread-b", "review");
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
            var file = detail.RootElement.GetProperty("data").GetProperty("files").EnumerateArray().Single(item => item.GetProperty("relative_path").GetString() == "index.md");
            Assert.Equal("index.md", file.GetProperty("relative_path").GetString());
            Assert.True(file.GetProperty("read_tokens").GetInt64() > 0);
            Assert.True(file.GetProperty("write_tokens").GetInt64() > 0);
            Assert.True(file.GetProperty("in_current_context").GetBoolean());
            Assert.DoesNotContain(fixture.Vault, detail.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

            using var byName = await Cli("agent", "status", "Reviewer", "--config-root", fixture.Config);
            Assert.Single(byName.RootElement.GetProperty("data").GetProperty("sessions").EnumerateArray());
            Assert.Equal("Reviewer", byName.RootElement.GetProperty("data").GetProperty("sessions")[0].GetProperty("friendly_name").GetString());
            Assert.Contains(byName.RootElement.GetProperty("data").GetProperty("files").EnumerateArray(), item => item.GetProperty("relative_path").GetString() == "index.md");

            var plainList = await RunCli(["agent", "status", "--config-root", fixture.Config], forceJson: false);
            Assert.Equal(0, plainList.ExitCode);
            Assert.Contains("Registered agents", plainList.Output, StringComparison.Ordinal);
            Assert.Contains("Author", plainList.Output, StringComparison.Ordinal);
            Assert.Contains("rulevault agent status <agent-id-or-name>", plainList.Output, StringComparison.Ordinal);

            var plainAgent = await RunCli(["agent", "status", "Reviewer", "--config-root", fixture.Config], forceJson: false);
            Assert.Equal(0, plainAgent.ExitCode);
            Assert.Contains("Agent details", plainAgent.Output, StringComparison.Ordinal);
            Assert.Contains("Files used", plainAgent.Output, StringComparison.Ordinal);
            Assert.Contains("index.md", plainAgent.Output, StringComparison.Ordinal);
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
    public async Task OpaqueAgentSessionCanReadAndWriteRepositoryAgentsWithoutVaultDisclosure()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        await Git(fixture.Root, "init");
        using var registration = await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "repo-session", "--name", "Repository agent", "--folder", fixture.Root, "--project", "fixture", "--agent-kind", "codex");
        Assert.Equal("OK", registration.RootElement.GetProperty("code").GetString());
        await AgentContext(fixture.Config, "repo-session", "edit", "repository");

        const string content = "---\nfile_id: \"00000000-0000-0000-0000-000000000099\"\ntitle: \"Repository Rules\"\nproject: \"fixture\"\nkind: \"rule\"\nstatus: \"active\"\naliases: []\nhasInboundLinks: false\ncreated: \"2026-01-01 00:00:00 UTC\"\n---\n\n# Repository Rules\n";
        using var write = await Cli("agent", "repository", "write", "--config-root", fixture.Config, "--session-id", "repo-session", "--path", ".agents/rules.md", "--content", content);
        Assert.False(write.RootElement.GetProperty("data").GetProperty("branch_manifest_updated").GetBoolean());
        await Git(fixture.Root, "add -- .agents/rules.md");

        using var read = await Cli("agent", "repository", "read", "--config-root", fixture.Config, "--session-id", "repo-session", "--path", ".agents/rules.md");
        Assert.Equal(content, read.RootElement.GetProperty("data").GetProperty("content").GetString());
        Assert.Equal("schema-valid", read.RootElement.GetProperty("data").GetProperty("metadata_status").GetString());
        Assert.DoesNotContain(fixture.Vault, read.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpaqueRepositoryReadReportsSecurityControlForSymbolicLink()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        await Git(fixture.Root, "init");
        await Git(fixture.Root, "config core.symlinks true");
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".agents"));
        var outside = Path.Combine(fixture.Root, "outside.md");
        var link = Path.Combine(fixture.Root, ".agents", "link.md");
        await File.WriteAllTextAsync(outside, "outside fixture\n", TestContext.Current.CancellationToken);
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Skip($"Symbolic-link creation is unavailable in this test environment: {exception.Message}");
        }

        await Git(fixture.Root, "add -- .agents/link.md");
        using var registration = await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "link-session", "--name", "Link reader", "--folder", fixture.Root, "--project", "fixture");
        await AgentContext(fixture.Config, "link-session", "read", "repository");
        using var blocked = await CliExpectExit(4, "agent", "repository", "read", "--config-root", fixture.Config, "--session-id", "link-session", "--path", ".agents/link.md");
        Assert.Equal("RV-SEC-001", blocked.RootElement.GetProperty("code").GetString());
        Assert.Equal("outside fixture\n", await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpaqueAgentCanCreateGitBackedProjectScaffoldWithSharedIdentity()
    {
        using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        await Git(fixture.Root, "init");
        using var registration = await Cli("agent", "register", "--config-root", fixture.Config, "--session-id", "init-session", "--name", "Initializer", "--folder", fixture.Root, "--project", "shared-project");
        await AgentContext(fixture.Config, "init-session", "edit", "project");
        using var privateProject = await Cli("agent", "project", "create", "--config-root", fixture.Config, "--session-id", "init-session", "--slug", "shared-project", "--title", "Shared Project");
        var projectId = privateProject.RootElement.GetProperty("data").GetProperty("project_id").GetString();

        await AgentContext(fixture.Config, "init-session", "edit", "repository");
        using var initialized = await Cli("agent", "repository", "initialize", "--config-root", fixture.Config, "--session-id", "init-session", "--project", "shared-project", "--title", "Shared Project");
        Assert.Equal(projectId, initialized.RootElement.GetProperty("data").GetProperty("repository").GetProperty("project_id").GetString());
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".agents", "content-integrity.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Vault, "projects", "shared-project", "project.json")));

        await Git(fixture.Root, "add -- .agents");
        using var read = await Cli("agent", "repository", "read", "--config-root", fixture.Config, "--session-id", "init-session", "--path", ".agents/index.md");
        Assert.Equal("candidate-working-tree-data-only", read.RootElement.GetProperty("data").GetProperty("content_handling").GetString());
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
            var before = RepositoryManaged("00000000-0000-0000-0000-000000000101", "Roles", "rule", "# Before\n");
            var after = before.Replace("# Before", "# After", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, before, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, ".agents", "content-integrity.json"), RepositoryManifest("00000000-0000-0000-0000-000000000102", []), TestContext.Current.CancellationToken);
            await Git(root, "add -- .agents/roles.md .agents/content-integrity.json");
            var read = await RepositoryAgentsAuthoring.ReadAsync(root, ".agents/roles.md", TestContext.Current.CancellationToken);
            var write = await RepositoryAgentsAuthoring.WriteAsync(new RepositoryAgentsWriteRequest(root, ".agents/roles.md", after, read.RawSha256), TestContext.Current.CancellationToken);
            Assert.False(write.Created);
            Assert.Equal(after, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(Path.Combine(root, ".agents", "content-integrity.json"), "{\"schema_version\":1,\"files\":[]}", TestContext.Current.CancellationToken);
            var dataOnly = await RepositoryAgentsAuthoring.ReadAsync(root, ".agents/roles.md", TestContext.Current.CancellationToken);
            Assert.Equal("schema-valid", dataOnly.MetadataStatus);
            Assert.Equal("invalid-manifest-data-only", dataOnly.ContentHandling);
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
            var before = RepositoryManaged("00000000-0000-0000-0000-000000000201", "Rules", "rule", "# Before\n", "integrity: \"protected\"\n");
            var after = before.Replace("# Before", "# After", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(root, ".agents", "rules.md"), before, TestContext.Current.CancellationToken);
            var manifest = RepositoryManifest("00000000-0000-0000-0000-000000000202", [("00000000-0000-0000-0000-000000000201", "rules.md", CanonicalHash.Text(before).ToUpperInvariant())]);
            await File.WriteAllTextAsync(Path.Combine(root, ".agents", "content-integrity.json"), manifest, TestContext.Current.CancellationToken);
            await Git(root, "add -- .agents/rules.md .agents/content-integrity.json");
            var read = await RepositoryAgentsAuthoring.ReadAsync(root, ".agents/rules.md", TestContext.Current.CancellationToken);
            var result = await RepositoryAgentsAuthoring.WriteAsync(new RepositoryAgentsWriteRequest(root, ".agents/rules.md", after, read.RawSha256), TestContext.Current.CancellationToken);
            Assert.True(result.BranchManifestUpdated);
            var updated = await File.ReadAllTextAsync(Path.Combine(root, ".agents", "content-integrity.json"), TestContext.Current.CancellationToken);
            Assert.Contains(CanonicalHash.Text(after).ToUpperInvariant(), updated, StringComparison.Ordinal);
        }
        finally { DeleteFixture(root); }
    }

    [Fact]
    public async Task NewMandatoryRepositoryDocumentIsEnrolledAndMalformedMetadataNeverBecomesAuthoritative()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init"); await Git(root, "config user.email fixture@example.invalid"); await Git(root, "config user.name Fixture");
            await RepositoryAgentsAuthoring.InitializeAsync(root, "00000000-0000-0000-0000-000000000301", "fixture", "Fixture", TestContext.Current.CancellationToken);
            await Git(root, "add -- .agents"); await Git(root, "commit -m initialize");

            var mandatory = RepositoryManaged("00000000-0000-0000-0000-000000000302", "Mandatory", "rule", "# Mandatory\n", "load_policy: \"always\"\n");
            var created = await RepositoryAgentsAuthoring.WriteAsync(new RepositoryAgentsWriteRequest(root, ".agents/rules/mandatory.md", mandatory, null), TestContext.Current.CancellationToken);
            Assert.True(created.BranchManifestUpdated);
            var updatedManifest = await File.ReadAllTextAsync(Path.Combine(root, ".agents", "content-integrity.json"), TestContext.Current.CancellationToken);
            Assert.Contains("rules/mandatory.md", updatedManifest, StringComparison.Ordinal);

            var malformed = "---\nfile_id: \"00000000-0000-0000-0000-000000000303\"\nproject: \"fixture\"\nkind: \"rule\"\nstatus: \"active\"\naliases: []\nhasInboundLinks: false\ncreated: \"2026-01-01 00:00:00 UTC\"\n---\n\n# Missing title\n";
            var malformedPath = Path.Combine(root, ".agents", "malformed.md");
            await File.WriteAllTextAsync(malformedPath, malformed, TestContext.Current.CancellationToken);
            var manifestNode = JsonNode.Parse(updatedManifest)!.AsObject();
            manifestNode["files"]!.AsArray().Add(new JsonObject
            {
                ["file_id"] = "00000000-0000-0000-0000-000000000303",
                ["path"] = "malformed.md",
                ["sha256"] = CanonicalHash.Text(malformed).ToUpperInvariant(),
                ["revision"] = 1,
                ["change_origin"] = "agent-authorized",
                ["changed_at"] = "2026-01-01T00:00:00Z"
            });
            await File.WriteAllTextAsync(Path.Combine(root, ".agents", "content-integrity.json"), manifestNode.ToJsonString(IndentedJson) + "\n", TestContext.Current.CancellationToken);
            await Git(root, "add -- .agents"); await Git(root, "commit -m malformed-fixture");

            var read = await RepositoryAgentsAuthoring.ReadAsync(root, ".agents/malformed.md", TestContext.Current.CancellationToken);
            Assert.Equal("invalid-data-only", read.MetadataStatus);
            Assert.Equal("invalid-metadata-data-only", read.ContentHandling);
        }
        finally { DeleteFixture(root); }
    }

    private static string RegexId(string content) => System.Text.RegularExpressions.Regex.Match(content, "file_id: \\\"(?<id>[0-9a-f-]{36})").Groups["id"].Value;

    private static Task<JsonDocument> AgentContext(string configRoot, string sessionId, string operation, string? subjects = null) =>
        subjects is null
            ? Cli("agent", "context", "--config-root", configRoot, "--session-id", sessionId, "--operation", operation, "--subjects", "memory")
            : Cli("agent", "context", "--config-root", configRoot, "--session-id", sessionId, "--operation", operation, "--subjects", subjects);

    private static string RepositoryManaged(string fileId, string title, string kind, string body, string extra = "") =>
        $"---\nfile_id: \"{fileId}\"\ntitle: \"{title}\"\nproject: \"fixture\"\nkind: \"{kind}\"\nstatus: \"active\"\naliases: []\nhasInboundLinks: false\ncreated: \"2026-01-01 00:00:00 UTC\"\n{extra}---\n\n{body}";

    private static string RepositoryManifest(string projectId, IReadOnlyList<(string FileId, string Path, string Sha256)> entries) =>
        JsonSerializer.Serialize(new
        {
            schema_version = 1,
            project_id = projectId,
            revision = 1,
            updated_at = "2026-01-01T00:00:00Z",
            files = entries.Select(entry => new { file_id = entry.FileId, path = entry.Path, sha256 = entry.Sha256, revision = 1, change_origin = "agent-authorized", changed_at = "2026-01-01T00:00:00Z" })
        }, IndentedJson) + "\n";

    private static async Task Git(string root, string arguments)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("git", $"-C \"{root}\" {arguments}") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.Start(); var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, error);
    }

    private static async Task<JsonDocument> Cli(params string[] arguments)
    {
        var execution = await RunCli(arguments);
        Assert.True(execution.ExitCode == 0, execution.Error + execution.Output);
        return JsonDocument.Parse(execution.Output);
    }

    private static async Task<JsonDocument> CliExpectExit(int expectedExitCode, params string[] arguments)
    {
        var execution = await RunCli(arguments);
        Assert.Equal(expectedExitCode, execution.ExitCode);
        return JsonDocument.Parse(execution.Output);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCli(string[] arguments, bool forceJson = true)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RuleVault.Cli", "bin", configuration, "net10.0", "rulevault.dll"));
        Assert.True(File.Exists(cli), $"Built CLI was not found: {cli}");
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add(cli);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (forceJson)
        {
            process.StartInfo.ArgumentList.Add("--format"); process.StartInfo.ArgumentList.Add("json");
        }
        process.Start(); var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, output, error);
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
            var discovery = Managed("Vault Discovery", "global", "rule", "# Vault Discovery\n");
            var discoveryId = LastCreatedId;
            var routes = JsonSerializer.Serialize(new { schema_version = 1, routes = new object[] { new { route_id = "root-index", path = "index.md", kind = "index", scope = "global", load_policy = "always", subjects = Array.Empty<string>(), operations = AllOperations, path_patterns = Array.Empty<string>(), optional_priority = 100, requires = Array.Empty<string>(), audience = "private" }, new { route_id = "vault-discovery-and-integrity", path = "global/vault-discovery-and-integrity.md", kind = "rule", scope = "global", load_policy = "always", subjects = Array.Empty<string>(), operations = AllOperations, path_patterns = Array.Empty<string>(), optional_priority = 100, requires = RootIndexRequirement, audience = "private" } } }, IndentedJson) + "\n";
            var manifest = JsonSerializer.Serialize(new { schema_version = 1, vault_id = VaultId, revision = 1, updated_at = "2026-01-01T00:00:00Z", files = new[] { new { file_id = RootIndexId, path = "index.md", sha256 = CanonicalHash.Text(index).ToUpperInvariant(), revision = 1, change_origin = "installation", changed_at = "2026-01-01T00:00:00Z" }, new { file_id = discoveryId, path = "global/vault-discovery-and-integrity.md", sha256 = CanonicalHash.Text(discovery).ToUpperInvariant(), revision = 1, change_origin = "installation", changed_at = "2026-01-01T00:00:00Z" }, new { file_id = Guid.NewGuid().ToString("D"), path = ".vault-system/routes.json", sha256 = CanonicalHash.Text(routes).ToUpperInvariant(), revision = 1, change_origin = "installation", changed_at = "2026-01-01T00:00:00Z" } } }, IndentedJson) + "\n";
            var registry = JsonSerializer.Serialize(new { schema_version = 1, default_vault_root = fixture.Vault, vaults = new[] { new { vault_id = VaultId, vault_root = fixture.Vault, applied_guide_sha256 = new string('A', 64), protected_content_manifest_sha256 = CanonicalHash.Text(manifest).ToUpperInvariant() } } }, IndentedJson) + "\n";
            await File.WriteAllTextAsync(Path.Combine(fixture.Vault, ".vault-system", "vault.json"), $"{{\"schema_version\":1,\"vault_id\":\"{VaultId}\"}}", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixture.Vault, ".vault-system", "content-integrity.json"), manifest, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixture.Vault, ".vault-system", "routes.json"), routes, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixture.Vault, "index.md"), index, cancellationToken);
            Directory.CreateDirectory(Path.Combine(fixture.Vault, "global"));
            await File.WriteAllTextAsync(Path.Combine(fixture.Vault, "global", "vault-discovery-and-integrity.md"), discovery, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixture.Config, "vault-registry.json"), registry, cancellationToken);
            return fixture;
        }

        public static string Managed(string title, string project, string kind, string body)
        {
            LastCreatedId = Guid.NewGuid().ToString("D");
            return $"---\nfile_id: \"{LastCreatedId}\"\ntitle: \"{title}\"\nproject: \"{project}\"\nkind: \"{kind}\"\nstatus: \"active\"\naliases: []\nhasInboundLinks: false\ncreated: \"2026-01-01 00:00:00 UTC\"\n---\n\n{body}";
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                DeleteFixture(Root);
            }
        }
    }
}
