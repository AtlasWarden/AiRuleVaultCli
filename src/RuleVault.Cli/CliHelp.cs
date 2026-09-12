using System.Text;

internal sealed record CliHelpOption(string Syntax, string Description);

internal sealed record CliHelpCommand(
    string Group,
    string Name,
    string Summary,
    string Usage,
    IReadOnlyList<CliHelpOption> Options,
    IReadOnlyList<string> Examples,
    IReadOnlyList<string>? Aliases = null);

internal sealed record CliHelpDocument(
    string Executable,
    string? Topic,
    string Summary,
    IReadOnlyList<CliHelpOption> GlobalOptions,
    IReadOnlyList<CliHelpCommand> Commands);

internal static class CliHelp
{
    private static readonly CliHelpOption[] GlobalOptions =
    [
        new("--help, -h", "Show general help or help for the selected command."),
        new("--format <text|json|table>", "Choose terminal text, JSON, or table output."),
        new("--output-file <path>", "Save a copy of the result to a file."),
        new("--verbose", "Show extra details when they are available.")
    ];

    private static readonly CliHelpCommand[] Commands =
    [
        Command("Commands for people", "version", "Show which Rule Vault version is installed.", "rulevault version", [], ["rulevault version"]),
        Command("Commands for AI agents", "agent capabilities", "Show AI agents how to use Rule Vault and which startup steps are required.", "rulevault agent capabilities [global options]", [], ["rulevault agent capabilities --format json"]),
        Command("Commands for apps and scripts", "capabilities", "Show advanced apps and scripts what Rule Vault can do.", "rulevault capabilities [global options]", [], ["rulevault capabilities --format json"]),
        Command("Commands for people", "agent doctor", "Check whether this program can see the newest vault files and explain how to fix stale copies.", "rulevault agent doctor [--config-root <path>] [global options]", [Opt("--config-root <path>", "Use a specific Rule Vault settings folder. Most users do not need this.")], ["rulevault agent doctor"]),
        Command("Commands for people", "status", "Show basic diagnostic information without changing the vault.", "rulevault status [--config-root <path>] [global options]", [Opt("--config-root <path>", "Use a specific Rule Vault settings folder.")], ["rulevault status"], ["doctor"]),

        Command("AI agent commands", "agent register", "Tell Rule Vault which AI agent is working and which project it is using.", "rulevault agent register --session-id <id> --name <name> --folder <path> --project <name> [options]", [Opt("--session-id <id>", "Unique ID for this chat or work session."), Opt("--name <name>", "A friendly name, such as Codex."), Opt("--folder <path>", "The project folder the agent is working in."), Opt("--project <name>", "The project name, or global when no project applies."), Opt("--agent-kind <kind>", "Optional name for the kind of AI agent."), Opt("--config-root <path>", "Use a specific Rule Vault settings folder. Most users do not need this.")], ["rulevault agent register --session-id 123 --name Codex --folder C:\\work\\app --project app --format json"]),
        Command("AI agent commands", "agent context", "Give an agent the rules and project information it needs for its current task.", "rulevault agent context --session-id <id> --operation <operation> [options]", [Opt("--session-id <id>", "The ID used when the agent registered."), Opt("--operation <read|edit|test|review|release|maintain-vault>", "What the agent is about to do."), Opt("--subjects <csv>", "Topics needed for the task, separated by commas."), Opt("--paths <csv>", "Project files involved in the task, separated by commas."), Opt("--project <name>", "Use a different project when needed."), Opt("--audience <private|shared>", "Whether the information is private or shared."), Opt("--known-context-sha256 <sha256>", "The safety code from the agent's previous context, used to spot changes."), Opt("--include-history <true|false>", "Include useful older information."), Opt("--optional-budget-chars <count>", "Limit optional information to this many characters."), Opt("--max-total-chars <count>", "Limit the complete result to this many characters.")], ["rulevault agent context --session-id 123 --operation edit --subjects rules,project --paths src/App.cs --format json"]),
        Command("AI agent commands", "agent read", "Let an agent safely read one or more files stored in Rule Vault.", "rulevault agent read --session-id <id> (--path <path> | --paths <csv>) [options]", [Opt("--session-id <id>", "The ID used when the agent registered."), Opt("--path <path>", "One file inside Rule Vault."), Opt("--paths <csv>", "Several Rule Vault files, separated by commas."), Opt("--config-root <path>", "Use a specific Rule Vault settings folder. Most users do not need this.")], ["rulevault agent read --session-id 123 --paths global/context.md,projects/app/current.md --format json"]),
        Command("AI agent commands", "agent write", "Let an agent safely create or update a Rule Vault file without overwriting newer work.", "rulevault agent write --session-id <id> --path <path> (--content <text> | --content-file <path>) [options]", [Opt("--expected-raw-sha256 <sha256>", "The file's current safety code. Required when updating an existing file."), Opt("--content <text>", "The new text."), Opt("--content-file <path>", "Read the new text from a file.")], ["rulevault agent write --session-id 123 --path projects/app/context/new.md --content-file update.md --format json"]),
        Command("AI agent commands", "agent repository initialize", "Set up the project's shared .agents folder for the first time.", "rulevault agent repository initialize --session-id <id> --project <slug> --title <title> [options]", [Opt("--session-id <id>", "The ID used when the agent registered."), Opt("--project <slug>", "Short project name used in file paths."), Opt("--title <title>", "Project name shown to people.")], ["rulevault agent repository initialize --session-id 123 --project app --title App --format json"]),
        Command("AI agent commands", "agent repository read", "Safely read a file from the project's shared .agents folder.", "rulevault agent repository read --session-id <id> --path <.agents/path> [options]", [Opt("--session-id <id>", "The ID used when the agent registered."), Opt("--path <path>", "A project path beginning with .agents/.")], ["rulevault agent repository read --session-id 123 --path .agents/project/context.md --format json"]),
        Command("AI agent commands", "agent repository write", "Safely create or update a file in the project's shared .agents folder.", "rulevault agent repository write --session-id <id> --path <.agents/path> (--content <text> | --content-file <path>) [options]", [Opt("--expected-raw-sha256 <sha256>", "The file's current safety code. Required when updating an existing file."), Opt("--content <text>", "The new text."), Opt("--content-file <path>", "Read the new text from a file.")], ["rulevault agent repository write --session-id 123 --path .agents/project/context.md --content-file context.md --format json"]),
        Command("AI agent commands", "agent project create", "Create a new project in Rule Vault.", "rulevault agent project create --session-id <id> --slug <slug> --title <title> [options]", [Opt("--purpose <text>", "Optional reason for the project."), Opt("--end-goal <text>", "Optional description of what done looks like.")], ["rulevault agent project create --session-id 123 --slug app --title App --format json"]),
        Command("AI agent commands", "agent project inspect", "Show a project's saved rules, context, and current status.", "rulevault agent project inspect --session-id <id> --project <slug> [options]", [Opt("--session-id <id>", "The ID used when the agent registered."), Opt("--project <slug>", "Short project name.")], ["rulevault agent project inspect --session-id 123 --project app --format json"]),
        Command("AI agent commands", "agent daily inspect", "Read today's project note and say whether yesterday's note needs to be closed out.", "rulevault agent daily inspect --session-id <id> --project <slug> [--date <YYYY-MM-DD>] [options]", [Opt("--date <YYYY-MM-DD>", "Date to check. Defaults to today.")], ["rulevault agent daily inspect --session-id 123 --project app --format json"]),
        Command("AI agent commands", "agent daily rollover", "Archive the reviewed older daily note and start the new one.", "rulevault agent daily rollover --session-id <id> --project <slug> --date <YYYY-MM-DD> --expected-raw-sha256 <sha256> --promotions-complete true [options]", [Opt("--promotions-complete <true|false>", "Confirms useful information was moved into the right rule or context files."), Opt("--expected-raw-sha256 <sha256>", "The note's safety code returned by daily inspect.")], ["rulevault agent daily rollover --session-id 123 --project app --date 2026-09-11 --expected-raw-sha256 <sha256> --promotions-complete true --format json"]),
        Command("AI agent commands", "agent links", "Show which other vault files point to this file before it is changed or deleted.", "rulevault agent links --session-id <id> --path <path> [options]", [Opt("--session-id <id>", "The ID used when the agent registered."), Opt("--path <path>", "File path inside Rule Vault.")], ["rulevault agent links --session-id 123 --path projects/app/context/topic.md --format json"]),
        Command("AI agent commands", "agent delete", "Safely delete a vault file after checking that nothing still points to it.", "rulevault agent delete --session-id <id> --path <path> --expected-raw-sha256 <sha256> [options]", [Opt("--expected-raw-sha256 <sha256>", "The file's safety code returned by a read."), Opt("--path <path>", "File path inside Rule Vault.")], ["rulevault agent delete --session-id 123 --path projects/app/context/old.md --expected-raw-sha256 <sha256> --format json"]),
        Command("Commands for people", "agent status", "List registered agents, or show one agent's details and files.", "rulevault agent status [<agent-id-or-name>] [options]", [Opt("--agent <id-or-name>", "Show the agent with this ID or friendly name."), Opt("--session-id <id>", "Older name for selecting one agent by ID."), Opt("--detail <summary|files|full>", "Show the agent list, file activity, or all available details.")], ["rulevault agent status", "rulevault agent status \"Codex CLI help implementation\"", "rulevault agent status --agent <session-id>"], ["agent usage", "agent list"]),
        Command("AI agent commands", "agent clear", "Remove old agent-session tracking information. Vault files are not removed.", "rulevault agent clear (--session-id <id> | --all true) [options]", [Opt("--session-id <id>", "One agent session to forget."), Opt("--all true", "Forget all agent sessions.")], ["rulevault agent clear --session-id 123 --format json"]),

        Command("Advanced vault commands", "vault inspect", "Check that a chosen vault is complete and healthy.", "rulevault vault inspect --vault-root <path> [global options]", [Opt("--vault-root <path>", "The vault folder to check.")], ["rulevault vault inspect --vault-root C:\\vault --format json"], ["vault status"]),
        Command("Advanced vault commands", "vault identity", "Show the vault ID and its saved protected-file safety code.", "rulevault vault identity --vault-root <path> [global options]", [Opt("--vault-root <path>", "The vault folder to check.")], ["rulevault vault identity --vault-root C:\\vault --format json"]),
        Command("Advanced vault commands", "vault read", "Read a file directly from a chosen vault and return its safety code.", "rulevault vault read --vault-root <path> --path <vault-relative-path> [global options]", [Opt("--vault-root <path>", "The vault folder to use."), Opt("--path <path>", "File path inside the vault.")], ["rulevault vault read --vault-root C:\\vault --path index.md --format json"]),
        Command("Advanced vault commands", "vault write", "Create or update a file directly in a chosen vault without overwriting newer work.", "rulevault vault write --vault-root <path> --config-root <path> --path <path> (--content <text> | --content-file <path>) [options]", [Opt("--expected-raw-sha256 <sha256>", "The file's current safety code. Required for an existing file.")], ["rulevault vault write --vault-root C:\\vault --config-root C:\\config --path global/context.md --content-file context.md --format json"]),
        Command("Advanced vault commands", "vault links", "Show which vault files point to a chosen file.", "rulevault vault links --vault-root <path> --path <path> [global options]", [Opt("--vault-root <path>", "The vault folder to use."), Opt("--path <path>", "File path inside the vault.")], ["rulevault vault links --vault-root C:\\vault --path global/context.md --format json"]),
        Command("Advanced vault commands", "vault delete", "Safely delete a file after checking that nothing still points to it.", "rulevault vault delete --vault-root <path> --path <path> --expected-raw-sha256 <sha256> [global options]", [Opt("--expected-raw-sha256 <sha256>", "The file's current safety code.")], ["rulevault vault delete --vault-root C:\\vault --path global/old.md --expected-raw-sha256 <sha256> --format json"]),
        Command("Advanced vault commands", "context", "Build one complete set of rules and project information for a task.", "rulevault context --vault-root <path> [options]", [Opt("--project <name>", "Project name, or global."), Opt("--operation <operation>", "What work is being done."), Opt("--subjects <csv>", "Topics needed, separated by commas."), Opt("--paths <csv>", "Project files involved, separated by commas."), Opt("--audience <private|shared>", "Whether the information is private or shared."), Opt("--include-history <true|false>", "Include useful older information."), Opt("--optional-budget-chars <count>", "Limit optional information to this many characters."), Opt("--max-total-chars <count>", "Limit the complete result to this many characters.")], ["rulevault context --vault-root C:\\vault --project app --operation edit --subjects rules --format json"]),
        Command("Advanced vault commands", "project create", "Create a new project with its starting rules, context, status, and daily note.", "rulevault project create --vault-root <path> --config-root <path> --slug <slug> --title <title> [options]", [Opt("--purpose <text>", "Optional reason for the project."), Opt("--end-goal <text>", "Optional description of what done looks like.")], ["rulevault project create --vault-root C:\\vault --config-root C:\\config --slug app --title App --format json"]),
        Command("Advanced vault commands", "daily inspect", "Read a project's daily note and say whether an older note needs to be closed out.", "rulevault daily inspect --vault-root <path> --project <slug> [--date <YYYY-MM-DD>] [options]", [Opt("--config-root <path>", "The Rule Vault settings folder."), Opt("--date <YYYY-MM-DD>", "Date to check. Defaults to today.")], ["rulevault daily inspect --vault-root C:\\vault --project app --format json"]),
        Command("Advanced vault commands", "daily rollover", "Archive the reviewed older daily note and start the new one.", "rulevault daily rollover --vault-root <path> --config-root <path> --project <slug> --date <YYYY-MM-DD> --expected-raw-sha256 <sha256> --promotions-complete true [global options]", [Opt("--promotions-complete <true|false>", "Confirms useful information was moved into the right rule or context files.")], ["rulevault daily rollover --vault-root C:\\vault --config-root C:\\config --project app --date 2026-09-11 --expected-raw-sha256 <sha256> --promotions-complete true --format json"]),

        Command("Projects and AI tools", "repository agents read", "Safely read a file from a project's shared .agents folder.", "rulevault repository agents read --repository-root <path> --path <.agents/path> [global options]", [Opt("--repository-root <path>", "The Git project folder."), Opt("--path <path>", "A project path beginning with .agents/.")], ["rulevault repository agents read --repository-root C:\\work\\app --path .agents/project/context.md --format json"]),
        Command("Projects and AI tools", "repository agents write", "Safely create or update a file in a project's shared .agents folder.", "rulevault repository agents write --repository-root <path> --path <.agents/path> (--content <text> | --content-file <path>) [options]", [Opt("--expected-raw-sha256 <sha256>", "The file's current safety code. Required for an existing file.")], ["rulevault repository agents write --repository-root C:\\work\\app --path .agents/project/context.md --content-file context.md --format json"]),
        Command("Projects and AI tools", "agents discover", "Look for one supported AI tool without starting it or changing its files.", "rulevault agents discover --home-root <path> [--adapter <id>] [global options]", [Opt("--home-root <path>", "The user folder to search."), Opt("--adapter <id>", "AI tool type. Defaults to codex-cli.")], ["rulevault agents discover --home-root C:\\Users\\me --adapter codex-cli --format json"]),
        Command("Projects and AI tools", "agents bootstrap discover", "Show supported AI tools that can be connected to Rule Vault. Nothing is changed.", "rulevault agents bootstrap discover [--home-root <path>] [global options]", [Opt("--home-root <path>", "User folder to search. Defaults to the current user.")], ["rulevault agents bootstrap discover --format table"]),
        Command("Projects and AI tools", "agents bootstrap apply", "Connect one or all safely discovered AI tools to Rule Vault.", "rulevault agents bootstrap apply (--adapter <id> | --all true) [options]", [Opt("--adapter <id>", "Connect one discovered AI tool type."), Opt("--all true", "Connect every safe discovered tool."), Opt("--home-root <path>", "User folder to search."), Opt("--descriptor <path>", "Use a specific trusted Rule Vault connection file.")], ["rulevault agents bootstrap apply --adapter codex-cli --format json"]),
        Command("Projects and AI tools", "agents write", "Older direct command for saving AI-created text in a chosen vault.", "rulevault agents write --vault-root <path> --adapter <id> --path <path> --content <text> [options]", [Opt("--expected-raw-sha256 <sha256>", "The file's current safety code. Required for an existing file."), Opt("--config-root <path>", "The Rule Vault settings folder.")], ["rulevault agents write --vault-root C:\\vault --adapter codex-cli --path projects/app/context.md --content <text> --format json"]),

        Command("Install and repair", "install plan", "Prepare a list of files for a new installation. This command does not make the changes.", "rulevault install plan --vault-root <path> --config-root <path> --vault-id <id> [options]", [Opt("--package-root <path>", "The extracted installer package folder."), Opt("--guide-sha256 <sha256>", "The install guide's safety code."), Opt("--output <path>", "Where to save the plan file.")], ["rulevault install plan --vault-root C:\\vault --config-root C:\\config --vault-id <guid> --package-root C:\\package --output plan.json --format json"]),
        Command("Install and repair", "update plan", "Prepare a list of update and move changes. This command does not make the changes.", "rulevault update plan --vault-root <path> --config-root <path> --vault-id <id> --package-root <path> --guide-sha256 <sha256> [options]", [Opt("--output <path>", "Where to save the plan file.")], ["rulevault update plan --vault-root C:\\vault --config-root C:\\config --vault-id <guid> --package-root C:\\package --guide-sha256 <sha256> --output plan.json --format json"]),
        Command("Install and repair", "repair plan", "Prepare a safe repair that keeps a backup of old or damaged files.", "rulevault repair plan --vault-root <path> --config-root <path> --vault-id <id> --package-root <path> --strategy <restore-package|accept-current> [options]", [Opt("--output <path>", "Where to save the repair plan."), Opt("--strategy <value>", "Restore the installer files or accept the files currently on disk.")], ["rulevault repair plan --vault-root C:\\vault --config-root C:\\config --vault-id <guid> --package-root C:\\package --strategy restore-package --output repair.json --format json"]),
        Command("Install and repair", "plan show", "Show a saved installation, update, or repair plan.", "rulevault plan show <plan-path> [global options]", [Opt("--plan <path>", "Use this plan file instead of giving the path after the command.")], ["rulevault plan show plan.json --format json"]),
        Command("Install and repair", "plan apply", "Make the changes in an approved plan, but only if nothing has changed since it was reviewed.", "rulevault plan apply <plan-path> --approve <plan-sha256> [options]", [Opt("--approve <sha256>", "The plan's safety code shown during review."), Opt("--accept-decision <csv>", "Any choices the plan asked the user to approve, separated by commas.")], ["rulevault plan apply plan.json --approve <plan-sha256> --format json"])
    ];

    public static CliHelpDocument Root() => new(
        "rulevault",
        null,
        "Store rules and project information safely, then share the right information with AI agents.",
        GlobalOptions,
        Commands);

    public static CliHelpDocument? Find(string topic)
    {
        var normalized = Normalize(topic);
        var exact = Commands.FirstOrDefault(command =>
            command.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            (command.Aliases?.Any(alias => alias.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ?? false));
        if (exact is not null)
        {
            return new("rulevault", exact.Name, exact.Summary, GlobalOptions, [exact]);
        }

        var groupCommands = Commands.Where(command =>
            command.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            command.Name.StartsWith(normalized + " ", StringComparison.OrdinalIgnoreCase)).ToArray();
        return groupCommands.Length == 0
            ? null
            : new("rulevault", normalized, $"Commands under '{normalized}'.", GlobalOptions, groupCommands);
    }

    public static string Render(CliHelpDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Rule Vault CLI");
        builder.AppendLine();
        builder.AppendLine(document.Summary);
        builder.AppendLine();

        if (document.Commands.Count == 1 && document.Topic is not null)
        {
            var command = document.Commands[0];
            builder.AppendLine("Usage:");
            builder.Append("  ").AppendLine(command.Usage);
            if (command.Aliases is { Count: > 0 })
            {
                builder.AppendLine();
                builder.Append("Aliases: ").AppendLine(string.Join(", ", command.Aliases));
            }

            if (command.Options.Count > 0)
            {
                builder.AppendLine();
                AppendOptions(builder, "Command options:", command.Options);
            }

            builder.AppendLine();
            AppendOptions(builder, "Global options:", document.GlobalOptions);
            if (command.Examples.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Examples:");
                foreach (var example in command.Examples)
                {
                    builder.Append("  ").AppendLine(example);
                }
            }

            return builder.ToString();
        }

        if (document.Topic is null)
        {
            builder.AppendLine("Usage:");
            builder.AppendLine("  rulevault <command> [options]");
            builder.AppendLine("  rulevault help [command]");
            builder.AppendLine("  rulevault <command> --help");
            builder.AppendLine();
            builder.AppendLine("Commands for people:");
            AppendRootCommand(builder, "agent status", "List agents or show one agent's files.");
            AppendRootCommand(builder, "agent doctor", "Check for stale or mismatched vault files.");
            AppendRootCommand(builder, "agents", "Find and connect supported AI tools.");
            AppendRootCommand(builder, "vault", "Operations for vault interaction.");
            AppendRootCommand(builder, "project", "Create and inspect project information.");
            AppendRootCommand(builder, "daily", "Inspect and close out daily notes.");
            AppendRootCommand(builder, "install", "Prepare a new installation.");
            AppendRootCommand(builder, "update", "Prepare a vault update.");
            AppendRootCommand(builder, "repair", "Prepare a safe vault repair.");
            AppendRootCommand(builder, "plan", "Review or apply a prepared plan.");
            AppendRootCommand(builder, "version", "Show the installed version.");
            builder.AppendLine();
            builder.AppendLine("Commands for AI agents:");
            AppendRootCommand(builder, "agent", "Register, load context, and safely read or write.");
            builder.AppendLine();
            builder.AppendLine("Commands for apps and scripts:");
            AppendRootCommand(builder, "capabilities", "Show the machine-readable command contract.");
            builder.AppendLine();
            builder.AppendLine("Advanced commands:");
            AppendRootCommand(builder, "context", "Build task context from a chosen vault.");
            AppendRootCommand(builder, "repository", "Work directly with a project's .agents files.");
            builder.AppendLine();
            AppendOptions(builder, "Global options:", document.GlobalOptions);
            builder.AppendLine();
            builder.AppendLine("Run 'rulevault <command> --help' to see that command's operations and examples.");
            return builder.ToString();
        }

        builder.AppendLine("Available commands:");
        foreach (var group in document.Commands.GroupBy(command => command.Group))
        {
            builder.Append("  ").AppendLine(group.Key);
            foreach (var command in group)
            {
                builder.Append("    ").Append(command.Name.PadRight(30)).AppendLine(command.Summary);
            }
            builder.AppendLine();
        }

        AppendOptions(builder, "Global options:", document.GlobalOptions);
        builder.AppendLine();
        builder.AppendLine("Run 'rulevault help <command>' for command options and examples.");
        return builder.ToString();
    }

    private static CliHelpCommand Command(string group, string name, string summary, string usage, IReadOnlyList<CliHelpOption> options, IReadOnlyList<string> examples, IReadOnlyList<string>? aliases = null)
    {
        var audienceGroup = name switch
        {
            "agent doctor" or "agent status" or "agent clear" or "agents discover" or "agents bootstrap discover" or "agents bootstrap apply" => "Commands for people",
            _ when name.StartsWith("agent ", StringComparison.Ordinal) => "Commands for AI agents",
            "capabilities" => "Commands for apps and scripts",
            _ when group is "Advanced vault commands" or "Projects and AI tools" or "Install and repair" => "Advanced commands for people",
            _ => group
        };
        return new(audienceGroup, name, summary, usage, options, examples, aliases);
    }

    private static CliHelpOption Opt(string syntax, string description) => new(syntax, description);

    private static string Normalize(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void AppendOptions(StringBuilder builder, string heading, IReadOnlyList<CliHelpOption> options)
    {
        builder.AppendLine(heading);
        var width = options.Max(option => option.Syntax.Length);
        foreach (var option in options)
        {
            builder.Append("  ").Append(option.Syntax.PadRight(width + 2)).AppendLine(option.Description);
        }
    }

    private static void AppendRootCommand(StringBuilder builder, string command, string description)
    {
        builder.Append("  ").Append(command.PadRight(16)).AppendLine(description);
    }
}
