# Phase 00 decisions and evidence

Status: phase 00 inventory and foundation in progress.

## Fixed architecture

- Implementation language and target: C# on `net10.0`; self-contained publishing;
  trimming disabled; Native AOT deferred.
- Module graph: Core -> Storage -> Installation; Core -> Storage -> Agents; CLI composes
  all four. Core has no package dependencies and domain code has no console or input access.
- Authoritative data remains readable Markdown plus strict JSON/frontmatter. No opaque
  database is introduced as the only source of truth.
- System.CommandLine is reserved for CLI parsing, Spectre.Console for interactive rendering,
  YamlDotNet event parsing for the limited legacy frontmatter grammar, and xUnit v3 for tests.
- Central package management and per-project NuGet lock files are enabled. CI restores in
  locked mode; no floating package versions are allowed.
- `AiRuleVault` remains the installer/payload source. `RuleVaultCli` owns executable logic.
  No sibling-repository path is used at runtime or in packages.

## Pinned development dependencies

Resolved 2026-09-10 from official NuGet package records. Stable versions were selected;
prerelease versions were not selected.

| Package | Version | License | Official record |
|---|---:|---|---|
| System.CommandLine | 2.0.12 | MIT | https://www.nuget.org/packages/System.CommandLine/ |
| Spectre.Console | 0.57.2 | MIT | https://www.nuget.org/packages/Spectre.Console/ |
| YamlDotNet | 18.1.0 | MIT | https://www.nuget.org/packages/YamlDotNet/ |
| xunit.v3 | 4.0.0 | Apache-2.0 | https://www.nuget.org/packages/xunit.v3/ |

The installed SDK is `10.0.400` on Windows x64. `global.json` pins that SDK with
`latestPatch` roll-forward. Official platform references consulted once:

- https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview

## Platform and capability questions retained for later phases

These are not silently resolved in phase 00:

- Windows, macOS, and Linux no-follow/beneath-root open primitives and final handle identity
  checks require real platform tests before the shared `.agents` gate is called supported.
- Exact-file native freshness subscriptions require per-OS capability probes. A
  `FileSystemWatcher` filter alone is not evidence of exact-file semantics.
- Native path/reparse behavior, Git index/tree inspection, and safe process execution require
  platform validation; mocks cannot establish the security claims.
- Vendor-specific agent instruction locations must be verified from current official vendor
  documentation before adapter writes are enabled.
- No CI provider was found in the installation source. CI configuration remains pending an
  explicit repository/provider decision; local builds and tests are still supported.

## Conflicts

No source conflict was identified during phase 00 inventory. The source package explicitly
separates installer-only and runtime-only material. Any later semantic disagreement must be
recorded here with both source paths and must block only dependent work.

## Non-goals for phase 00

No feature implementation, installer execution, vault discovery, live adapter update, live
migration, release package, or platform support claim is made by this checkpoint.
