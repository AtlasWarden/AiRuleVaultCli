# Local release evidence

## Validated artifact

- RID: win-x64
- CLI version: 0.1.0-dev
- Artifact: artifacts/package-win-x64-r33/bin/win-x64/rv.exe
- Offline package: artifacts/RuleVault-0.1.0-dev-win-x64-r33.zip
- Package manifest: artifacts/package-win-x64-r33/artifacts.json
- Executable SHA-256: 9C7944FDBC6FA81CF955334B5FEA125FA7B13950FBC6390EE603DC56F9FCED57
- Archive SHA-256: C0B33997CF661AC083C0AA196D07324BFDA029B285931BD27A4A8A8FF1A9C64D

The artifact is self-contained and its raw SHA-256 is pinned in the package manifest. The packaged executable passed rv.exe version --format json on the Windows development host.

## Test evidence

- Locked restore, CI-style Release build, and full test suite passed.
- Final suite: 61 passed, 0 failed, 0 skipped.
- Real synthetic Windows directory-symlink rejection, junction-reparse rejection, and hard-link rejection passed after Developer Mode was enabled.
- The self-contained Windows package verified its bundled CLI digest and installed a fresh synthetic vault/configuration pair through Windows PowerShell 5.1.
- An isolated copy of the current vault and AppData registry was updated through the package. The copied user index was unchanged; the immutable vault ID and guide/manifest registry anchors were verified afterward.
- The packaged CLI inspected both the updated copy and the live vault read-only; their canonical index hashes and character counts matched.
- A second installer run on the updated copy created and applied a zero-operation plan, leaving the copied registry unchanged.
- A malformed copy with duplicate JSON metadata returned `MALFORMED_INSTALLATION`; its bytes remained unchanged.
- A fresh r31 temporary installation placed the verified CLI under its temporary configuration root, wrote `rule-vault-cli.json`, and exposed `agent capabilities --format json` from that descriptor. R33 packages the current verified executable, installer guide, and expanded table renderer; its package inventory was reverified.
- The r31 installed CLI registered two opaque agent sessions, read protected root content, invalidated the second reader after the first writer changed it, required a fresh new basis hash, and passed a final integrity inspection. No agent response or table export contained the fixture vault path.
- `agent status` provides compact current/stale session and token totals; `--detail files` reports per-file read/write tokens, access counts, and participating sessions. The CLI uses the `cl100k_base` tokenizer for a provider-neutral estimate, while provider billing remains authoritative.
- All successful command responses support `--format text`, `--format json`, or `--format table`; `--output-file <path>` atomically exports the selected representation. Table output expands result arrays, including agent sessions and per-file token usage, into structured grids. The packaging script now writes its manifest with a Windows PowerShell 5.1-compatible BOM-free UTF-8 API.
- The installer delegates optional bootstrap work to its installed, package-verified CLI. The r31 synthetic-home test replaced one recognized legacy bootstrap, appended one owned block while preserving unrelated text, skipped ambiguous Rule Vault text, and confirmed an all-target rerun was idempotent. It did not create absent agent directories or execute agent software.
- The installed CLI created a global daily stream on first no-project access, created a project router/rules/context/daily structure, wrote and reread a managed rule containing normal YAML frontmatter, and passed a final integrity inspection.
- Managed vault writes use explicit roots, no-follow paths, raw-hash preconditions, per-target locks/journals, internal-link sidecars, and manifest/registry re-anchoring for protected files.
- The CLI reports inbound link sources and refuses an unprotected managed-file deletion until the agent updates those sources; deletion then journals the target and sidecar together.
- A copied live vault plus copied/rebased AppData registry was updated by r24 packaging evidence without touching the live locations; its immutable ID verified and a second update plan had zero operations.
- Git `.agents` operations use a dedicated Git object-type/no-follow gate. Branch-different manifest data is not compared with private-vault data; ordinary Markdown and manifest-listed protected Markdown were tested through the adapter, with protected changes updating the current branch manifest under local locks.
- The Windows installer uses a compact top-of-screen progress indicator and green selected text. Its menu accepts numbers plus Enter or Up/Down plus Enter, and only asks for a vault location. It was parsed and executed with Windows PowerShell 5.1.
- The generic vault writer still blocks `.vault-system` and repository `.agents` paths; the latter is available only through the dedicated repository adapter.

## Honest release boundary

- Tested: Windows 10, win-x64, .NET SDK 10.0.400 build host.
- Not tested: win-arm64, macOS, Linux, POSIX wrapper execution, real provider startup delivery, and exact native file watches.
- Built-in agent capabilities remain explicitly unverified or unsupported unless independently probed.
- No live vault, registry, agent configuration, PATH, shell policy, remote repository, or published release was modified. The live vault and registry were read only to create and compare isolated fixtures.

An independent security/migration/enforcement review remains required before describing this as a production release.
