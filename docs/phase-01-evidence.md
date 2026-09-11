# Phase 01 evidence

Phase 01 implementation is present but its gate remains open pending one host capability.

## Implemented

- Canonical UTF-8 text hashing strips one BOM and normalizes CRLF/lone CR to LF; raw-byte hashing remains separate.
- Strict JSON performs duplicate-key preflight at every object depth and rejects comments, trailing commas, unknown fields, invalid UTF-8, and configured size/depth limits.
- Strict YAML uses parser events, requires one mapping document, rejects duplicate keys, aliases, anchors, tags, ambiguous plain scalars, and unsupported shapes; only lowercase `true`/`false` receive boolean typing.
- Windows trusted reads use a directory-relative `NtCreateFile` chain with `FILE_OPEN_REPARSE_POINT`, retained handles, final handle identity checks, and multiple-hard-link rejection. Other platforms return explicit unsupported status for shared-agent paths.
- Standard writes use same-directory staging, fresh raw-hash preconditions, atomic replacement where the filesystem supports it, and post-write verification. Protected locks use atomic `CreateNew` ownership files; journals are staged and replace-written.

## Synthetic and platform evidence

- `dotnet test RuleVaultCli.sln --configuration Release --no-restore`: 61 passed, 0 skipped, 0 failed in the final Windows build.
- Unit coverage includes canonical hash vectors, JSON/YAML hostile metadata, hostile relative paths, write conflicts, lock competition, and journal round-trip.
- Windows platform coverage includes a real hard-link rejection and nested directory-relative trusted read.
- With Developer Mode enabled, a real Windows directory-symlink escape was created in an isolated fixture and rejected by the mediated agent-write path; no file was written outside the vault.
- Integration coverage verifies managed vault authoring, protected manifest/registry re-anchoring, link-sidecar maintenance, project creation, daily rollover/promotion confirmation, lazy global daily creation, regular Git `.agents` writing, and current-branch protected `.agents` manifest updates.

## Remaining phase-boundary work

- The dedicated repository `.agents` adapter now gates Git object type and physical no-follow containment before read/write. It deliberately treats other-branch bytes as normal divergence; exact cross-platform evidence and RV-SEC-001 daily-disposition recording remain follow-up work.
- Protected stale-lock recovery still needs prepared-journal coordination and process-start identity checks before its control can be marked complete.
- Native exact-file watcher capability is intentionally not claimed here; it remains a later adapter/platform concern.

The live vault and registry were read only to create and compare isolated fixtures; no live vault, registry, agent configuration, or user installation was modified.
