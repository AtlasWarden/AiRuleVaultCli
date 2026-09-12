# Project Management

Load whenever a normal agent must create project infrastructure, resolve a project location, repair project identity/storage, or manage Git-backed Rule Vault state.

This file is the installed answer to "create missing project infrastructure." Ordinary agents must not consult the Creation Guide for that procedure.

# 22. Automatic New-Project Creation

If an agent is working on a durable task that clearly belongs to a project and no vault folder exists for that project, the agent is responsible for creating the project structure.

Do not wait for a separate instruction merely to create the vault scaffold.

## 22.1 Project creation test

Create a project folder when the work represents a coherent body of activity likely to require future continuation, rules, state, or history.

Do not create a permanent project folder for a trivial one-off question with no durable continuation value.

## 22.2 Minimum new-project scaffold

Create the private vault scaffold:

```text
projects/<project-slug>/
|-- project.json
|-- index.md
|-- private-context/
|   |-- index.md
|-- daily/
    |-- index.md
    |-- active/
    |   |-- YYYY-MM-DD.md
    |-- archive/
        |-- index.md
```

`projects/<project-slug>/project.json` is the local private project pointer/router. It may contain machine-specific information such as the current checkout path because it never belongs in the project repository.

Minimum local pointer:

```json
{
  "schema_version": 1,
  "project_id": "<immutable UUID>",
  "name": "<project name>",
  "storage_mode": "local-private",
  "git_state": "none",
  "last_known_path": "<cached local project path>",
  "repo_identity": null
}
```

Do not commit this local pointer to the project repository.

Create additional rule/context files only when real content requires them.

When the actual project working directory is available, also create the portable repository identity marker:

```text
<PROJECT_ROOT>/
|-- .agents/
    |-- project.json
```

Minimum repository marker:

```json
{
  "schema_version": 1,
  "project_id": "<immutable UUID>",
  "name": "<project name>"
}
```

The same `project_id` must appear in both:

```text
<VAULT_ROOT>/projects/<project-slug>/project.json
<PROJECT_ROOT>/.agents/project.json
```

The files have different purposes.

- Local `projects/<project-slug>/project.json` is the private machine-local pointer/router. It may contain `last_known_path`, `repo_identity`, `storage_mode`, and `git_state`.
- Repository `.agents/project.json` is a portable identity marker only.

Repository `.agents/project.json` must never contain `vault_root`, a private-vault path, `last_known_path`, a user home path, a checkout path, drive-specific routing, or other machine/user-specific filesystem locations.

`.agents/project.json` is safe and expected to be committed with `.agents/` because it contains portable identity only. It is not a pointer back to the local vault and is not proof that the project is already `git-shared`.

## 22.3 New-project creation sequence

1. derive and sanitize a stable lowercase `kebab-case` project slug using Section 22.4;
2. check for an existing equivalent project before creating a duplicate;
3. generate one immutable `project_id` UUID and use it in both `<VAULT_ROOT>/projects/<project-slug>/project.json` and `<PROJECT_ROOT>/.agents/project.json` when the project root is available;
4. treat filesystem paths only as cached locations, never as project identity;
5. identify the project's primary purpose and end goal or success condition from verified user instructions or primary evidence;
6. if the primary purpose or end goal is unknown, do **not** invent it: create the scaffold with the unknown field explicitly marked, then ask the user for the missing goal information and explain that it will be used as the decision-alignment reference for judging whether future choices move the project closer to or farther from its intended outcome;
7. choose `Write Safety: standard` by default; use `protected` only when the profile criteria clearly apply or the user requires it;
8. use protected structural-write handling for the project-creation operation because root routing is changing;
9. create the private project scaffold including its local `project.json` pointer, and create the portable repository `.agents/project.json` identity marker when the project root is available;
10. populate the rule and context indexes;
11. populate `current-state.md`, including the verified primary purpose and end goal/success condition or the explicit pending-user-input marker;
12. create today's active daily note;
13. update root `index.md`;
14. validate links and verify the local vault `project.json` `project_id` matches `.agents/project.json`, while also verifying the repository marker contains no machine-specific or private-vault path fields;
15. commit the structural change set using the protected protocol.

---



## 22.4 Project and Category Slug Sanitization

Never place an unsanitized project/category name directly into a filesystem path.

Portable slug grammar:

```text
^[a-z0-9]+(?:-[a-z0-9]+)*$
```

Procedure:

1. lowercase the source name;
2. transliterate or remove unsupported characters where practical;
3. replace runs of spaces/underscores with one hyphen;
4. remove all path separators, drive/volume separators, control characters, and characters outside lowercase ASCII letters, digits, and hyphens;
5. reject `.` and `..` exactly;
6. reject absolute paths and any value containing traversal segments;
7. on Windows, reject reserved device basenames such as `con`, `prn`, `aux`, `nul`, `com1`-`com9`, and `lpt1`-`lpt9`;
8. trim leading/trailing hyphens;
9. if the result is empty or collides with another project, derive a safe disambiguated slug rather than using raw input;
10. validate the final slug against the grammar before constructing a path.

Use the same sanitization rule for newly created rule/context category directory names.

A human-readable project `name` remains separate from the sanitized filesystem slug.

---

---

# 45. Project Identity and Location Recovery

Project identity must be independent of filesystem location.

Every durable project has one immutable `project_id` shared between two deliberately different records:

```text
<VAULT_ROOT>/projects/<project-slug>/project.json
    private local pointer/router

<PROJECT_ROOT>/.agents/project.json
    portable repository identity marker
```

Only the immutable project identity is shared. Machine-specific routing information stays in the local vault pointer.

Moving, copying, cloning, or mounting the project at a different path must not change the logical project's identity.

## 45.1 Local private `project.json` pointer

The private vault stores:

```text
<VAULT_ROOT>/projects/<project-slug>/project.json
```

This is the local project pointer/router. It is not repository content.

For a local-private project:

```json
{
  "schema_version": 1,
  "project_id": "<immutable UUID>",
  "name": "<project name>",
  "storage_mode": "local-private",
  "git_state": "none",
  "last_known_path": "<cached local path>",
  "repo_identity": null
}
```

For a Git-backed project:

```json
{
  "schema_version": 1,
  "project_id": "<same immutable UUID>",
  "name": "<project name>",
  "storage_mode": "git-backed",
  "git_state": "shared",
  "last_known_path": "<cached current/most-recent checkout>",
  "repo_identity": "<verified repository identity>"
}
```

`last_known_path` is only a local cache. This file may contain absolute local paths because it stays inside the user's private vault.

Never copy, publish, or commit this local pointer into the project repository.

Never use an absolute path as proof of project identity.

## 45.2 Portable repository project marker

The actual project directory carries a separate portable marker:

```text
<PROJECT_ROOT>/.agents/project.json
```

Minimum content:

```json
{
  "schema_version": 1,
  "project_id": "<immutable UUID>",
  "name": "<project name>"
}
```

This repository marker is identity-only.

It must not contain:

- `vault_root`;
- a path to the user's Rule Vault;
- `last_known_path`;
- the user's home-directory path;
- a checkout path;
- drive letters or machine-specific mount paths used for routing;
- a pointer to another user's private vault;
- other machine/user-specific location metadata.

The portable marker is created before Git is required so project identity moves with the project folder.

When Git is initialized, the same identity marker may be committed as part of `.agents/`. Committing it is expected once `.agents/` is intentionally placed under version control.

The local vault pointer and repository marker must share the same `project_id`, but they are not interchangeable files.

## 45.3 Location resolution ladder

If the cached project path is missing, inaccessible, or no longer contains the expected `project_id`, do not immediately ask the user and do not manually crawl file-by-file.

Resolve location in this order:

1. inspect the current workspace or working directory for `.agents/project.json`;
2. check the cached `last_known_path`;
3. check other recently verified working-copy paths for the same `project_id`, if maintained;
4. use native operating-system or command-line filesystem query capabilities to locate `.agents/project.json` under configured or likely project roots;
5. inspect only the returned candidates and compare their exact `project_id`;
6. for Git-backed candidates, use repository identity, remotes, worktree information, and current workspace state to disambiguate;
7. broaden the native search scope only when narrower roots fail;
8. ask the user where the project is only when no trustworthy unique working copy can be resolved.

The search operation should find candidate marker paths first; the agent should not recursively read project contents merely to locate the project.

Examples of suitable mechanisms include native shell/file-search commands, indexed filesystem search, or IDE workspace search. The guide defines behavior, not one platform-specific command.

## 45.4 Multiple valid working copies

A Git-backed project may legitimately have several locations:

```text
C:/work/project
D:/test/project
C:/worktrees/project-feature
```

These may share the same `project_id`.

Treat them as working copies of the same logical project rather than duplicate projects when repository identity confirms the relationship.

Resolution rules:

1. if the task is already running inside one matching working copy, use that working copy;
2. otherwise prefer a working copy explicitly selected by the user or task;
3. use Git repository/worktree identity to distinguish legitimate clones from accidental duplicate folders;
4. if several valid candidates remain and the correct checkout materially affects the task, ask the user rather than guessing.

Do not overwrite one cached path merely because another valid clone exists. The router may retain a most-recent path or a small recent-locations list if that improves recovery.

## 45.5 Missing Git-backed working copy

If a project is known to be `git-backed` and no local working copy can be resolved:

1. do not create a new project identity;
2. preserve the existing `project_id` and repository identity;
3. ask the user where the checkout was moved when location cannot be resolved;
4. if cloning/restoring the repository is explicitly authorized and the environment supports it, restore the existing project rather than creating a replacement project;
5. never claim the shared `.agents/` context was loaded from the repository when no matching checkout or verified repository content was actually accessed.

---

---

# 46. Git-Backed Project Mode

Git-backed project mode allows team-shareable project rules and context to live with the source repository without copying the entire private vault into every repository.

The architecture is:

```text
PERSONAL VAULT
|-- projects/<project>/
    |-- project.json          <- local pointer/router; may contain local checkout path
    |-- index.md
    |-- private-context/
    |-- daily/

PROJECT REPOSITORY
|-- .agents/
    |-- project.json          <- portable identity only; no local/vault paths
    |-- content-integrity.json
    |-- index.md
    |-- rules/
    |-- context/
```

For Git-backed work, team-shareable project rules and context belong in the project's `.agents/` directory. The local vault keeps the local pointer/router, private/user-specific context, personal daily history, and concise pointers to repository authority after promotion completes.

For a verified shared Git-backed project:

- repository `.agents/rules/` is authoritative for team-shareable project rules;
- repository `.agents/context/` is authoritative for team-shareable durable project context;
- the private vault remains authoritative for private/user-specific context and personal daily history;
- shared information must not remain as a second authoritative copy in the private vault after promotion is safely completed.

## 46.1 What belongs in repository `.agents/`

Store only information appropriate for all contributors to that project, such as:

- project purpose and shared success condition;
- architecture and implementation rules;
- testing requirements;
- deployment rules;
- shared security/privacy constraints;
- durable design rationale;
- non-obvious shared project context;
- durable team decisions;
- current shared project state when useful to contributors.

Do not commit private/user-specific information merely because it relates to the project.

Ordinary personal daily notes remain in the private vault by default.

The repository `.agents/project.json` is not the local vault pointer. Never place `vault_root`, `last_known_path`, private-vault paths, or other machine-specific routing information in it.

The local pointer belongs at:

```text
<VAULT_ROOT>/projects/<project-slug>/project.json
```


## 46.2 Git does not automatically mean durable or shared

The presence of `.git/` does **not** transfer authority from the private vault.

Use these lifecycle states:

```text
local-private
-> git-initialized
-> git-committed-local
-> git-shared
```

### `local-private`

The private vault is authoritative for project rules/context.

`.agents/project.json` may already exist solely for identity and location recovery.

### `git-initialized`

A Git repository exists, but shared authority does not change.

The agent may prepare repository `.agents/rules/` and `.agents/context/`, but these are candidate copies.

Before verified publication, the same team-shareable information may temporarily exist in both places. That duplication is deliberate rollback protection, not the final storage model.


The private vault remains authoritative because:

- nothing may yet be committed;
- `.git/` may be deleted;
- Git may be reinitialized;
- the repository may never be published;
- no teammate can yet rely on the candidate state.

### `git-committed-local`

The candidate `.agents/` content exists in a local commit.

This provides local version history but is not sufficient to remove the private authoritative copies.

A local `.git/` directory and its history can still be deleted or lost before publication.

### `git-shared`

Only enter this state after verifying all of the following:

1. intended team-shareable `.agents` files exist;
2. the files are tracked by Git;
3. the files are included in a commit;
4. that exact commit or a descendant containing the same `.agents` state has been successfully pushed/published to the intended persistent remote;
5. the intended remote branch can be verified to contain that state;
6. repository identity matches the project router's `project_id`/repository metadata.

Only after these checks may the repository become the authoritative home for the promoted shared rules/context.

Core rule:

> **Git existence does not transfer authority. Verified durable publication does.**

The intended final storage split is:

```text
LOCAL VAULT
-> project.json pointer/router
-> private/user-specific context
-> personal daily history
-> pointers to repository authority

PROJECT .agents/
-> portable project.json identity marker
-> team-shareable rules
-> team-shareable context/current state
-> integrity/index files
```

Do not reverse these roles.


## 46.3 Local-to-Git promotion

When a local-private project later adopts Git, treat it as promotion of the existing project, not creation of a new project.

Procedure:

1. verify repository `.agents/project.json` matches the local vault `projects/<project-slug>/project.json` `project_id`, and verify the repository marker contains no local/private-vault path fields;
2. set `git_state: initialized`;
3. review existing private project rules/context;
4. classify each durable item as:
   - team-shareable;
   - private/user-specific;
   - personal daily/history;
5. run the Section 46.14 Shared-Content Privacy Preflight on every team-shareable candidate;
6. copy only candidates that pass preflight into repository `.agents/rules/` and `.agents/context/`;
7. build repository `.agents/index.md`, rule indexes, context indexes, and shared `current-state.md` using the same modular/load-scope rules as the vault;
8. keep the original private authoritative copies intact while publication is pending;
9. after a local commit containing the candidate `.agents` state is verified, set `git_state: committed-local`;
10. do **not** remove the private authoritative copies yet;
11. after verified publication to the intended persistent remote, set:
    `storage_mode: git-backed`
    and
    `git_state: shared`;
12. verify the remotely published `.agents` state;
13. only then remove migrated duplicate team-shareable rule/context copies from the private vault, leaving the local `projects/<project-slug>/project.json` pointer, private context, daily history, and concise pointers to repository authority;
14. preserve private context and daily history in the personal vault;
15. update routing indexes and validate that future agents can resolve both sources.

Promotion is a protected structural operation because authority is moving between storage systems.

## 46.4 Pending promotion is not authority

While `git_state` is `initialized` or `committed-local`:

- repository `.agents` content is pending/candidate shared state;
- the private vault remains authoritative for the material being promoted;
- an agent must not infer that candidate repository files outrank the private originals;
- do not delete or truncate the private originals merely because equivalent files exist under `.agents/`.

If the user deletes `.git/`, abandons the repository, or never pushes it, the private authority survives.

## 46.5 No-remote repositories

If the project intentionally has no persistent remote:

- it may remain `git-committed-local`;
- Git may still provide useful local versioning;
- the private vault remains the durable authority for shareable rules/context unless another verified durable shared publication mechanism is explicitly designated.

Do not equate a local Git commit with team distribution or durable backup.

## 46.6 Shared rules are branch-aware

Once `git-shared`, repository `.agents/` follows the checked-out Git branch.

The current branch's `.agents` state should match the code state on that branch.

Rules/context changes required by a code change should travel through the normal Git review/merge workflow with the code they govern.

Do not automatically import a different branch's `.agents` state merely because it is newer.

If the current branch predates the shared `.agents` structure or lacks required shared rules:

1. verify repository identity and branch state;
2. do not silently copy rules from another branch;
3. treat the branch as lacking authoritative branch-local shared context;
4. resolve through the project's normal merge/rebase/update workflow or ask the user when the correct action is material.

## 46.7 Merge conflicts in `.agents/`

Do not use blind union merging for rule/context conflicts.

If two branches make semantically incompatible changes:

1. preserve both competing changes during conflict analysis;
2. compare them against current user direction, project purpose, applicable primary evidence, and code state;
3. explicitly resolve which rule/context remains authoritative;
4. validate indexes and links after resolution;
5. do not allow syntactically valid Markdown to conceal contradictory rules.

Git text-merge success is not proof of semantic correctness.

## 46.8 Shared versus private daily information

Ordinary daily notes remain in the private vault.

During daily archival or durable promotion for a Git-backed project:

```text
team-shareable rule/context
-> repository .agents/

private/user-specific context
-> private vault

personal chronological history
-> private vault daily/archive/
```

Do not commit personal daily narration to the repository merely to preserve history.

If a team later requires shared handoffs, define that as a separate explicit repository feature rather than making all personal daily notes shared by default.

## 46.9 Uncommitted and unpublished `.agents` changes

After a project is already `git-shared`, local changes to repository `.agents/` may be valid current working-tree changes, but they are not yet verified team-shared durable state.

Agents must distinguish:

```text
working-tree state
local committed state
verified remote/shared state
```

Do not claim a new rule/context change is available to teammates until its containing commit is verified on the intended remote.

If a task modifies `.agents` but publication is not authorized or completed, record the pending publication state concisely in the private active daily note without duplicating the full shared file contents.

## 46.10 Repository deletion or relocation after sharing

If the local `.git/` directory or checkout disappears after `git-shared`:

- do not silently revert the project to a new identity;
- use Section 45 location recovery;
- preserve the known repository identity;
- if the remote remains available, it is the recovery source for shared `.agents` history;
- if neither a working copy nor remote can be accessed, state that shared project context could not be loaded and ask the user for the project's location or recovery direction.

## 46.11 Repository `.agents` write safety

Do not duplicate the private vault's entire lock infrastructure inside every Git repository merely because Git-backed mode is enabled.

For repository `.agents` files:

- use immediate fresh reads before writes;
- preserve unrelated working-tree changes;
- use atomic replacement when available;
- respect the repository's existing Git workflow;
- use file-level locking only when actual concurrent local writers require it;
- rely on Git for version history, branching, diffs, and merge detection;
- do not treat Git as a substitute for semantic conflict validation.

## 46.12 Team onboarding behavior

A teammate who clones only the repository should be able to begin from:

```text
<PROJECT_ROOT>/.agents/index.md
```

and load the same team-shareable rules/context as other contributors.

The teammate does not need access to another user's private vault.

A user's private vault may add private context and daily continuity on top of the repository layer, but must never be required for understanding team-shareable project rules.

---



## 46.13 Shared Protected-Content Integrity

Each Git-backed repository `.agents/` directory must contain:

```text
.agents/content-integrity.json
```

Track the same classes of shared runtime-authoritative material described by Section 25.1: routing indexes, mandatory/`always` files, and explicitly protected context.

Before parsing, reading as instruction, watching, or otherwise trusting **any** shared `.agents` entry, first pass the repository-ingest gate in Section 46.16. Machine-significant metadata must then pass the deterministic parser profile in Section 46.17.

Before treating shared protected content as authoritative:

1. pass the Section 46.16 Git object/path containment checks without following repository-controlled indirection;
2. parse machine-significant metadata under Section 46.17 and reject ambiguous/duplicate-key input;
3. verify `.agents/content-integrity.json` is tracked in the current Git commit;
4. verify each protected file loaded matches the manifest hash;
5. inspect Git working-tree status for the manifest and protected files being loaded;
6. treat uncommitted modifications to protected shared files as **candidate changes**, not shared authority, unless the current user explicitly authorizes those candidate changes for the current task;
7. use the committed protected state on the current branch as baseline shared authority;
8. treat a change as team-shared only after the repository's normal review/merge/publish controls complete as required by Section 46.2.

For protected shared-rule changes, update the protected file and `.agents/content-integrity.json` in the same commit/change set.

Git history, branch protection, CODEOWNERS/reviewer policy, signed commits/releases when configured, and repository permissions are external authorization/provenance controls. This specification does not replace them.

Normal repository write permission is **not** permission to cause a different developer's agent to dereference repository-controlled paths into that developer's local filesystem. Section 46.16 is therefore a mandatory cross-user trust boundary even when the contributor legitimately holds repository write access.

## 46.14 Shared-Content Privacy Preflight

Before promoting private-vault information into repository `.agents/`, perform both semantic review and a lightweight deterministic privacy/secret preflight when permitted.

Minimum deterministic review signals include:

- private-key block markers;
- obvious password/secret/token/API-key assignments;
- bearer/authentication credential material;
- complete credentials or connection strings;
- content explicitly marked private/non-shareable;
- likely personal email addresses, phone numbers, personal filesystem paths, or internal hostnames as **review signals**, not automatic proof of sensitivity.

Behavior:

1. use an already-available repository/secret scanner when configured by the project, otherwise native text/pattern search is sufficient;
2. do not install a new scanner/runtime merely for preflight without separate authorization;
3. confirmed credentials/secrets block promotion until removed/rotated/resolved;
4. review-only signals require semantic classification before promotion;
5. a clean pattern scan does not prove content is safe - semantic private-vs-team classification remains mandatory;
6. if deterministic scanning is unavailable, perform manual semantic review and state that automated backstop was unavailable;
7. never copy private daily history wholesale into `.agents/`.

## 46.15 Shared Rule Review Authority

A shared protected `always` rule change becomes authoritative according to the repository's configured governance:

- if the repository requires pull-request/reviewer/CODEOWNERS approval, the change remains candidate until those controls succeed;
- if the repository has no review requirement, current user authorization plus repository write permissions may permit the change, but the lack of independent review is a known governance limitation;
- agents must not weaken branch protection, review requirements, or repository permissions merely to make a Rule Vault change easier to publish.

## 46.16 Git-Shared Repository Ingest Gate (RV-SEC-001)

A `git-shared` repository crosses a privilege boundary: one contributor can author `.agents` entries using repository permissions, while another developer's agent later processes those entries using the second developer's local filesystem permissions.

Therefore Git tracking, commit history, or ordinary contributor write access is never sufficient reason to follow filesystem indirection under `.agents/`.

Before any `.agents` entry is parsed, read as instruction/context, opened for writing, subscribed to for file events, or used as an integrity-manifest target, perform this fail-closed ingest gate:

1. derive the repository-relative path without dereferencing it;
2. reject path traversal, absolute paths, drive/volume escapes, and path components outside the canonical `.agents/` root;
3. inspect the Git index/tree entry type before opening the workspace target;
4. allow only expected regular-file/tree entries for managed `.agents` content;
5. reject Git symlink mode `120000` anywhere that would participate in a managed `.agents` path;
6. reject Gitlink/submodule mode `160000` inside managed `.agents` content;
7. inspect every existing filesystem path component with no-follow/lstat-equivalent semantics;
8. reject symbolic links, junctions, mount/reparse-point indirection, aliases, or platform-equivalent redirection in any managed `.agents` path component or final file;
9. require the final target to be a regular file when a file is expected and a real directory when a managed directory is expected;
10. verify the physical opened target remains beneath the canonical repository `.agents` root;
11. where the platform exposes no-follow/beneath-root open primitives, use them so validation and opening are one protected operation rather than a check-then-follow sequence;
12. if equivalent atomic no-follow primitives are unavailable, revalidate immediately after open and fail closed on any identity/type/containment change;
13. never allow a symlink merely because its current target appears to point back inside the repository;
14. repeat the gate after checkout, branch switch, merge, rebase, pull, reset, restore, or other operation that can replace `.agents` entries;
15. repeat the gate before rebinding an exact-file subscription after save-by-replace/rename;
16. repeat the gate before any agent write to a shared `.agents` target.

Failure behavior:

```text
INVALID_SHARED_AGENT_PATH
-> do not follow the target
-> do not parse or obey its contents
-> do not establish a file subscription
-> stop work that depends on the rejected entry
-> notify the user immediately
-> report the offending repository-relative path
-> report the exact failed gate condition
-> explain the correction required to make the entry eligible for safe ingest
-> wait until the entry is corrected, removed, or the user chooses another safe disposition
-> rerun the complete Section 46.16 gate before continuing dependent work
```

The notification must describe the problem without dereferencing or executing the rejected entry. When safe and useful, identify the expected repository-safe form, such as replacing a symlink with a regular tracked file inside `.agents/`.

Record the failure in the active project daily note. The record must contain only the minimum durable security handoff:

- timestamp;
- `RV-SEC-001`;
- offending repository-relative path;
- failed gate condition;
- status: `open | corrected | removed | accepted-external-resolution`;
- user-confirmed disposition when available;
- verification result after correction or removal.

Do not copy target contents, secrets, unrelated local paths, or speculative attacker intent into the daily note.

The failure remains open until the unsafe entry is corrected/removed or the user records another safe disposition. A user acknowledgment by itself does not make the rejected path safe and does not bypass the ingest gate.

Do not automatically delete or repair a suspicious repository entry. Preserve evidence and follow the repository's normal security/review workflow.

## 46.17 Deterministic Machine-Metadata Parser Profile (RV-SEC-007)

Shared `.agents` content is contributor-controlled input until it passes this parser boundary. Two agents or parser libraries must not be allowed to derive different authoritative meaning from the same bytes.

All machine-significant YAML/frontmatter and JSON used for Rule Vault routing, identity, integrity, policy, or conformance must use a deterministic fail-closed profile.

### YAML/frontmatter requirements

1. accept exactly one document;
2. require a mapping at the document root when frontmatter is expected;
3. require scalar string keys using exact case-sensitive ASCII field names;
4. reject duplicate keys at every mapping level;
5. reject anchors, aliases, merge keys (`<<`), custom tags, directives that change interpretation, and complex/non-scalar keys;
6. reject parser-specific extensions or implicit types outside the defined Rule Vault schema;
7. allow booleans only as lowercase `true` or `false`;
8. require timestamps and other schema-defined strings to be quoted strings when their syntax could be implicitly typed by a parser;
9. require integers, when a schema permits them, to use canonical base-10 form and reject octal/hex/sexagesimal or implementation-specific numeric forms;
10. reject unknown security/runtime-significant fields unless the applicable schema explicitly permits them;
11. keep extensions, when allowed, under an explicitly defined namespaced extension field that cannot alter core security/runtime semantics;
12. validate value types and allowed enumerations after parsing and before use.

### JSON requirements

1. decode UTF-8 without accepting duplicate object member names at any nesting level;
2. reject non-standard JSON extensions such as comments, trailing commas, NaN/Infinity, or implementation-specific values;
3. require exact case-sensitive field names and schema-defined types;
4. reject unknown security/runtime-significant fields unless the schema explicitly permits them;
5. validate the complete schema before using any value to determine authority, routing, identity, path, integrity, or execution behavior.

### Parser capability rule

If the available parser cannot detect and reject duplicate keys or cannot enforce this deterministic profile, it must not be used for authoritative Rule Vault metadata. Use another already-authorized deterministic parser or report that the shared metadata cannot be safely interpreted.

Never resolve ambiguity with "first key wins", "last key wins", permissive coercion, case folding, or parser-dependent fallback behavior.

Parser-profile validation occurs **before** protected-content hashes are trusted as semantic authority. A cryptographic hash can prove byte equality; it cannot make ambiguously parsed bytes safe.

---

---