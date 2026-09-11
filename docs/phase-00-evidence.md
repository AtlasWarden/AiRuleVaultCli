# Phase 00 evidence

Captured 2026-09-10. This is a concise handoff record, not runtime authority.

## Source reconciliation

- Source: `C:\Users\Justin\source\repos\AiRuleVault`.
- Destination: `C:\Users\Justin\source\repos\RuleVaultCli` was absent before setup.
- Git source state: `main...origin/main`, only untracked `cli-handoff/` present.
- Baseline: all 24 entries in `cli-handoff/source-baseline.json` matched canonical hashes and
  recorded character counts.
- Recovery-stage directory was empty; no source schema, CI, installer project, or build helper
  beyond the inventoried Markdown/Python source was found.
- Legacy Python files were analyzed as source material and were not executed.

## Toolchain evidence

- Installed SDK: .NET SDK `10.0.400`, host runtime `10.0.11`, Windows `10.0.26200`, `win-x64`.
- Git: `2.55.0.windows.5`.
- No CI provider configuration was present in the source tree.
- Pinned packages and official references are recorded in `docs/decisions.md`.

## Foundation outputs

- `RuleVaultCli.sln` and five production module projects exist.
- Unit, integration, and platform test projects exist with phase-00 smoke tests.
- Central package management, lock-file restore, nullable analysis, deterministic builds, and
  CI warnings-as-errors are configured.
- Initial strict JSON Schema 2020-12 sources are stored under `schemas/`.
- Legacy format inventory and the security-control coverage ledger are stored under `docs/`.
- `dotnet restore --locked-mode` passed for all eight projects.
- CI-style Release build passed with zero errors; `dotnet test` passed with 3 test executables,
  3 succeeded, 0 failed, and 0 skipped.
- `dotnet format --verify-no-changes` passed.
- The CLI `--help` path rendered successfully and exited 0.

## Safety boundary

No live vault root, registry, agent configuration, installer script, migration script, or
existing runtime payload was activated or modified by this phase. Temporary test installations
and synthetic fixtures remain the only permitted targets for later phases.
