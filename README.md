# Rule Vault CLI

This repository contains the compiled Rule Vault CLI and its modular storage, installation,
agent, and command-line layers. The installer source and runtime payload remain in the
`AiRuleVault` repository.

The Windows release provides mediated vault authoring, migration/install planning, Git-gated
repository `.agents` access, opaque agent sessions with freshness hashes and token accounting,
and an optional bounded bootstrap manager for known user-level agent configuration paths.

Use `rv agent capabilities --format json` for the agent-facing contract. Human users can choose
`--format text`, `json`, or `table`, and can export a response with `--output-file <path>`.
Bootstrap discovery is reviewable before mutation: `rv agents bootstrap discover`, then
`rv agents bootstrap apply --adapter <id>` or `--all true`. Missing directories, ambiguous legacy
content, and conflicting ownership markers are left unchanged.

Development and automated validation use synthetic fixtures and disposable temporary roots.
