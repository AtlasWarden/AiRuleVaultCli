using System.Text.Json;
using RuleVault.Agents;
using RuleVault.Cli;
using RuleVault.Core;
using RuleVault.Installation;
using RuleVault.Storage;

var invocation = CliInvocation.Parse(args);
return await invocation.ExecuteAsync();

internal sealed record CliEnvelope(
    int SchemaVersion,
    string Command,
    string Status,
    string Code,
    string Summary,
    object? Data,
    IReadOnlyList<CliDiagnostic> Diagnostics,
    IReadOnlyList<string> NextActions);

internal sealed record CliDiagnostic(string Code, string Severity, string Message);
internal sealed record UnavailableOperation(string Command, string Reason, string Alternative);

internal sealed class CliInvocation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    private static readonly UnavailableOperation[] UnavailableOperations =
    [
        new("setup", "interactive setup is owned by the signed installer", "run install.ps1 from a verified package"),
        new("vault list", "agent-facing vault enumeration would disclose private locations", "use agent status or the installer for administrative selection"),
        new("vault select", "agent-facing vault selection would expose private configuration", "use the installer or edit a reviewed administrative plan"),
        new("migrate plan", "general source-layout migration is not implemented as a standalone command", "run update plan; it performs bounded additive conversion and archives ambiguous legacy data"),
        new("recover list", "journal recovery inspection has no stable public response contract", "inspect retained journals and archives administratively"),
        new("recover resume", "automatic journal replay is unsafe when a partial write is ambiguous", "review retained evidence and create a new exact-digest repair or update plan"),
        new("rollback plan", "general rollback planning is not implemented", "use restore-package repair or restore reviewed archived content through mediated writes"),
        new("check", "there is no separate check command", "use agent context for task-scoped validation or vault inspect for explicit administrative inspection"),
        new("memory plan", "standalone memory planning is not implemented", "use agent write after loading task context and applying the vault classification rules"),
        new("changes plan", "standalone change planning is not implemented", "use agent links/read and an exact-hash mediated write"),
        new("agents list", "legacy adapter listing was replaced by opaque session status", "use agent status and agents bootstrap discover"),
        new("agents plan", "legacy adapter planning was replaced by explicit bootstrap selection", "use agents bootstrap discover, then agents bootstrap apply"),
        new("agents probe", "executing or probing agent software is outside the safe adapter boundary", "use agents bootstrap discover, which performs bounded non-executing discovery"),
        new("run", "Rule Vault is a short-lived CLI and does not host a long-running agent process", "use agent context at task start, then opaque agent commands"),
        new("package verify", "package verification is performed by the installer before CLI execution", "run the verified installer package workflow"),
        new("self-update plan", "self-update is owned by the installer update workflow", "run install.ps1 and approve its reviewed update plan"),
        new("uninstall plan", "an uninstall planner is not implemented", "preserve the vault and remove only reviewed installer-owned files manually"),
        new("cleanup plan", "general cleanup could remove user-owned data and is not implemented", "review archive/session retention and clear only explicit agent session records"),
        new("context explain", "a separate explanation command is not implemented", "use agent context; its segments identify every included route")
    ];

    private readonly string[] _positionals;
    private readonly IReadOnlyDictionary<string, string?> _options;

    private CliInvocation(string[] positionals, IReadOnlyDictionary<string, string?> options)
    {
        _positionals = positionals;
        _options = options;
    }

    public static CliInvocation Parse(string[] args)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (!value.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(value);
                continue;
            }

            var equals = value.IndexOf('=');
            if (equals > 2)
            {
                options[value[2..equals]] = value[(equals + 1)..];
                continue;
            }

            var key = value[2..];
            if (index + 1 < args.Length && (!args[index + 1].StartsWith("--", StringComparison.Ordinal) || key == "content"))
            {
                options[key] = args[++index];
            }
            else
            {
                options[key] = null;
            }
        }

        return new CliInvocation(positionals.ToArray(), options);
    }

    public async Task<int> ExecuteAsync()
    {
        var command = _positionals.Length switch
        {
            0 => string.Empty,
            1 => _positionals[0],
            _ when _positionals.Length >= 3 && ((_positionals[0] == "repository" && _positionals[1] == "agents") || _positionals[0] == "agent" || (_positionals[0] == "agents" && _positionals[1] == "bootstrap")) => string.Join(' ', _positionals.Take(3)),
            _ => string.Join(' ', _positionals.Take(2))
        };
        var requestedFormat = Option("format") ?? "text";
        if (!requestedFormat.Equals("text", StringComparison.OrdinalIgnoreCase) && !requestedFormat.Equals("json", StringComparison.OrdinalIgnoreCase) && !requestedFormat.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            return Write("command", "failed", "INVALID_FORMAT", "Use --format text, json, or table.", null, 2, false);
        }

        var json = string.Equals(requestedFormat, "json", StringComparison.OrdinalIgnoreCase) || Has("events-json");
        if (Has("events-json") && string.Equals(Option("format"), "json", StringComparison.OrdinalIgnoreCase))
        {
            return Write("command", "failed", "INVALID_OPTIONS", "Use either --format json or --events-json, not both.", null, 2, json);
        }

        if (_positionals.Length == 0 || command is "help" or "--help" or "-h")
        {
            return WriteHelp(json);
        }

        if (command is "version" or "--version")
        {
            return Write("version", "ok", "OK", "Rule Vault CLI version information.", new
            {
                cli_version = "0.1.0",
                schema_version = 1,
                platform = Environment.OSVersion.Platform.ToString()
            }, 0, json);
        }

        if (command == "agent capabilities")
        {
            return Write(command, "ok", "OK", "Machine-readable opaque agent contract.", AgentCapabilityContract(), 0, json);
        }

        if (command == "capabilities")
        {
            return Write(command, "ok", "OK", "Machine-readable Rule Vault authoring contract.", CapabilityContract(), 0, json);
        }

        if (command is "status" or "doctor")
        {
            return Write(command, "ok", "OK", "No live registry was opened; provide --config-root for an explicit diagnostic target.", new
            {
                config_root = Option("config-root"),
                live_state_changed = false,
                native_exact_watch = ExactFileWatchProbe.Probe()
            }, 0, json);
        }

        if (command == "agent register")
        {
            return await RegisterAgentSessionAsync(json);
        }

        if (command == "agent context")
        {
            return await BuildAgentContextAsync(json);
        }

        if (command == "agent read")
        {
            return await ReadAgentSessionAsync(json);
        }

        if (command == "agent write")
        {
            return await WriteAgentSessionAsync(json);
        }

        if (command == "agent repository read")
        {
            return await ReadAgentRepositoryAsync(json);
        }

        if (command == "agent repository write")
        {
            return await WriteAgentRepositoryAsync(json);
        }

        if (command == "agent repository initialize")
        {
            return await InitializeAgentRepositoryAsync(json);
        }

        if (command is "agent usage" or "agent status" or "agent list")
        {
            return await ShowAgentUsageAsync(json);
        }

        if (command == "agent clear")
        {
            return await ClearAgentSessionAsync(json);
        }

        if (command == "agent project create")
        {
            return await CreateAgentProjectAsync(json);
        }

        if (command == "agent project inspect")
        {
            return await InspectAgentProjectAsync(json);
        }

        if (command == "agent daily inspect")
        {
            return await InspectAgentDailyAsync(json);
        }

        if (command == "agent daily rollover")
        {
            return await RolloverAgentDailyAsync(json);
        }

        if (command == "agent links")
        {
            return await InspectAgentLinksAsync(json);
        }

        if (command == "agent delete")
        {
            return await DeleteAgentContentAsync(json);
        }

        if (command == "install plan")
        {
            return await CreateInstallPlanAsync(json);
        }

        if (command == "update plan")
        {
            return await CreateUpdatePlanAsync(json);
        }

        if (command == "repair plan")
        {
            return await CreateIntegrityRepairPlanAsync(json);
        }

        if (command is "vault inspect" or "vault status")
        {
            return await InspectVaultAsync(command, json);
        }

        if (command == "vault identity")
        {
            return await InspectVaultIdentityAsync(json);
        }

        if (command == "vault read")
        {
            return await ReadVaultAsync(json);
        }

        if (command == "vault write")
        {
            return await WriteVaultAsync(json);
        }

        if (command == "vault links")
        {
            return await InspectVaultLinksAsync(json);
        }

        if (command == "vault delete")
        {
            return await DeleteVaultAsync(json);
        }

        if (command == "project create")
        {
            return await CreateProjectAsync(json);
        }

        if (command == "daily inspect")
        {
            return await InspectDailyAsync(json);
        }

        if (command == "daily rollover")
        {
            return await RolloverDailyAsync(json);
        }

        if (command == "repository agents read")
        {
            return await ReadRepositoryAgentsAsync(json);
        }

        if (command == "repository agents write")
        {
            return await WriteRepositoryAgentsAsync(json);
        }

        if (command == "agents discover")
        {
            return DiscoverAgent(json);
        }

        if (command == "agents bootstrap discover")
        {
            return await DiscoverBootstrapsAsync(json);
        }

        if (command == "agents bootstrap apply")
        {
            return await ApplyBootstrapsAsync(json);
        }

        if (command == "agents write")
        {
            return await WriteAgentOutputAsync(json);
        }

        if (command == "plan show")
        {
            return await ShowPlanAsync(json);
        }

        if (command == "plan apply")
        {
            return await ApplyPlanAsync(json);
        }

        if (command == "context")
        {
            return await BuildContextAsync(json);
        }

        if (IsKnownCommand(command))
        {
            return Write(command, "unavailable", "COMMAND_UNAVAILABLE", "This command is not available in this build. Use the documented supported alternative; no partial action was attempted.", UnavailableCommand(command), 7, json);
        }

        return Write(command, "failed", "UNKNOWN_COMMAND", "Unknown command. Run rv help for the supported command set.", null, 2, json);
    }

    private async Task<int> CreateInstallPlanAsync(bool json)
    {
        var vaultRoot = Option("vault-root");
        var configRoot = Option("config-root");
        var vaultId = Option("vault-id");
        if (string.IsNullOrWhiteSpace(vaultRoot) || string.IsNullOrWhiteSpace(configRoot) || string.IsNullOrWhiteSpace(vaultId))
        {
            return Write("install plan", "needs-input", "EXPLICIT_ROOTS_REQUIRED", "Install planning requires --vault-root, --config-root, and --vault-id; no default/live location is used.", null, 3, json);
        }

        LifecyclePlan plan;
        try
        {
            plan = await LifecyclePlanner.CreateNewInstallAsync(configRoot, vaultRoot, vaultId, DateTimeOffset.UtcNow, Option("package-root"), Option("guide-sha256"));
        }
        catch (LifecycleException exception)
        {
            return Write("install plan", "blocked", exception.Code, exception.Message, null, 4, json);
        }
        var output = Option("output");
        if (!string.IsNullOrWhiteSpace(output))
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(plan, JsonOptions));
        }

        return Write("install plan", "ok", "OK", "Created a reviewable install plan without mutation.", plan, 0, json);
    }

    private async Task<int> CreateUpdatePlanAsync(bool json)
    {
        var vaultRoot = Option("vault-root");
        var configRoot = Option("config-root");
        var vaultId = Option("vault-id");
        var packageRoot = Option("package-root");
        var guideSha256 = Option("guide-sha256");
        if (string.IsNullOrWhiteSpace(vaultRoot) || string.IsNullOrWhiteSpace(configRoot) || string.IsNullOrWhiteSpace(vaultId) || string.IsNullOrWhiteSpace(packageRoot) || string.IsNullOrWhiteSpace(guideSha256))
        {
            return Write("update plan", "needs-input", "EXPLICIT_ROOTS_REQUIRED", "Update planning requires --vault-root, --config-root, --vault-id, --package-root, and --guide-sha256; no default/live location is used.", null, 3, json);
        }

        try
        {
            var inspection = await VaultInspector.InspectIdentityAsync(vaultRoot);
            if (!string.Equals(inspection.VaultId, vaultId, StringComparison.OrdinalIgnoreCase))
            {
                return Write("update plan", "blocked", "VAULT_ID_MISMATCH", "Selected vault identity does not match --vault-id; no update plan was created.", null, 4, json);
            }

            var update = await LifecyclePlanner.CreateAdditiveUpdateAsync(configRoot, vaultRoot, vaultId, packageRoot, guideSha256, DateTimeOffset.UtcNow);
            var plan = update.Plan;
            var output = Option("output");
            if (!string.IsNullOrWhiteSpace(output))
            {
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(plan, JsonOptions));
            }

            return Write("update plan", "ok", "OK", "Created a reviewed update plan that adds missing runtime files and preserves divergent installed content.", new
            {
                plan,
                added_files = update.AddedFiles,
                converted_vendor_files = update.ConvertedVendorFiles,
                preserved_divergent_files = update.PreservedDivergentFiles,
                archive_root = update.ArchiveRoot,
                read_metrics = update.ReadMetrics
            }, 0, json);
        }
        catch (LifecycleException exception)
        {
            return Write("update plan", "blocked", exception.Code, exception.Message, null, 4, json);
        }
        catch (StorageFormatException exception)
        {
            return Write("update plan", "blocked", "MALFORMED_INSTALLATION", $"{exception.Code}: {exception.Message} Existing bytes were preserved and no update plan was created.", null, 4, json);
        }
        catch (Exception exception) when (exception is SafePathException or FileNotFoundException or UnauthorizedAccessException)
        {
            return Write("update plan", "blocked", "MALFORMED_INSTALLATION", $"{exception.Message} Existing bytes were preserved and no update plan was created.", null, 4, json);
        }
    }

    private async Task<int> CreateIntegrityRepairPlanAsync(bool json)
    {
        var vaultRoot = Option("vault-root");
        var configRoot = Option("config-root");
        var vaultId = Option("vault-id");
        var packageRoot = Option("package-root");
        var strategy = Option("strategy");
        if (string.IsNullOrWhiteSpace(vaultRoot) ||
            string.IsNullOrWhiteSpace(configRoot) ||
            string.IsNullOrWhiteSpace(vaultId) ||
            string.IsNullOrWhiteSpace(packageRoot) ||
            string.IsNullOrWhiteSpace(strategy))
        {
            return Write(
                "repair plan",
                "needs-input",
                "INTEGRITY_REPAIR_INPUT_REQUIRED",
                "Provide --vault-root, --config-root, --vault-id, --package-root, and --strategy restore-package|accept-current. No files are changed while planning.",
                null,
                3,
                json);
        }

        try
        {
            var repair = await LifecyclePlanner.CreateIntegrityRepairAsync(
                configRoot,
                vaultRoot,
                vaultId,
                packageRoot,
                strategy,
                DateTimeOffset.UtcNow);
            var output = Option("output");
            if (!string.IsNullOrWhiteSpace(output))
            {
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(repair.Plan, JsonOptions));
            }

            return Write(
                "repair plan",
                "ok",
                "OK",
                "Created an explicit integrity-repair plan. Existing records and divergent bytes are archived before any accepted or restored state is committed.",
                repair,
                0,
                json);
        }
        catch (LifecycleException exception)
        {
            return Write("repair plan", "blocked", exception.Code, exception.Message, null, 4, json);
        }
        catch (StorageFormatException exception)
        {
            return Write("repair plan", "blocked", exception.Code, exception.Message, null, 4, json);
        }
        catch (Exception exception) when (exception is SafePathException or FileNotFoundException or UnauthorizedAccessException)
        {
            return Write("repair plan", "blocked", "INTEGRITY_REPAIR_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> InspectVaultAsync(string command, bool json)
    {
        var vaultRoot = Option("vault-root");
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            return Write(command, "needs-input", "VAULT_ROOT_REQUIRED", "Provide an explicit --vault-root; no live vault is selected by default.", null, 3, json);
        }

        try
        {
            var inspection = await VaultInspector.InspectAsync(vaultRoot);
            return Write(command, "ok", "OK", "Verified selected vault metadata and canonical index data.", inspection, 0, json);
        }
        catch (StorageFormatException exception)
        {
            return Write(command, "blocked", exception.Code, exception.Message, null, 4, json);
        }
        catch (Exception exception) when (exception is SafePathException or FileNotFoundException or UnauthorizedAccessException)
        {
            return Write(command, "blocked", "VAULT_INSPECTION_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> InspectVaultIdentityAsync(bool json)
    {
        var vaultRoot = Option("vault-root");
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            return Write("vault identity", "needs-input", "VAULT_ROOT_REQUIRED", "Provide an explicit --vault-root; no live vault is selected by default.", null, 3, json);
        }

        try
        {
            var identity = await VaultInspector.InspectIdentityAsync(vaultRoot);
            return Write("vault identity", "ok", "OK", "Read immutable vault identity and the current manifest digest without trusting protected content.", identity, 0, json);
        }
        catch (StorageFormatException exception) { return Write("vault identity", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or FileNotFoundException or UnauthorizedAccessException)
        {
            return Write("vault identity", "blocked", "VAULT_IDENTITY_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> BuildContextAsync(bool json)
    {
        var vaultRoot = Option("vault-root");
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            return Write("context", "needs-input", "VAULT_ROOT_REQUIRED", "Provide an explicit --vault-root; no live vault is selected by default.", null, 3, json);
        }

        try
        {
            var descriptor = TaskDescriptor.Create(
                Option("project") ?? "global",
                VaultInspector.ParseOperation(Option("operation") ?? "read"),
                Csv("subjects"),
                Csv("paths"),
                VaultInspector.ParseAudience(Option("audience") ?? "private"),
                BooleanOption("include-history", false),
                IntegerOption("optional-budget-chars", 8000, minimum: 0));
            var context = await VaultInspector.BuildTaskPacketAsync(vaultRoot, descriptor, NullableIntegerOption("max-total-chars", minimum: 0));
            var packet = context.Packet;
            return Write("context", "ok", "OK", "Built the verified deterministic task context packet.", new
            {
                body = packet.Body,
                body_sha256 = packet.BodySha256,
                characters = packet.Characters,
                utf8_bytes = packet.Utf8Bytes,
                approximate_tokens = packet.ApproximateTokens,
                segments = packet.Segments.Select(segment => new { segment.RouteId, segment.FileId, segment.RelativePath, segment.Mandatory, segment.CanonicalSha256 }),
                diagnostics = packet.Diagnostics
            }, 0, json);
        }
        catch (ContextCatalogException exception)
        {
            return Write("context", "blocked", exception.Code, exception.Message, null, 4, json);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return Write("context", "failed", "TASK_DESCRIPTOR_INVALID", exception.Message, null, 2, json);
        }
        catch (StorageFormatException exception)
        {
            return Write("context", "blocked", exception.Code, exception.Message, null, 4, json);
        }
        catch (Exception exception) when (exception is SafePathException or FileNotFoundException or UnauthorizedAccessException)
        {
            return Write("context", "blocked", "CONTEXT_READ_FAILED", exception.Message, null, 4, json);
        }
    }

    private int DiscoverAgent(bool json)
    {
        var adapterId = Option("adapter") ?? "codex-cli";
        var discoveryRoot = Option("home-root");
        if (string.IsNullOrWhiteSpace(discoveryRoot))
        {
            return Write("agents discover", "needs-input", "DISCOVERY_ROOT_REQUIRED", "Provide an explicit --home-root fixture; discovery never defaults to personal agent configuration.", null, 3, json);
        }

        try
        {
            var adapter = BuiltInAdapters.Get(adapterId);
            var discovery = BuiltInAdapters.Discover(adapter, discoveryRoot);
            return Write("agents discover", "ok", "OK", "Completed bounded non-executing adapter discovery.", discovery, 0, json);
        }
        catch (Exception exception) when (exception is ArgumentException or SafePathException)
        {
            return Write("agents discover", "failed", "ADAPTER_DISCOVERY_FAILED", exception.Message, null, 2, json);
        }
    }

    private async Task<int> DiscoverBootstrapsAsync(bool json)
    {
        try
        {
            var homeRoot = Option("home-root") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var result = await AgentBootstrapManager.DiscoverAsync(homeRoot);
            return Write("agents bootstrap discover", "ok", "OK", "Discovered bounded known agent configuration targets without executing agent software.", new { home_root = Path.GetFullPath(homeRoot), targets = result }, 0, json);
        }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or UnauthorizedAccessException or IOException)
        {
            return Write("agents bootstrap discover", "blocked", "BOOTSTRAP_DISCOVERY_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> ApplyBootstrapsAsync(bool json)
    {
        var all = string.Equals(Option("all"), "true", StringComparison.OrdinalIgnoreCase);
        var adapter = Option("adapter");
        if (!all && string.IsNullOrWhiteSpace(adapter))
        {
            return Write("agents bootstrap apply", "needs-input", "BOOTSTRAP_SELECTION_REQUIRED", "Run discovery, then provide --adapter <id> or --all true. No agent configuration was changed.", null, 3, json);
        }

        if (all && !string.IsNullOrWhiteSpace(adapter))
        {
            return Write("agents bootstrap apply", "failed", "BOOTSTRAP_SELECTION_AMBIGUOUS", "Use either one --adapter or --all true.", null, 2, json);
        }

        try
        {
            var configRoot = AgentSessions.ResolveConfigRoot(Option("config-root"));
            var descriptor = Option("descriptor") ?? Path.Combine(configRoot, "rule-vault-cli.json");
            if (!File.Exists(descriptor))
            {
                return Write("agents bootstrap apply", "blocked", "CLI_DESCRIPTOR_NOT_FOUND", "The installed Rule Vault descriptor is required before a bootstrap can be written.", null, 4, json);
            }

            var homeRoot = Option("home-root") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var result = await AgentBootstrapManager.ApplyAsync(homeRoot, Path.GetFullPath(descriptor), string.IsNullOrWhiteSpace(adapter) ? null : [adapter], all);
            return Write("agents bootstrap apply", "ok", "OK", "Applied only selected, recognized Rule Vault bootstrap targets; ambiguous or conflicting targets were left unchanged.", result, 0, json);
        }
        catch (ArgumentException exception) { return Write("agents bootstrap apply", "failed", "BOOTSTRAP_SELECTION_INVALID", exception.Message, null, 2, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or UnauthorizedAccessException or IOException or WriteConflictException)
        {
            return Write("agents bootstrap apply", "blocked", "BOOTSTRAP_APPLY_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> RegisterAgentSessionAsync(bool json)
    {
        var session = Option("session-id"); var name = Option("name"); var folder = Option("folder"); var project = Option("project");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(project))
        {
            return Write("agent register", "needs-input", "AGENT_REGISTRATION_INPUT_REQUIRED", "Provide --session-id, --name, --folder, and --project. The CLI resolves the selected vault internally and never returns its location.", null, 3, json);
        }

        try
        {
            var result = await AgentSessions.RegisterAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, name, folder, project, Option("agent-kind"));
            return Write("agent register", "ok", "OK", "Registered the agent session. Sessions inactive for more than three days are automatically removed.", result, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent register", "blocked", exception.Code, exception.Message, null, 4, json); }
    }

    private async Task<int> BuildAgentContextAsync(bool json)
    {
        var session = Option("session-id");
        var operation = Option("operation");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(operation))
        {
            return Write("agent context", "needs-input", "AGENT_CONTEXT_INPUT_REQUIRED", "Provide --session-id and --operation read|edit|test|review|release|maintain-vault. Add comma-separated --subjects and repository-relative --paths when they apply.", null, 3, json);
        }

        try
        {
            var result = await AgentSessions.BuildContextAsync(
                AgentSessions.ResolveConfigRoot(Option("config-root")),
                session,
                VaultInspector.ParseOperation(operation),
                Csv("subjects"),
                Csv("paths"),
                VaultInspector.ParseAudience(Option("audience") ?? "private"),
                BooleanOption("include-history", false),
                IntegerOption("optional-budget-chars", 8000, minimum: 0),
                NullableIntegerOption("max-total-chars", minimum: 0));
            return Write("agent context", "ok", "OK", "Loaded every applicable mandatory rule and the task-scoped optional context. Agent startup is complete for this session.", result, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent context", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (ContextCatalogException exception) { return Write("agent context", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent context", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return Write("agent context", "failed", "TASK_DESCRIPTOR_INVALID", exception.Message, null, 2, json);
        }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or FileNotFoundException)
        {
            return Write("agent context", "blocked", "AGENT_CONTEXT_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> ReadAgentSessionAsync(bool json)
    {
        var session = Option("session-id"); var path = Option("path"); var paths = Option("paths");
        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(paths))
        {
            return Write("agent read", "failed", "AGENT_READ_INPUT_AMBIGUOUS", "Use --path for one file or --paths for a comma-separated set, not both.", null, 2, json);
        }
        if (string.IsNullOrWhiteSpace(session) || (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(paths)))
        {
            return Write("agent read", "needs-input", "AGENT_READ_INPUT_REQUIRED", "Provide --session-id and either vault-relative --path or comma-separated --paths. Register first; do not supply a vault root.", null, 3, json);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(paths))
            {
                var requested = paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var batch = await AgentSessions.ReadManyAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, requested);
                return batch.RequiresAdditionalRefresh
                    ? Write("agent read", "blocked", "AGENT_BASIS_STALE", "The requested files were refreshed, but other previously read files changed. Refresh the reported relative paths before proceeding.", batch, 5, json)
                    : Write("agent read", "ok", "OK", "Read the requested files in one verified snapshot and returned a fresh opaque basis hash.", batch, 0, json);
            }

            var result = await AgentSessions.ReadAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, path!);
            return result.RequiresAdditionalRefresh
                ? Write("agent read", "blocked", "AGENT_BASIS_STALE", "The requested file was refreshed, but other previously read files changed. Refresh the reported relative paths before proceeding.", result, 5, json)
                : Write("agent read", "ok", "OK", "Read content with a fresh opaque session basis hash.", result, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent read", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent read", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or FileNotFoundException) { return Write("agent read", "blocked", "AGENT_READ_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> WriteAgentSessionAsync(bool json)
    {
        var session = Option("session-id"); var path = Option("path"); var content = Option("content"); var contentFile = Option("content-file");
        if (content is not null && contentFile is not null)
        {
            return Write("agent write", "failed", "CONTENT_INPUT_AMBIGUOUS", "Use exactly one of --content or --content-file.", null, 2, json);
        }
        if (contentFile is not null)
        {
            content = await File.ReadAllTextAsync(contentFile);
        }

        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(path) || content is null)
        {
            return Write("agent write", "needs-input", "AGENT_WRITE_INPUT_REQUIRED", "Provide --session-id, vault-relative --path, and --content or --content-file. Existing files require the raw hash returned by agent read.", null, 3, json);
        }

        try
        {
            var result = await AgentSessions.WriteAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, path, content, Option("expected-raw-sha256"));
            return Write("agent write", "ok", "OK", "Committed the mediated write and invalidated affected readers. Read again to obtain a new basis hash before the next dependent operation.", result, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent write", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent write", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or WriteConflictException or FileNotFoundException) { return Write("agent write", "blocked", "AGENT_WRITE_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> ReadAgentRepositoryAsync(bool json)
    {
        var session = Option("session-id");
        var path = Option("path");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(path))
        {
            return Write("agent repository read", "needs-input", "AGENT_REPOSITORY_READ_INPUT_REQUIRED", "Provide --session-id and a repository-relative .agents Markdown --path. The Git root is resolved from the registered workspace folder.", null, 3, json);
        }

        try
        {
            var result = await AgentSessions.ReadRepositoryAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, path);
            return result.RequiresAdditionalRefresh
                ? Write("agent repository read", "blocked", "AGENT_BASIS_STALE", "The requested repository file was refreshed, but other previously read data changed.", result, 5, json)
                : Write("agent repository read", "ok", "OK", "Read current-branch .agents data through the no-follow Git ingest gate and refreshed the opaque basis hash.", result, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent repository read", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent repository read", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (SafePathException exception) { return WriteRepositoryPathFailure("agent repository read", exception, json); }
        catch (Exception exception) when (exception is StorageFormatException or FileNotFoundException)
        {
            return Write("agent repository read", "blocked", "AGENT_REPOSITORY_READ_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> WriteAgentRepositoryAsync(bool json)
    {
        var session = Option("session-id");
        var path = Option("path");
        var content = Option("content");
        var contentFile = Option("content-file");
        if (content is not null && contentFile is not null)
        {
            return Write("agent repository write", "failed", "CONTENT_INPUT_AMBIGUOUS", "Use exactly one of --content or --content-file.", null, 2, json);
        }
        if (contentFile is not null) { content = await File.ReadAllTextAsync(contentFile); }
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(path) || content is null)
        {
            return Write("agent repository write", "needs-input", "AGENT_REPOSITORY_WRITE_INPUT_REQUIRED", "Provide --session-id, .agents Markdown --path, and --content or --content-file. Existing files require the hash returned by agent repository read.", null, 3, json);
        }

        try
        {
            var result = await AgentSessions.WriteRepositoryAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, path, content, Option("expected-raw-sha256"));
            return Write("agent repository write", "ok", "OK", "Committed the current-branch .agents change through the no-follow gate and invalidated affected readers.", result, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent repository write", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent repository write", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (SafePathException exception) { return WriteRepositoryPathFailure("agent repository write", exception, json); }
        catch (Exception exception) when (exception is StorageFormatException or WriteConflictException or FileNotFoundException)
        {
            return Write("agent repository write", "blocked", "AGENT_REPOSITORY_WRITE_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> InitializeAgentRepositoryAsync(bool json)
    {
        var session = Option("session-id"); var project = Option("project"); var title = Option("title");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(title))
        {
            return Write("agent repository initialize", "needs-input", "AGENT_REPOSITORY_INITIALIZE_INPUT_REQUIRED", "Provide --session-id, --project, and --title after creating or inspecting the private project.", null, 3, json);
        }

        try
        {
            var result = await AgentSessions.InitializeRepositoryAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, project, title);
            return Write("agent repository initialize", "ok", "OK", "Created a Git-backed .agents candidate scaffold with matching project identity and a branch-local integrity manifest. Normal Git review and publication are still required.", result, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent repository initialize", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent repository initialize", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (SafePathException exception) { return WriteRepositoryPathFailure("agent repository initialize", exception, json); }
        catch (Exception exception) when (exception is StorageFormatException or WriteConflictException or FileNotFoundException)
        {
            return Write("agent repository initialize", "blocked", "AGENT_REPOSITORY_INITIALIZE_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> ShowAgentUsageAsync(bool json)
    {
        try
        {
            var result = await AgentSessions.UsageAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), Option("session-id"));
            var detail = string.Equals(Option("detail"), "files", StringComparison.OrdinalIgnoreCase) || string.Equals(Option("detail"), "full", StringComparison.OrdinalIgnoreCase);
            object data = detail ? result : new { overview = result.Overview, sessions = result.Sessions };
            return Write("agent usage", "ok", "OK", detail ? "Reported aggregate, per-session, and per-file token/access usage." : "Reported compact aggregate and per-session token usage. Use --detail files for per-file readers and access counts.", data, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent usage", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or FileNotFoundException) { return Write("agent usage", "blocked", "AGENT_USAGE_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> ClearAgentSessionAsync(bool json)
    {
        try
        {
            var all = string.Equals(Option("all"), "true", StringComparison.OrdinalIgnoreCase);
            var removed = await AgentSessions.ClearAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), Option("session-id"), all);
            return Write("agent clear", "ok", "OK", $"Removed {removed} agent session record(s).", new { removed }, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent clear", "blocked", exception.Code, exception.Message, null, 4, json); }
    }

    private async Task<int> CreateAgentProjectAsync(bool json)
    {
        var session = Option("session-id"); var slug = Option("slug"); var title = Option("title");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(title))
        {
            return Write("agent project create", "needs-input", "AGENT_PROJECT_INPUT_REQUIRED", "Provide --session-id, --slug, and --title. The selected vault is resolved internally.", null, 3, json);
        }

        try { var result = await AgentSessions.CreateProjectAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, slug, title, Option("purpose"), Option("end-goal")); return Write("agent project create", "ok", "OK", "Created the mediated project structure and invalidated affected readers.", result, 0, json); }
        catch (AgentSessionException exception) { return Write("agent project create", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent project create", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is StorageFormatException or WriteConflictException) { return Write("agent project create", "blocked", "AGENT_PROJECT_CREATE_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> InspectAgentProjectAsync(bool json)
    {
        var session = Option("session-id"); var project = Option("project");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(project))
        {
            return Write("agent project inspect", "needs-input", "AGENT_PROJECT_INPUT_REQUIRED", "Provide --session-id and --project. The private project pointer remains internal.", null, 3, json);
        }

        try
        {
            var result = await AgentSessions.InspectProjectAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, project);
            return result.RequiresAdditionalRefresh
                ? Write("agent project inspect", "blocked", "AGENT_BASIS_STALE", "Project identity was refreshed, but other previously read data changed.", result, 5, json)
                : Write("agent project inspect", "ok", "OK", "Verified the private project identity and storage state without disclosing its location.", result, 0, json);
        }
        catch (AgentSessionException exception) { return Write("agent project inspect", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent project inspect", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or FileNotFoundException)
        {
            return Write("agent project inspect", "blocked", "AGENT_PROJECT_INSPECTION_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> InspectAgentDailyAsync(bool json)
    {
        var session = Option("session-id"); var project = Option("project");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(project))
        {
            return Write("agent daily inspect", "needs-input", "AGENT_DAILY_INPUT_REQUIRED", "Provide --session-id and --project; optionally --date YYYY-MM-DD.", null, 3, json);
        }

        if (!DateOnly.TryParse(Option("date") ?? DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), out var date))
        {
            return Write("agent daily inspect", "failed", "DATE_INVALID", "Use an ISO local date (YYYY-MM-DD).", null, 2, json);
        }

        try { var result = await AgentSessions.InspectDailyAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, project, date); return result.RequiresAdditionalRefresh ? Write("agent daily inspect", "blocked", "AGENT_BASIS_STALE", "The active daily note was refreshed, but other data must be refreshed before proceeding.", result, 5, json) : Write("agent daily inspect", "ok", "OK", "Returned the current daily note with a fresh opaque basis hash.", result, 0, json); }
        catch (AgentSessionException exception) { return Write("agent daily inspect", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent daily inspect", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is StorageFormatException or FileNotFoundException) { return Write("agent daily inspect", "blocked", "AGENT_DAILY_INSPECTION_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> RolloverAgentDailyAsync(bool json)
    {
        var session = Option("session-id"); var project = Option("project"); var expected = Option("expected-raw-sha256");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(expected) || !DateOnly.TryParse(Option("date") ?? string.Empty, out var date))
        {
            return Write("agent daily rollover", "needs-input", "AGENT_DAILY_ROLLOVER_INPUT_REQUIRED", "Provide --session-id, --project, --date, --expected-raw-sha256, and --promotions-complete true.", null, 3, json);
        }

        try { var result = await AgentSessions.RolloverDailyAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, project, date, expected, string.Equals(Option("promotions-complete"), "true", StringComparison.OrdinalIgnoreCase)); return Write("agent daily rollover", "ok", "OK", "Archived the explicitly reviewed daily note and created the next note.", result, 0, json); }
        catch (AgentSessionException exception) { return Write("agent daily rollover", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent daily rollover", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is StorageFormatException or WriteConflictException) { return Write("agent daily rollover", "blocked", "AGENT_DAILY_ROLLOVER_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> InspectAgentLinksAsync(bool json)
    {
        var session = Option("session-id"); var path = Option("path");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(path))
        {
            return Write("agent links", "needs-input", "AGENT_LINKS_INPUT_REQUIRED", "Provide --session-id and vault-relative --path.", null, 3, json);
        }

        try { var result = await AgentSessions.InspectLinksAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, path); return result.RequiresAdditionalRefresh ? Write("agent links", "blocked", "AGENT_BASIS_STALE", "The requested link metadata was refreshed, but other data must be refreshed before proceeding.", result, 5, json) : Write("agent links", "ok", "OK", "Returned inbound-link metadata with a fresh opaque basis hash.", result, 0, json); }
        catch (AgentSessionException exception) { return Write("agent links", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent links", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is StorageFormatException or FileNotFoundException) { return Write("agent links", "blocked", "AGENT_LINKS_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> DeleteAgentContentAsync(bool json)
    {
        var session = Option("session-id"); var path = Option("path"); var expected = Option("expected-raw-sha256");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expected))
        {
            return Write("agent delete", "needs-input", "AGENT_DELETE_INPUT_REQUIRED", "Provide --session-id, vault-relative --path, and its current raw hash. Query agent links first.", null, 3, json);
        }

        try { var result = await AgentSessions.DeleteAsync(AgentSessions.ResolveConfigRoot(Option("config-root")), session, path, expected); return Write("agent delete", "ok", "OK", "Deleted mediated content and invalidated affected readers.", result, 0, json); }
        catch (AgentSessionException exception) { return Write("agent delete", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (VaultAuthoringException exception) { return Write("agent delete", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is StorageFormatException or WriteConflictException or FileNotFoundException) { return Write("agent delete", "blocked", "AGENT_DELETE_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> WriteAgentOutputAsync(bool json)
    {
        var vaultRoot = Option("vault-root");
        var adapter = Option("adapter");
        var relativePath = Option("path");
        var content = Option("content");
        if (string.IsNullOrWhiteSpace(vaultRoot) || string.IsNullOrWhiteSpace(adapter) || string.IsNullOrWhiteSpace(relativePath) || content is null)
        {
            return Write("agents write", "needs-input", "AGENT_WRITE_INPUT_REQUIRED", "Provide --vault-root, --adapter, --path, and --content. Read first and pass --expected-raw-sha256 for an existing file.", null, 3, json);
        }

        try
        {
            var result = await AgentVaultWriter.WriteAsync(new AgentVaultWriteRequest(vaultRoot, adapter, relativePath, content, Option("expected-raw-sha256"), Option("config-root")));
            return Write("agents write", "ok", "OK", "Wrote managed vault content through the mediated authoring interface.", result, 0, json);
        }
        catch (AgentVaultWriteException exception)
        {
            return Write("agents write", "blocked", exception.Code, exception.Message, null, 4, json);
        }
        catch (Exception exception) when (exception is SafePathException or FileNotFoundException or WriteConflictException or StorageFormatException)
        {
            return Write("agents write", "blocked", "AGENT_WRITE_FAILED", exception.Message, null, 4, json);
        }
    }

    private async Task<int> ShowPlanAsync(bool json)
    {
        var path = _positionals.Length > 2 ? _positionals[2] : Option("plan");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Write("plan show", "failed", "PLAN_NOT_FOUND", "Provide an existing explicit plan path.", null, 2, json);
        }

        var plan = await ReadPlanAsync(path);
        return Write("plan show", "ok", "OK", $"Plan {plan.PlanId} has {plan.Operations.Count} operations and {plan.RequiredDecisions.Count} required decisions.", plan, 0, json);
    }

    private async Task<int> ApplyPlanAsync(bool json)
    {
        var path = _positionals.Length > 2 ? _positionals[2] : Option("plan");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Write("plan apply", "failed", "PLAN_NOT_FOUND", "Provide an existing explicit plan path.", null, 2, json);
        }

        var plan = await ReadPlanAsync(path);
        if (!string.Equals(Option("approve"), plan.PlanSha256, StringComparison.Ordinal))
        {
            return Write("plan apply", "needs-input", "APPROVAL_DIGEST_REQUIRED", "Pass --approve with the exact plan digest after review.", null, 3, json);
        }

        try
        {
            var acceptedDecisions = (Option("accept-decision") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(decisionId => new LifecycleDecision(decisionId, plan.PlanSha256, plan.PlanSha256, "accepted"))
                .ToArray();
            var written = await LifecycleApplier.ApplyAsync(plan, acceptedDecisions, DateTimeOffset.UtcNow);
            return Write("plan apply", "ok", "OK", $"Applied {written.Count} operations.", new { written }, 0, json);
        }
        catch (LifecycleException exception)
        {
            return Write("plan apply", "blocked", exception.Code, exception.Message, null, exception.Code is "PLAN_STALE" ? 5 : 4, json);
        }
    }

    private static async Task<LifecyclePlan> ReadPlanAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        using var strict = StrictJson.Parse(bytes);
        return JsonSerializer.Deserialize<LifecyclePlan>(strict.RootElement.GetRawText(), JsonOptions)
            ?? throw new InvalidDataException("Plan JSON could not be parsed.");
    }

    private int WriteHelp(bool json)
    {
        var text = "Rule Vault CLI\n\n" +
            "Agent workflow: agent capabilities, agent register, required agent context startup, agent read/write, agent repository read/write, agent project create, agent daily inspect/rollover, agent links/delete, and agent status/clear. Agent commands resolve the selected vault internally and never disclose its location.\n\n" +
            "Administrative workflow: capabilities, version, status, doctor, vault inspect/read/write/links/delete, project create, daily inspect/rollover, context, agents discover/write, agents bootstrap discover/apply, install plan, update plan, repair plan, plan show, plan apply.\n" +
            "Use --format text, json, or table. Use --output-file <path> to export the selected representation. Agent reads return a new opaque basis hash; stale sessions must refresh before writing.";
        return Write("help", "ok", "OK", text, null, 0, json);
    }

    private int WriteRepositoryPathFailure(string command, SafePathException exception, bool json)
    {
        var summary = $"The repository ingest gate rejected this path ({exception.Code}): {exception.Message}";
        return Write(command, "blocked", "RV-SEC-001", summary, null, 4, json);
    }

    private int Write(string command, string status, string code, string summary, object? data, int exitCode, bool json)
    {
        string rendered;
        if (json)
        {
            var envelope = new CliEnvelope(1, command, status, code, summary, data, [], status == "needs-input" ? ["Review the required input and rerun."] : []);
            rendered = JsonSerializer.Serialize(envelope, JsonOptions) + Environment.NewLine;
        }
        else if (string.Equals(Option("format"), "table", StringComparison.OrdinalIgnoreCase))
        {
            rendered = RenderTable(command, status, code, summary, data);
        }
        else
        {
            rendered = summary + Environment.NewLine;
            if (data is not null && Has("verbose"))
            {
                rendered += JsonSerializer.Serialize(data, JsonOptions) + Environment.NewLine;
            }
        }

        var outputFile = Option("output-file");
        if (!string.IsNullOrWhiteSpace(outputFile))
        {
            try
            {
                WriteExport(outputFile, rendered);
            }
            catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or IOException or NotSupportedException)
            {
                Console.Error.WriteLine("Unable to write --output-file: " + exception.Message);
                return 2;
            }
        }
        Console.Out.Write(rendered);
        return exitCode;
    }

    private static void WriteExport(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Output file must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        var stage = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".stage");
        try
        {
            File.WriteAllText(stage, content, new System.Text.UTF8Encoding(false));
            File.Move(stage, fullPath, true);
        }
        finally
        {
            if (File.Exists(stage))
            {
                File.Delete(stage);
            }
        }
    }

    private static string RenderTable(string command, string status, string code, string summary, object? data)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(data ?? new { }, JsonOptions));
        var rows = new List<string[]> { new[] { "command", command }, new[] { "status", status }, new[] { "code", code }, new[] { "summary", summary } };
        var sections = new List<(string Name, JsonElement Values)>();
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    sections.Add((property.Name, property.Value.Clone()));
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    rows.AddRange(property.Value.EnumerateObject().Select(child => new[] { property.Name + "." + child.Name, TableValue(child.Value) }));
                }
                else
                {
                    rows.Add(new[] { property.Name, TableValue(property.Value) });
                }
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            rows.AddRange(document.RootElement.EnumerateArray().Select((value, index) => new[] { index.ToString(System.Globalization.CultureInfo.InvariantCulture), TableValue(value) }));
        }
        else
        {
            rows.Add(new[] { "data", TableValue(document.RootElement) });
        }

        var builder = new System.Text.StringBuilder(RenderGrid(["field", "value"], rows));
        foreach (var section in sections)
        {
            builder.AppendLine(section.Name + ":");
            builder.Append(RenderArrayGrid(section.Values));
        }

        return builder.ToString();
    }

    private static string RenderArrayGrid(JsonElement values)
    {
        var items = values.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            return "(none)" + Environment.NewLine;
        }

        if (items.All(item => item.ValueKind == JsonValueKind.Object))
        {
            var columns = items.SelectMany(item => item.EnumerateObject().Select(property => property.Name)).Distinct(StringComparer.Ordinal).ToArray();
            var rows = items.Select(item => columns.Select(column => item.TryGetProperty(column, out var value) ? TableValue(value) : string.Empty).ToArray()).ToArray();
            return RenderGrid(columns, rows);
        }

        return RenderGrid(["index", "value"], items.Select((item, index) => new[] { index.ToString(System.Globalization.CultureInfo.InvariantCulture), TableValue(item) }).ToArray());
    }

    private static string RenderGrid(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select((header, index) => Math.Max(header.Length, rows.Count == 0 ? 0 : rows.Max(row => row[index].Length))).ToArray();
        var line = "+" + string.Join("+", widths.Select(width => new string('-', width + 2))) + "+" + Environment.NewLine;
        var builder = new System.Text.StringBuilder(line);
        builder.Append('|').Append(string.Join("|", headers.Select((header, index) => " " + header.PadRight(widths[index]) + " "))).Append("|\n").Append(line);
        foreach (var row in rows)
        {
            builder.Append('|').Append(string.Join("|", row.Select((value, index) => " " + value.PadRight(widths[index]) + " "))).Append("|\n");
        }

        return builder.Append(line).ToString();
    }

    private static string TableValue(JsonElement value)
    {
        var text = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? value.GetRawText() : value.ToString();
        text = text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ↵ ", StringComparison.Ordinal);
        return text.Length <= 240 ? text : text[..237] + "...";
    }

    private bool Has(string key) => _options.ContainsKey(key);

    private string? Option(string key) => _options.TryGetValue(key, out var value) ? value : null;

    private string[] Csv(string key) => (Option(key) ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private bool BooleanOption(string key, bool defaultValue)
    {
        var value = Option(key);
        if (string.IsNullOrWhiteSpace(value)) { return defaultValue; }
        return value switch
        {
            "true" => true,
            "false" => false,
            _ => throw new FormatException($"--{key} must be true or false.")
        };
    }

    private int IntegerOption(string key, int defaultValue, int minimum)
    {
        var value = Option(key);
        if (string.IsNullOrWhiteSpace(value)) { return defaultValue; }
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var result) || result < minimum)
        {
            throw new FormatException($"--{key} must be an integer greater than or equal to {minimum}.");
        }
        return result;
    }

    private int? NullableIntegerOption(string key, int minimum)
    {
        var value = Option(key);
        return string.IsNullOrWhiteSpace(value) ? null : IntegerOption(key, minimum, minimum);
    }

    private static object UnavailableCommand(string command)
    {
        var operation = UnavailableOperations.Single(item => item.Command == command);
        return new { command = operation.Command, reason = operation.Reason, supported_alternative = operation.Alternative };
    }

    private static bool IsKnownCommand(string command) => UnavailableOperations.Any(item => item.Command == command);

    private async Task<int> ReadVaultAsync(bool json)
    {
        var root = Option("vault-root"); var path = Option("path");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return Write("vault read", "needs-input", "VAULT_READ_INPUT_REQUIRED", "Provide --vault-root and --path.", null, 3, json);
        }

        try { var result = await VaultAuthoring.ReadAsync(root, path); return Write("vault read", "ok", "OK", "Read managed vault content with its current raw SHA-256.", result, 0, json); }
        catch (VaultAuthoringException exception) { return Write("vault read", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or FileNotFoundException) { return Write("vault read", "blocked", "VAULT_READ_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> WriteVaultAsync(bool json)
    {
        var root = Option("vault-root"); var path = Option("path"); var content = Option("content"); var contentFile = Option("content-file");
        if (content is not null && contentFile is not null)
        {
            return Write("vault write", "failed", "CONTENT_INPUT_AMBIGUOUS", "Use exactly one of --content or --content-file.", null, 2, json);
        }

        if (contentFile is not null)
        {
            content = await File.ReadAllTextAsync(contentFile);
        }

        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path) || content is null)
        {
            return Write("vault write", "needs-input", "VAULT_WRITE_INPUT_REQUIRED", "Provide --vault-root, --path, and --content or --content-file. Existing files require --expected-raw-sha256.", null, 3, json);
        }

        try { var result = await VaultAuthoring.WriteAsync(new VaultWriteRequest(root, Option("config-root"), path, content, Option("expected-raw-sha256"))); return Write("vault write", "ok", "OK", "Committed mediated managed vault write.", result, 0, json); }
        catch (VaultAuthoringException exception) { return Write("vault write", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or WriteConflictException or FileNotFoundException) { return Write("vault write", "blocked", "VAULT_WRITE_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> CreateProjectAsync(bool json)
    {
        var root = Option("vault-root"); var config = Option("config-root"); var slug = Option("slug"); var title = Option("title");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(title))
        {
            return Write("project create", "needs-input", "PROJECT_CREATE_INPUT_REQUIRED", "Provide --vault-root, --config-root, --slug, and --title. Purpose and end goal may be explicitly unknown.", null, 3, json);
        }

        try { var result = await VaultAuthoring.CreateProjectAsync(new ProjectCreateRequest(root, config, slug, title, Option("purpose"), Option("end-goal"))); return Write("project create", "ok", "OK", "Created project routing, rules/context indexes, current state, and first daily stream.", result, 0, json); }
        catch (VaultAuthoringException exception) { return Write("project create", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or WriteConflictException or FileNotFoundException) { return Write("project create", "blocked", "PROJECT_CREATE_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> InspectVaultLinksAsync(bool json)
    {
        var root = Option("vault-root"); var path = Option("path");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return Write("vault links", "needs-input", "VAULT_LINKS_INPUT_REQUIRED", "Provide --vault-root and --path.", null, 3, json);
        }

        try { var result = await VaultAuthoring.InspectInboundLinksAsync(root, path); return Write("vault links", "ok", "OK", "Reported current inbound-link metadata for the managed file.", result, 0, json); }
        catch (VaultAuthoringException exception) { return Write("vault links", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or FileNotFoundException) { return Write("vault links", "blocked", "VAULT_LINKS_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> DeleteVaultAsync(bool json)
    {
        var root = Option("vault-root"); var path = Option("path"); var expected = Option("expected-raw-sha256");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expected))
        {
            return Write("vault delete", "needs-input", "VAULT_DELETE_INPUT_REQUIRED", "Provide --vault-root, --path, and the current --expected-raw-sha256. Query vault links first.", null, 3, json);
        }

        try { var result = await VaultAuthoring.DeleteAsync(root, path, expected); return Write("vault delete", "ok", "OK", "Deleted the unprotected managed file after inbound-link verification.", new { changed_paths = result }, 0, json); }
        catch (VaultAuthoringException exception) { return Write("vault delete", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or WriteConflictException or FileNotFoundException) { return Write("vault delete", "blocked", "VAULT_DELETE_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> InspectDailyAsync(bool json)
    {
        var root = Option("vault-root"); var project = Option("project");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(project))
        {
            return Write("daily inspect", "needs-input", "DAILY_INPUT_REQUIRED", "Provide --vault-root and --project; optionally provide --date YYYY-MM-DD.", null, 3, json);
        }

        if (!DateOnly.TryParse(Option("date") ?? DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), out var date))
        {
            return Write("daily inspect", "failed", "DATE_INVALID", "Use an ISO local date (YYYY-MM-DD).", null, 2, json);
        }

        try { var result = await VaultAuthoring.InspectDailyAsync(root, project, date, Option("config-root")); return Write("daily inspect", "ok", "OK", result.RolloverRequired ? "Daily rollover is required; previous note is returned for explicit review and promotion." : "Current daily note is ready.", result, 0, json); }
        catch (VaultAuthoringException exception) { return Write("daily inspect", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or FileNotFoundException) { return Write("daily inspect", "blocked", "DAILY_INSPECTION_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> RolloverDailyAsync(bool json)
    {
        var root = Option("vault-root"); var config = Option("config-root"); var project = Option("project"); var expected = Option("expected-raw-sha256");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(expected))
        {
            return Write("daily rollover", "needs-input", "DAILY_ROLLOVER_INPUT_REQUIRED", "Inspect the active note first, then provide --vault-root, --config-root, --project, --expected-raw-sha256, --date, and --promotions-complete true.", null, 3, json);
        }

        if (!DateOnly.TryParse(Option("date") ?? string.Empty, out var date))
        {
            return Write("daily rollover", "failed", "DATE_INVALID", "Use an ISO local date (YYYY-MM-DD).", null, 2, json);
        }

        try { var result = await VaultAuthoring.RolloverDailyAsync(root, config, project, date, expected, string.Equals(Option("promotions-complete"), "true", StringComparison.OrdinalIgnoreCase)); return Write("daily rollover", "ok", "OK", result.Count == 0 ? "No rollover was required." : "Archived the reviewed daily note and created the new active daily note.", new { changed_paths = result }, 0, json); }
        catch (VaultAuthoringException exception) { return Write("daily rollover", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (Exception exception) when (exception is SafePathException or StorageFormatException or WriteConflictException or FileNotFoundException) { return Write("daily rollover", "blocked", "DAILY_ROLLOVER_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> ReadRepositoryAgentsAsync(bool json)
    {
        var root = Option("repository-root"); var path = Option("path");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return Write("repository agents read", "needs-input", "REPOSITORY_AGENTS_INPUT_REQUIRED", "Provide --repository-root and .agents --path.", null, 3, json);
        }

        try { var result = await RepositoryAgentsAuthoring.ReadAsync(root, path); return Write("repository agents read", "ok", "OK", "Read a Git-gated .agents file with its raw SHA-256.", result, 0, json); }
        catch (VaultAuthoringException exception) { return Write("repository agents read", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (SafePathException exception) { return WriteRepositoryPathFailure("repository agents read", exception, json); }
        catch (Exception exception) when (exception is FileNotFoundException or StorageFormatException) { return Write("repository agents read", "blocked", "REPOSITORY_AGENTS_READ_FAILED", exception.Message, null, 4, json); }
    }

    private async Task<int> WriteRepositoryAgentsAsync(bool json)
    {
        var root = Option("repository-root"); var path = Option("path"); var content = Option("content"); var contentFile = Option("content-file");
        if (content is not null && contentFile is not null)
        {
            return Write("repository agents write", "failed", "CONTENT_INPUT_AMBIGUOUS", "Use exactly one of --content or --content-file.", null, 2, json);
        }

        if (contentFile is not null)
        {
            content = await File.ReadAllTextAsync(contentFile);
        }

        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path) || content is null)
        {
            return Write("repository agents write", "needs-input", "REPOSITORY_AGENTS_INPUT_REQUIRED", "Provide --repository-root, .agents --path, and --content or --content-file. Existing files require --expected-raw-sha256.", null, 3, json);
        }

        try { var result = await RepositoryAgentsAuthoring.WriteAsync(new RepositoryAgentsWriteRequest(root, path, content, Option("expected-raw-sha256"))); return Write("repository agents write", "ok", "OK", "Wrote ordinary Git-gated .agents Markdown through the dedicated adapter.", result, 0, json); }
        catch (VaultAuthoringException exception) { return Write("repository agents write", "blocked", exception.Code, exception.Message, null, 4, json); }
        catch (SafePathException exception) { return WriteRepositoryPathFailure("repository agents write", exception, json); }
        catch (Exception exception) when (exception is FileNotFoundException or WriteConflictException) { return Write("repository agents write", "blocked", "REPOSITORY_AGENTS_WRITE_FAILED", exception.Message, null, 4, json); }
    }

    private static object CapabilityContract() => new
    {
        schema_version = 1,
        contract = "Rule Vault CLI mediated authoring",
        discovery = new { command = "rv capabilities --format json", descriptor = "AI-Rule-Vault/rule-vault-cli.json" },
        operations = new object[]
        {
            new { name = "agent register", purpose = "Register an opaque agent session with its friendly name, thread/session identity, folder context, and project.", safety = "selected vault resolves internally; stale sessions expire after three days" },
            new { name = "agent read", purpose = "Read one managed file with --path or one verified set with --paths and receive a new opaque freshness basis hash.", safety = "verifies requested protected files; blocks dependent work when previously read content changed; never returns the vault location" },
            new { name = "agent write", purpose = "Commit a mediated managed Markdown write for a registered, current session.", safety = "raw-hash precondition; stale-basis block; affected reader sessions are invalidated" },
            new { name = "agent repository read/write/initialize", purpose = "Use the registered workspace folder to initialize or access current-branch .agents Markdown/metadata without disclosing the private vault.", safety = "Git object and no-follow path gate on every access; strict parse plus applicable schema validation before verified authority; protected shared files are paired with their branch manifest" },
            new { name = "agent usage", purpose = "Show compact token totals or per-file read/write token and access details.", safety = "cl100k_base model-agnostic estimate; no vault-root disclosure" },
            new { name = "agent status", purpose = "Alias for agent usage; show sessions with current or stale content bases.", safety = "does not disclose the private vault filesystem location" },
            new { name = "agent clear", purpose = "Remove one registered session or all session records.", safety = "explicit session id or --all true" },
            new { name = "agents bootstrap discover/apply", purpose = "Show bounded known user-level agent instruction targets, then add/update one owned Rule Vault bootstrap per explicit selection.", safety = "does not execute agent software; absent directories are not created; ambiguous or conflicting legacy text is not overwritten" },
            new { name = "vault read", purpose = "Administrative: read a managed vault Markdown file and its optimistic-concurrency raw hash.", safety = "explicit vault root; no-follow path validation" },
            new { name = "vault write", purpose = "Create or update managed Markdown including internal link-sidecar maintenance.", safety = "raw-hash precondition; protected locks; journal; protected files require manifest and registry anchor update" },
            new { name = "vault links", purpose = "Report inbound sources before a move, rename, or deletion.", safety = "checks managed metadata consistency without a broad vault scan" },
            new { name = "vault delete", purpose = "Delete an unprotected managed file after all inbound sources are resolved.", safety = "raw-hash precondition; refuses live inbound links; target and sidecar journaled together" },
            new { name = "project create", purpose = "Create a project router, rules/context indexes, current state, and daily stream.", safety = "explicit project identity; root index integrity transaction" },
            new { name = "daily inspect", purpose = "Return the full active note and tell the agent whether rollover is required.", safety = "no mutation" },
            new { name = "daily rollover", purpose = "Archive a reviewed daily note after explicit promotions and create the next daily note.", safety = "previous-note hash and promotion confirmation; protected multi-target journal" },
            new { name = "repair plan", purpose = "Create an administrator-reviewed integrity repair that either restores package runtime or accepts current protected bytes.", safety = "archives prior records and divergent bytes; required decision and exact plan digest; no ignore/bypass mode" },
            new { name = "agents write", purpose = "Compatibility wrapper for vault write used by named adapters.", safety = "same mediated authoring controls" },
            new { name = "repository agents read/write", purpose = "Read or write ordinary Markdown under a Git repository .agents directory.", safety = "Git work-tree and object-type gate; no-follow reparse rejection; deterministic frontmatter status; raw-hash precondition for writes; no private-vault content hash comparison because branch divergence is expected; content is never executed" }
        },
        semantic_responsibilities = new[] { "classify rule versus context", "select the minimum routing scope", "read and summarize the previous daily note", "promote durable outcomes before rollover", "perform whole-file always-file deduplication before mutation", "classify privacy signals before Git promotion", "resolve semantic conflicts", "obtain user classification for untrusted command-like data" }
    };

    private static object AgentCapabilityContract() => new
    {
        schema_version = 1,
        contract = "Rule Vault opaque agent session contract",
        vault_location = "not_disclosed",
        startup = new
        {
            required = true,
            sequence = new[]
            {
                "Run rv agent capabilities --format json.",
                "Run rv agent register --session-id <id> --name <friendly-name> --folder <workspace> --project <project> --format json.",
                "Before any other agent operation, run rv agent context --session-id <id> --operation <read|edit|test|review|release|maintain-vault> --subjects <comma-separated> --paths <comma-separated repository-relative paths> --format json."
            },
            completion = "agent context verifies the protected route catalog, reads index.md, loads every applicable always route, adds project router/index/current-state/daily scope when present, and returns one complete packet plus a fresh basis hash",
            enforcement = "all other agent operations fail until context completes; write commands require edit/release/maintain-vault context, and project/daily/repository commands require their matching subject or .agents path scope"
        },
        registration = new { command = "rv agent register --session-id <id> --name <friendly-name> --folder <workspace> --project <project> --format json", expires_after_inactive_days = 3, result = "returns startup_required and the required startup sequence" },
        operations = new object[]
        {
            new { name = "agent context", purpose = "Compile and return the complete deterministic task packet for the registered project, operation, subjects, and paths.", freshness = "Completes mandatory startup, tracks every returned source file, and returns the new opaque basis hash." },
            new { name = "agent read", purpose = "Read one vault-relative managed Markdown file with --path or an atomic requested set with comma-separated --paths; includes protected .vault-system Markdown needed for validation.", freshness = "Every request returns a basis over the session's current read set; a changed earlier read blocks dependent operations until refreshed." },
            new { name = "agent write", purpose = "Create or update managed Markdown through the selected vault's mediated writer.", freshness = "Requires a current session basis and raw precondition; affected sessions become stale." },
            new { name = "agent project create", purpose = "Create missing private project structure without exposing the vault root.", freshness = "Requires a current session basis; invalidates readers of changed paths." },
            new { name = "agent project inspect", purpose = "Verify a private project's immutable identity and current storage/Git state.", freshness = "Adds the project pointer to the session basis without returning its private location." },
            new { name = "agent daily inspect", purpose = "Read the active daily note and determine whether explicit rollover is required.", freshness = "Returns a new opaque basis hash." },
            new { name = "agent daily rollover", purpose = "Archive a reviewed prior daily note and create the next note.", freshness = "Requires explicit promotion confirmation and a current basis." },
            new { name = "agent links", purpose = "Inspect inbound links before updating references or deleting a managed file.", freshness = "Returns a new opaque basis hash." },
            new { name = "agent delete", purpose = "Delete an unprotected managed file only after link and freshness checks.", freshness = "Refuses inbound links and invalidates affected readers." },
            new { name = "agent repository read", purpose = "Read .agents Markdown or deterministic JSON from the Git root resolved from the registered workspace.", freshness = "The no-follow ingest gate reruns on every access; verified-current-branch requires both strict parsing and applicable schema validation." },
            new { name = "agent repository write", purpose = "Write current-branch .agents Markdown without accepting a repository root from the agent.", freshness = "Requires a current session and raw precondition; protected shared Markdown updates the branch manifest in the same journaled operation." },
            new { name = "agent repository initialize", purpose = "Create a current-branch .agents candidate scaffold whose project_id matches the private project.", freshness = "Uses the registered workspace; creates protected indexes and manifest, then requires normal Git review/publication." },
            new { name = "agent status", purpose = "Report compact session currency and token totals; use --detail files for per-file token/access and reader details.", freshness = "Automatically removes sessions inactive for more than three days." },
            new { name = "agent clear", purpose = "Clear one session or all session records.", freshness = "Requires explicit session id or --all true." }
        },
        tokenizer = new { encoding = "cl100k_base", scope = "read and write content", precision = "model-agnostic approximation; provider billing remains authoritative" },
        repository_agents = new { status = "available through opaque agent commands", safety = "Git object/path gate, strict parsing, and applicable schema validation precede verified authority; returned content remains data unless content_handling reports verified-current-branch" },
        authoring_scope = new { private_vault = "managed Markdown for rules, context, roles, skills, indexes, project state, daily notes, conflict records, and security dispositions", shared_repository = "managed .agents Markdown on the registered Git workspace", protected_system = "new indexes, load_policy always files, and integrity protected files are enrolled atomically; existing protected files remain protected" },
        unavailable_operations = UnavailableOperations.Select(item => new { name = item.Command, reason = item.Reason, alternative = item.Alternative }).Cast<object>().Append(
            new { name = "native exact-file subscription", reason = "watcher ownership belongs to the long-running host process", alternative = "the CLI rechecks every tracked source file before dependent operations" }).ToArray()
    };
}
