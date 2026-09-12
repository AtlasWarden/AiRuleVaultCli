# Rule Vault

Rule Vault is a local rules and context store for AI agents. The bundled CLI keeps
the vault location private, verifies protected content, coordinates concurrent agent
sessions, manages project and daily files, and safely reads or writes Git `.agents`
content.

## Install on Windows

Extract `artifacts/RuleVault-0.1.0-dev-win-x64.zip`, open PowerShell in that folder,
and run:

```powershell
.\install.ps1
```

Press Enter to accept recommended choices, or use the number/arrow-key menus. The
installer handles new installs, updates, migrations, and archive-backed integrity
repair. It never changes an existing vault or agent bootstrap without approval.

## Use

Run `rv --help` for human commands. Agents discover the installed executable from
`%LOCALAPPDATA%\AI-Rule-Vault\rule-vault-cli.json`, then call `agent capabilities`,
`agent register`, and the required `agent context` startup command before other
opaque agent operations.

Commands support `--format text`, `--format json`, or `--format table`, plus
`--output-file <path>` for exports.

## Build and test

Requires the .NET SDK selected by `global.json` and PowerShell 7.

```powershell
.\build\build.ps1 -Target verify
.\build\create-package.ps1
```

The second command creates `InstallerPackage` and the self-contained Windows release
ZIP under `artifacts`.
