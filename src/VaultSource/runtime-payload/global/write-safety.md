# Write Safety and Integrity

Load before vault writes. Use the smallest applicable scope and load protected-mode detail only when required.

# 23. Tiered Concurrent Write Safety

Full lock, lease, hash, and transaction handling provides strong protection but is not free. It can add filesystem operations, tool calls, and agent reasoning to edits where concurrent-loss risk is negligible.

Therefore the vault uses **tiered write safety** rather than imposing the maximum protocol on every project.

The project router declares:

```text
Write Safety: standard
```

or:

```text
Write Safety: protected
```

The detailed procedures live in:

```text
<VAULT_ROOT>/.vault-system/write-safety.md
```

### Verified single-writer environment exception

If the host environment is positively verified as a single-user, local standalone environment in which concurrent agent writes to the vault are physically impossible, the agent may bypass directory-locking overhead for otherwise protected structural operations such as daily-note rollover.

This exception removes only **multi-writer locking**. It does not remove crash-consistency protections. The agent must still use:

- immediate fresh reads;
- staged or atomic replacement when supported;
- post-write verification;
- a transaction journal when partial completion could leave materially inconsistent structure.

Do not infer single-writer safety merely because only one agent is currently visible.

The exception is available only when `single_writer_mode` is configured and the agent can verify one of these mechanisms:

- `host-guaranteed`  -  the host/runtime explicitly guarantees that no second writer can access the selected vault during the operation;
- `coordinator-enforced`  -  every vault writer is required to pass through one external mutex/queue/coordinator and the current writer holds the exclusive grant;
- `isolated-sandbox`  -  the selected vault copy is mounted or permissioned so no other process/agent can write it for the operation's lifetime.

Default `single_writer_mode: none` means the exception is unavailable.

A user statement that they normally run one agent, or the absence of another visible process, is insufficient by itself. If the configured mechanism cannot be verified at runtime, use normal protected locking.

That file is **conditional context**. Do not load it during ordinary read-only tasks.

Load it only when:

- the current task will write to the vault;
- a structural operation is required;
- a conflict or interrupted transaction must be recovered.

This prevents the concurrency protocol itself from consuming normal task context.

---

# 24. Write-Safety Profiles

## 24.1 `standard`

Use for ordinary projects where an occasional lost concurrent edit would be inconvenient but not materially damaging and concurrent same-file writes are uncommon.

Standard mode requires only:

1. read the target immediately before editing;
2. merge the intended change into the newest content;
3. write through a temporary file when supported;
4. atomically replace the target when the filesystem exposes a reliable replace operation;
5. verify the resulting file;
6. if the environment exposes a cheap version, ETag, generation number, compare-and-swap, or equivalent change detector, use it.

Standard mode does **not** require:

- lock files;
- leases;
- heartbeats;
- SHA-256 transaction journals;
- multi-file recovery journals.

This keeps routine vault updates inexpensive.

Standard mode reduces partial-write risk and stale-edit risk but does **not** claim complete protection from two truly simultaneous writers.

## 24.2 `protected`

Use when silent loss, truncation, or conflicting writes would be materially harmful.

Protected mode uses:

1. per-file atomic locks;
2. re-read after lock acquisition;
3. canonical multi-file lock ordering;
4. staged writes;
5. atomic replacement;
6. ownership verification;
7. stale-lock recovery;
8. semantic-conflict preservation;
9. transaction journals only when a logical operation requires coordinated multi-file integrity.

## 24.3 Choosing the profile

Default a newly created project to `standard`.

Use `protected` when one or more of these are true:

- the project is expected to have multiple agents writing concurrently;
- losing one committed change could materially damage legal, evidentiary, financial, safety, production, or other high-value records;
- changes are difficult or expensive to reconstruct;
- authoritative decisions must not be silently overwritten;
- the user explicitly requires strong multi-writer protection;
- repeated concurrent-write collisions have already occurred.

Do not use `protected` merely because the capability exists.

The objective is proportional protection, not maximum machinery.

## 24.4 Automatic operation-level escalation

Even in a `standard` project, temporarily use the protected protocol for structural operations where an interrupted or conflicting update could break vault navigation or integrity:

- project creation, rename, move, or removal;
- rule/context file rename, move, or removal;
- daily-note rollover and archival;
- link-mode migration;
- any operation that changes the same routing relationship across multiple indexes;
- repair of an interrupted protected transaction.

This escalation is operation-specific. It does not permanently change the project's profile.

When the verified single-writer environment exception applies, structural escalation still requires crash-consistency handling but may omit directory locks and multi-writer lock ordering.

---



## 24.5 Write-Safety Token and Tool Budget

Concurrency protection is proportional to expected loss avoided:

```text
read-only task
-> no locks or journals

standard write
-> fresh read + staged/atomic replacement + verification

protected single-file write
-> lock + fresh read + staged/atomic replacement + verification

protected structural/multi-file transaction
-> sorted locks + staging + journal only when needed + verification
```

Prefer the lowest tier that keeps the expected consequence of lost modification acceptable. Do not downgrade merely to save tokens when silent loss would be materially harmful.

---

# 25. Always-Protected Vault-System Files

The following shared control files use protected writes whenever they are modified, regardless of project profile:

```text
<VAULT_ROOT>/index.md
<VAULT_ROOT>/.vault-system/config.md
<VAULT_ROOT>/.vault-system/write-safety.md
<VAULT_ROOT>/.vault-system/content-integrity.json
```

These files are written infrequently but affect vault-wide routing or behavior, so the small additional write cost is justified.

Other files inherit their project's profile unless an operation-level escalation applies.

---



## 25.1 Protected Authoritative Content Integrity

Protected authoritative content is the runtime material whose silent alteration could change what agents load or how they are required to behave.

Track in `.vault-system/content-integrity.json`:

- root `index.md`;
- global routing indexes and `global/operating-rules.md`;
- project routers used to select runtime behavior;
- rule/context indexes that determine applicable mandatory context;
- every file whose load policy is `always`;
- any additional rule/context file explicitly marked `integrity: protected`.

Do not track daily notes or ordinary conditional/historical context by default solely to maximize coverage; they remain lower-authority data and would create unnecessary manifest churn. A project may explicitly protect high-impact conditional context.

Mark conditional context `integrity: protected` when it is difficult to re-verify and materially affects security, legal/compliance obligations, production deployment, financial decisions, identity/access behavior, safety, or another high-impact decision boundary. Easily re-verifiable source-derived context should normally remain untracked and be rechecked from its primary source instead.

Schema:

```json
{
  "schema_version": 1,
  "vault_id": "<vault UUID>",
  "revision": 1,
  "updated_at": "<UTC> (<local>)",
  "files": [
    {
      "file_id": "<immutable file UUID>",
      "path": "global/operating-rules.md",
      "sha256": "<SHA-256>",
      "revision": 1,
      "change_origin": "installation | user-direct | agent-authorized | migration",
      "changed_at": "<UTC> (<local>)"
    }
  ]
}
```

`change_origin` is provenance metadata, not user surveillance. Do not record prompts, reasoning, or unrelated user data.

The manifest intentionally does not hash itself. Its canonical hash is anchored externally in `vault-registry.json` as `protected_content_manifest_sha256`.

### Canonical protected-content hashing

For managed Markdown/JSON/text integrity hashes:

1. read as text when the file format is defined as text by this specification;
2. remove UTF-8 BOM if present;
3. normalize CRLF/lone CR to LF;
4. encode UTF-8 without BOM;
5. preserve all other bytes/text exactly;
6. calculate SHA-256.

This avoids false mismatches caused only by line-ending conventions while still detecting content/formatting changes. Binary files are not protected-authority files under this default scheme unless a project defines an exact-byte hashing rule.

### Initial manifest creation

After a new vault's runtime routing/rule files are finalized, the installer creates the initial manifest, hashes every required protected file, anchors the manifest hash in the registry, then runs the Runtime Validation Specification before handoff.

## 25.2 Protected Content Load Check

Before obeying a protected authoritative file:

1. verify the manifest hash against the registry anchor once per selected runtime state/session or after a protected-content change;
2. locate the file by `file_id`/path in the manifest;
3. hash the file contents being loaded;
4. require an exact manifest match;
5. if missing/mismatched, treat the protected file as unverified and run targeted integrity validation instead of silently obeying changed instructions.

Full validation verifies every manifest entry. Ordinary tasks verify only protected files actually loaded.

## 25.3 Protected Content Change Gate

A legitimate protected-content update is one protected logical operation:

Because the operation spans the vault and external registry, create a recovery journal whenever a protected-content change would modify the externally anchored manifest state. The journal must preserve before/staged hashes or recoverable copies sufficient to complete or restore the last verified state if the registry update fails.

1. confirm authorization under Section 1.7;
2. acquire required write protection;
3. update the authoritative file;
4. increment that entry's revision;
5. update hash, timestamp, and `change_origin`;
6. increment manifest revision;
7. canonical-hash the completed manifest;
8. update registry `protected_content_manifest_sha256`;
9. verify file -> manifest -> registry consistency;
10. only then treat the new protected content as authoritative.

If any stage fails, the new content is not authoritative. Use the recovery journal to either complete the full file -> manifest -> registry operation or restore the last verified file/manifest state when safe; otherwise block protected-rule use and surface the integrity failure.

---

# 26. Protected Lock Directory

Protected mode uses:

```text
<VAULT_ROOT>/.vault-system/locks/
```

Each target file has a lock identified by a hash of its canonical vault-relative path.

Conceptually:

```text
.vault-system/locks/<SHA256-OF-CANONICAL-PATH>.lock/
    owner.json
```

`owner.json` contains:

```json
{
  "writer_id": "<unique UUID>",
  "target": "projects/example/context/current-state.md",
  "acquired": "<UTC timestamp> (<local timestamp>)",
  "heartbeat": "<UTC timestamp> (<local timestamp>)",
  "lease_seconds": "<effective stale_lock_threshold_seconds>"
}
```

A `writer_id` must be unique for the writing agent or transaction.

---

# 27. Protected Atomic Lock Acquisition

The preferred operation is an atomic "create if absent" directory or file operation, an exclusive-create operation, or a host-provided mutex with equivalent behavior.

The required property is:

> exactly one competing writer succeeds when the lock does not exist.

Do not implement a lock as a separate "check, then create" sequence because that has a race condition.

Acquire locks only immediately before final read/merge/write work.

Do not hold locks while researching, reasoning, asking the user questions, generating long drafts, or running unrelated tests.

---

# 28. Protected Single-File Write Protocol

For a protected single-file modification:

1. compute the canonical vault-relative path;
2. derive the lock identifier;
3. atomically acquire the lock;
4. write lock ownership metadata;
5. re-read the target **after** lock acquisition;
6. merge the intended change into the newest content;
7. stage the complete replacement in the same filesystem;
8. verify the writer still owns the lock;
9. atomically replace the target when supported;
10. re-read and verify the destination;
11. release the lock only if ownership still matches.

Do not hash or journal an ordinary protected single-file write unless another requirement needs it.

This keeps protected single-file edits lighter than the earlier full transaction design.

---

# 29. Protected Multi-File Write Protocol

When protected mode or operation-level escalation affects multiple files:

## 29.1 Canonical lock order

1. determine all targets before locking;
2. normalize vault-relative paths;
3. sort paths lexicographically;
4. acquire locks in that order.

Every agent uses the same ordering rule.

## 29.2 Busy target

If one required lock is busy:

1. release every lock already acquired for this operation;
2. use short randomized backoff;
3. retry the full sorted set.

Recommended progression:

```text
250 ms
500 ms
1 s
2 s
2 s maximum with jitter
```

Do not hold a partial lock set while waiting.

## 29.3 Re-read

After all locks are owned, re-read every target and merge against current content.

Never commit content generated only from a pre-lock read.

---

# 30. Transaction Journals Are Conditional

Use:

```text
<VAULT_ROOT>/.vault-system/transactions/
```

A protected operation does **not** automatically require a transaction journal merely because two files are touched.

Create a journal only when partial completion would create a materially inconsistent state that is difficult to repair automatically.

Typical journal-worthy operations:

- moving/renaming a file while updating multiple inbound links and indexes;
- daily rollover that simultaneously promotes information, archives the old note, creates the new note, and changes the current pointer;
- project creation/removal affecting vault-wide routing;
- link-mode migration;
- a protected change that updates several authoritative files which must agree as one logical unit.

Ordinary updates such as "update context file, then append a daily outcome" normally do not require a journal unless the project's risk justifies it.

A journal records only enough recovery data to identify transaction state:

```json
{
  "transaction_id": "<UUID>",
  "writer_id": "<UUID>",
  "state": "prepared",
  "created": "<UTC timestamp> (<local timestamp>)",
  "targets": [
    {
      "path": "<vault-relative path>",
      "before_sha256": "<hash or MISSING>",
      "staged_sha256": "<hash>"
    }
  ]
}
```

The journal is recovery infrastructure, not task context.

---

# 31. Stale and Abandoned Protected Lock Recovery

A crashed, rate-limited, disconnected, or manually terminated agent must not permanently block protected writes.

Policy defaults are stored in `.vault-system/config.md`:

```text
stale_lock_threshold_seconds: 900
lock_heartbeat_seconds: 30
```

A protected project may define an explicit stale-lock threshold override in its project router; otherwise it inherits the vault default. The numeric values are policy defaults, not universal facts.

Lock timestamps use the Section 3 UTC-first time standard.

## 31.1 Normal stale-lock test

When a protected writer encounters an existing lock:

1. read `owner.json`;
2. compare the latest valid heartbeat with current UTC time;
3. if the heartbeat is newer than the abandonment threshold, treat the lock as active and do not disturb it;
4. if the heartbeat age is at least the effective configured `stale_lock_threshold_seconds`, treat the lock as an **abandoned-lock candidate**;
5. when the environment exposes reliable process/session ownership information, use it as supporting evidence;
6. absence of a visible process is **not**, by itself, proof that no remote, containerized, IDE-hosted, or otherwise hidden agent exists.

## 31.2 Safe abandoned-lock takeover

For an abandoned-lock candidate:

1. re-read the heartbeat after a short delay and confirm it did not advance;
2. if available, verify that the recorded local writer/process is no longer active;
3. atomically rename the lock directory to a unique quarantine path;
4. if the rename fails, assume another contender acted first and retry normal acquisition;
5. acquire a fresh lock normally;
6. immediately before commit, verify the fresh lock still belongs to the current `writer_id`.

Never delete the existing lock first and then attempt acquisition. Quarantine rename preserves race safety.

Example:

```text
.vault-system/locks/stale/<lock-id>.<new-writer-id>.lock
```

## 31.3 Stale writer transaction registration and recovery

After quarantining an abandoned lock candidate and before beginning a new protected write on related targets:

1. read the stale lock's `writer_id`;
2. search `.vault-system/transactions/` only for journals owned by that `writer_id`;
3. inspect each matching journal that is relevant to the targets being acquired;
4. if a journal is already `committed`, verify its destinations and remove the stale journal when safe;
5. if a journal is `prepared`, invoke the interrupted-transaction recovery procedure before starting the new write;
6. complete the prior transaction only when recorded hashes, staged data, and current targets prove that recovery is safe;
7. if the prior transaction cannot be completed confidently, preserve current files and move or recreate the unresolved record under `.vault-system/conflicts/`;
8. do not begin a new protected operation on any target affected by an unresolved prepared transaction from another writer.

Transaction ownership invariant:

> A writer may not begin a new protected operation on targets affected by an unresolved prepared transaction from another writer.

Do not automatically classify every stale writer journal as a conflict. Recover valid transactions when the evidence is sufficient.

## 31.4 Cleanup and logging

After recovery completes:

- remove a quarantined orphan only after confirming no prepared transaction or recovery artifact still depends on it;
- routine stale-lock cleanup should remain silent and should not create permanent project history;
- record the event in the active daily note only when the abandoned lock caused a material task interruption, exposed an incomplete transaction, created a conflict, or required non-routine recovery.

This prevents abandoned locks from permanently blocking the vault without turning infrastructure housekeeping into durable project noise.


---

# 32. Semantic Conflicts

Filesystem locking prevents lost bytes; it cannot decide which of two incompatible meanings should become authoritative.

## 32.1 Conflict Record

When a material semantic conflict cannot be resolved immediately through the Section 1.2 authority order, preserve the currently verified authoritative state and create:

```text
<VAULT_ROOT>/.vault-system/conflicts/<UTC-COMPACT>-<UUID>.md
```

Minimum record:

```markdown
---
conflict_id: "<UUID>"
status: "unresolved"
target_file_id: "<file UUID>"
target_path: "<vault-relative path>"
current_sha256: "<verified current hash>"
created: "<UTC> (<local>)"
---

# Semantic Conflict

## Current authoritative position
<concise statement or authoritative concept IDs>

## Competing proposed position
<concise statement or proposed concept IDs>

## Evidence pointers
- <primary/source references only>

## Resolution required from
<user | project owner | repository review | authoritative source>
```

Do not store prompts, chain-of-thought, raw conversations, or unrelated user information.

## 32.2 Resolution Procedure

1. keep the last verified authoritative content active while the conflict is unresolved, unless a higher-priority current user instruction already resolves the issue;
2. do not merge incompatible meanings into one live rule/context item;
3. if the current task depends materially on the disputed meaning, stop that dependent portion and surface the conflict;
4. if unaffected work can safely continue, continue without treating either proposed position as settled;
5. seek resolution from the highest available authority under Section 1.2;
6. after resolution, update the authoritative file through normal protected/integrity procedures;
7. if stable concept meaning changes materially, retire/supersede the old RULE-/CTX- ID instead of reusing it;
8. mark the conflict record `resolved`, record only the resolution reference/IDs and timestamp, then retain or archive according to configured conflict retention;
9. record the conflict in the active daily note only when it materially blocked or changed project work.

## 32.3 Git Semantic Conflicts

For repository `.agents` conflicts:

- do not complete a merge while incompatible authoritative meanings remain unresolved;
- preserve both branch versions during review;
- use repository review/owner authority plus Section 1.2 evidence order;
- after resolution, regenerate/verify `.agents/content-integrity.json` and complete the normal Git review/publish path.

Git text-merge success never proves semantic agreement.

---

# 33. Recovery and Unsupported Environments

Before a protected write, inspect only stale `prepared` transaction journals relevant to the targets being changed.

Do not scan every historical transaction during ordinary reads.

If the environment cannot provide the atomic primitives required for protected mode:

1. use an equivalent host-provided lock/transaction mechanism if available;
2. otherwise serialize protected writes through a single vault writer;
3. if neither is possible, do not claim the write is multi-writer safe.

For a `standard` project, continue using the lightweight immediate re-read plus atomic-replace behavior the environment supports.

---