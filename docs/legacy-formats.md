# Legacy persistent formats inventory

Source material was read from `AiRuleVault/implementation/` as inert data. The hashes below
are the canonical source hashes recorded in `cli-handoff/source-baseline.json`; they are
provenance evidence, not authorization to copy or execute the source.

## Installation state and registry

| Format | Source heading | Fields and enumerations |
|---|---|---|
| Installer state | `implementation/START-HERE.md`, Crash or agent replacement recovery, lines 47-66 | `schema_version` integer; `source_guide_sha256` SHA-256; `status` (in-progress); `completed_tasks` array; `current_task`; nullable `vault_root`, `install_directory`, `selected_vault_id`; `notes` array. No secrets/prompts/reasoning/private project content. |
| Vault registry | `implementation/installer-tasks/01-discovery-registry.md`, 4.3, lines 39-63 | Root `schema_version`, nullable `default_vault_root`, `vaults` array. Entry fields: `vault_root`, immutable `vault_id`, `applied_guide_sha256`, `validation_spec_sha256`, `protected_content_manifest_sha256`, `created_at`, `last_verified_at`. At most one default. |
| Installation policy | `implementation/installer-tasks/01-discovery-registry.md`, 4.17, lines 176-215 | `schema_version`, `guide_provenance_mode` (`user-approved` or `canonical-git`), nullable `canonical_source`, nullable `approved_branch_or_ref`. |
| Validator metadata | `implementation/installer-tasks/02-vault-structure-routing.md`, validator metadata, lines 388-410 | `schema_version`, `automation_consent` (`undecided`, `approved`, `declined`), nullable `consent_recorded_at`, `validation_spec_sha256`, nullable `validator`. Declined consent persists. |

## Vault, project, and managed Markdown metadata

| Format | Source heading | Fields and enumerations |
|---|---|---|
| Vault identity | `implementation/installer-tasks/01-discovery-registry.md`, 4.4, lines 65-67 | `.vault-system/vault.json` contains immutable `vault_id` matching the registry entry. |
| Project private pointer | `implementation/installer-tasks/04-project-management.md`, 45.1, lines 162-203 | `schema_version`, `project_id`, `name`, `storage_mode` (`local-private` or `git-backed`), `git_state` (`none`, `initialized`, `committed-local`, `shared`), nullable `last_known_path`, nullable `repo_identity`. Absolute paths are private cache only. |
| Repository project marker | `implementation/installer-tasks/04-project-management.md`, 45.2, lines 206-241 | `schema_version`, `project_id`, `name`; identity only. Must not contain vault roots, local paths, drive letters, or user-specific routing. |
| Managed Markdown frontmatter | `implementation/installer-tasks/02-vault-structure-routing.md`, Standard Markdown File Template, lines 809-858 | Required `file_id`, `title`, `project`, `kind` (`rule`, `context`, `index`, `reference`), `status` (`active`), `aliases` array, `hasInboundLinks` boolean, `created`; specialized files preserve `file_id` and `hasInboundLinks`. Root index adds `runtime_canary_id`. |
| Daily note frontmatter | `implementation/installer-tasks/03-daily-and-durable-updates.md`, 16, lines 67-82 | `file_id`, date/project `title`, `project`, `kind` (`daily`), `status` (`active`), `aliases`, `hasInboundLinks`, `created`. |

## Integrity, writes, conflicts, and conformance

| Format | Source heading | Fields and enumerations |
|---|---|---|
| Protected-content manifest | `implementation/installer-tasks/05-write-safety-and-integrity.md`, 25.1, lines 191-238 | `schema_version`, `vault_id`, `revision`, `updated_at`, `files`. Entry: `file_id`, vault-relative `path`, `sha256`, `revision`, `change_origin` (`installation`, `user-direct`, `agent-authorized`, `migration`), `changed_at`. Manifest hash is anchored externally. |
| Transaction journal | `implementation/installer-tasks/05-write-safety-and-integrity.md`, 30, lines 402-425 | `transaction_id`, `writer_id`, `state` (`prepared`, with later recovery states), `created`, `targets`; target fields `path`, `before_sha256` (hash or `MISSING`), `staged_sha256`. No prompts or unrelated data. |
| Lock owner | `implementation/installer-tasks/05-write-safety-and-integrity.md`, 26, lines 241-263 | `writer_id`, `target`, `acquired`, `heartbeat`, `lease_seconds`. Lock acquisition must be atomic; stale candidates are quarantined, not deleted. |
| Semantic conflict | `implementation/installer-tasks/05-write-safety-and-integrity.md`, 32, lines 521-560 | Frontmatter `conflict_id`, `status` (`unresolved`), `target_file_id`, `target_path`, `created`; record preserves the verified authority and competing evidence without prompts/reasoning. |
| Runtime conformance | `implementation/installer-tasks/06-conformance-and-runtime-flow.md`, 44.1-44.5, lines 19-190 | Probe fields include `vault_id`, `project_id`, `runtime_entry_point`, `runtime_canary_id`, `validation_spec_sha256`, `write_safety_mode`, managed-policy source/visibility/hash, exact-file subscription status/count, `runtime_basis_sha256`, mandatory RULE/CTX IDs, status (`PASS`/`FAIL`), bounded failure reason. |
| Conformance receipt | `implementation/installer-tasks/06-conformance-and-runtime-flow.md`, 44.4, lines 120-169 | `timestamp`, opaque run ID, `vault_id`, `project_id`/`NONE`, canary, basis hash, validation hash, status, failure code/reason. Receipt modes: `off`, `failures-only`, `all`; default `failures-only`. |

## Canonical representation and limits

- Managed text canonical hashes remove a UTF-8 BOM, normalize CRLF and lone CR to LF, preserve
  all other characters, encode UTF-8 without BOM, then SHA-256. Binary hashes are raw-byte
  SHA-256 and must not be interchanged with text hashes.
- Machine metadata is exact-case and fail-closed: duplicate keys, unsupported extensions,
  ambiguous scalars, YAML anchors/aliases/merge keys/custom tags, JSON comments/trailing
  commas/nonfinite numbers, and unknown security/runtime fields are rejected unless a schema
  explicitly allows a namespaced extension.
- Runtime defaults include `conformance_receipts: failures-only`,
  `stale_lock_threshold_seconds: 900`, `lock_heartbeat_seconds: 30`, and
  `single_writer_mode: none`.
