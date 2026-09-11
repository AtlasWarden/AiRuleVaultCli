# Agent-capable vault operations — review evidence

## Scope

Make Rule Vault capable of carrying out every ordinary durable-vault procedure an
agent is required to perform, through a safe and machine-discoverable local CLI
surface. This is a functional correction to the narrow `agents write` feature,
not a relaxation of write, integrity, or repository safety controls.

## Problem and intended outcome

The current CLI can only write an agent-owned note below
`agent-output/<adapter>/`. It cannot create or update a project rule, context,
skill, role, daily note, index, or global file. It cannot create a project,
perform daily rollover, maintain inbound-link metadata, or expose its callable
surface in a form that an agent can discover from a bootstrap location.

An agent using the vault procedure today therefore cannot complete its required
durable-update work through the CLI. The desired outcome is that every such
operation has an explicit, safe CLI command or adapter operation, with the CLI
performing deterministic filesystem/integrity mechanics and the agent supplying
only the semantic content and classification it is authorized to provide.

## Current implementation evidence

- `agents write` permits only `agent-output/<adapter>/`.
- `project create`, daily lifecycle, link maintenance, repository adapter writes,
  recovery, and capability discovery are registered or absent but not implemented.
- `TrustedWriter`, `ProtectedLock`, `TransactionJournal`, strict metadata parsing,
  no-follow reads, and integrity-manifest primitives already exist but are not
  assembled into the normal durable-update workflow.
- The installer writes the CLI package but does not create a discoverable CLI
  contract in the selected AppData configuration root.

## Proposed behavior

1. `agent capabilities --format json` returns a versioned, machine-readable local
   operation contract, path requirements, safety profiles, unsupported states,
   and recovery guidance. The installer stores a bootstrap descriptor in the
   selected AppData configuration root containing the installed executable path
   and capability command.
2. A vault-authoring command supports scoped writes for global, project, role,
   skill, rule, context, index, and daily content. It validates the selected
   vault identity, exact safe relative path, expected precondition hash, and
   required metadata. It never writes outside the selected vault.
3. Authoring operations update `file_id`, `hasInboundLinks`, inbound-reference
   sidecars, affected indexes, integrity manifest, and registry anchor whenever
   the changed file is protected. Structural/multi-file work uses sorted locks
   and a recoverable transaction journal.
4. Project creation scaffolds a router, rules/context/private-context/daily
   structure, current-state file, daily pointer, and necessary indexes without
   inventing a purpose or end goal.
5. Daily rollover reads the active note, returns it for agent review, accepts
   explicit agent-supplied durable promotions, updates their targets, archives
   the prior note, updates archive indexes and the active daily pointer, and
   creates the new note. It does not summarize or invent durable content.
6. Repository `.agents` writes are a separate adapter operation. They remain
   blocked until the existing no-follow Git object/path gate and deterministic
   metadata parser accept the exact target. This is not a general permission to
   write arbitrary repository files.

## Non-goals

- No direct modification of a live vault or agent configuration during development.
- No semantic conflict resolution, durable-content classification, or daily-note
  summarization guessed by the CLI. Those remain agent/user decisions expressed
  through explicit command inputs.
- No bypass of protected integrity updates, locks, path gates, or `.agents`
  ingestion safety.
- No claim of macOS/Linux validation on this Windows-only host.

## Security and data implications

- All writes are constrained to a verified explicit vault root or a separately
  validated repository adapter target.
- Protected writes must update the file, manifest, and external registry anchor
  as one recoverable operation.
- Link sidecars are metadata, not a replacement for accurate link updates.
- Concurrent writers must use the existing lock and stale-write preconditions;
  structural operations require deterministic lock order and journaling.
- The CLI descriptor contains only executable/capability location metadata; it
  does not store prompts, reasoning, or user content.

## Acceptance criteria

- An agent can discover every implemented operation without prior chat context.
- An agent can create/update global and project rules, context, roles, skills,
  indexes, and daily notes through explicit operations in an isolated vault.
- Project creation and daily rollover produce the required routing/index files,
  preserve the prior daily note, and require explicit promotions.
- Protected writes preserve file/manifest/registry consistency across concurrent
  attempts and fail closed on stale or malformed state.
- Link creation/removal/move/delete updates or blocks on inbound-reference
  metadata exactly as required.
- Unsafe `.agents` targets are rejected before access; a valid fixture target can
  receive only the adapter-owned bootstrap content.
- The installer creates a discoverable AppData CLI descriptor.
- All behavior is verified with synthetic fixtures and copied-vault tests only.

## Evidence gaps

- The precise final CLI grammar and project/daily templates need to be derived
  from the currently installed runtime procedures and tested before release.
- macOS/Linux and live provider adapter delivery remain outside this host's
  validation scope.
