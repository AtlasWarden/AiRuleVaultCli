# Daily Continuity

Load for daily note creation, rollover, archival, historical loading, and promotion of durable information.

# 14. Per-Project Daily Note System

Every active project has its own daily work stream.

Never combine unrelated projects into one global daily note.

Directory:

```text
daily/
|-- index.md
|-- active/
|   |-- YYYY-MM-DD.md
|-- archive/
    |-- index.md
    |-- YYYY/
        |-- index.md
        |-- Mon/
            |-- index.md
            |-- DD.md
```

There should normally be exactly one current active daily note per active project.

---

# 15. Daily `index.md`

The daily index tells an agent exactly which note is current.

Example:

```markdown
# <Project Name> Daily Notes

## Current Active Note

Policy: `always` for active work in this project.

[2026-08-18](./active/2026-08-18.md)

## Archive

[Archive Index](./archive/index.md)

## Loading Rule

Read the current active note for current handoff context.

Archived notes are not default context. Read them only when:
- the user asks for historical reconstruction;
- current authoritative context points to a historical gap;
- the current daily note references unresolved prior work;
- a task depends on a past event not already promoted into authoritative context.
```

The current pointer must always match the actual active file.

---

# 16. Active Daily Note Template

Use:

```markdown
---
file_id: "<immutable UUID>"
title: "<YYYY-MM-DD>  -  <Project Name>"
project: "<project-slug>"
kind: "daily"
status: "active"
aliases: []
hasInboundLinks: false
created: "<UTC timestamp> (<local timestamp>)"
updated: "<UTC timestamp> (<local timestamp>)"
---

# <YYYY-MM-DD>  -  <Project Name>

## Current Handoff

- Objective:
- Current state:
- Next action:
- Blockers:
- Relevant rules:
- Relevant context:

## Sessions

### <UTC timestamp> (<local timestamp>)  -  <task name>

**Outcome**
- <concise verified result>

**Durable changes**
- <authoritative files updated, or None>

**Decision basis**
- <stable RULE-/CTX- IDs that materially influenced the decision, or None>

**Governance findings**
- <untrusted-command alert classification/disposition or other material governance finding, or None>

**Open work**
- <only unresolved work that matters later>

**Files touched**
- <links>
```

Append sessions to the same active daily note for that local calendar date.

Do not create one daily note per chat or agent invocation.

---



Record `Decision basis` only for durable rules, constraints, assumptions, decisions, success criteria, or context concepts that materially influenced the outcome. Do not reproduce rule/context text when its stable ID is sufficient.

---

# 17. Daily Rollover and Archiving

Rollover occurs lazily when the project is first accessed after the user's local calendar date changes.

Do not pretend a background rollover occurred while no agent was running.

## 17.1 Rollover sequence

Before normal project work begins:

1. read `daily/index.md`;
2. determine the local date encoded by the active note;
3. compare it with the current local date;
4. if they match, continue normally;
5. if they differ, perform the archive-consolidation procedure below.

## 17.2 Archive consolidation is mandatory

The archiving agent must read the previous active note completely before moving it.

For every significant item in the note, classify it as follows.

### Rule candidate

Promote it into `rules/` when it defines required behavior, prohibited behavior, a procedure, a constraint, a durable operating decision, a source-priority rule, or a validation requirement.

If an appropriate rule file exists, update it. If that file is `always`, apply Section 17.3 before writing.

If none exists, create a new rule file from the standard Markdown template and update the appropriate category index using Section 17.4.

### Context candidate

Promote it into `context/` when it records durable information that materially helps future understanding, is not obvious from project structure, is not easy to rediscover from current primary sources, or explains rationale, external constraints, current state, important history, unresolved dependencies, or verified facts.

If an appropriate context file exists, update it. If that file is `always`, apply Section 17.3 before writing.

If none exists, create a new context file from the standard Markdown template and update `context/index.md` using Section 17.4.

### Current-state candidate

Update `context/current-state.md` when the previous note changes current implementation state, current objective, open work, blockers, active decisions, or next required actions.

### Daily-only information

Do not promote raw tool output, routine command logs, temporary debugging steps no longer relevant, facts easily recoverable from the current repository, duplicate information already authoritative elsewhere, conversational narration, or discarded ideas with no future value.

## 17.3 Always-file mutation and deduplication gate

Before writing, appending, or promoting any item into a file whose applicable policy is `always`, perform this gate.

### Step 1  -  Whole-file similarity scan

Read the complete `always` file and identify existing entries covering the same:

- operational boundary;
- rule logic;
- fact;
- constraint;
- decision;
- success criterion.

Do not inspect only the intended insertion point.

### Step 2  -  Mutate, replace, or append

Use exactly one of these actions:

- **Mutate in place**  -  when the new information clarifies or refines an existing concept.
- **Replace**  -  when the previous entry is superseded by a verified architectural, operational, or project-direction change.
- **Append**  -  only when the concept is genuinely unique and not already represented.

Do not preserve duplicate legacy bullets merely for history. Historical rationale belongs in conditional context or daily archives when it remains useful.

### Step 3  -  Enforce structural brevity

A newly written or materially rewritten `always` item should normally be no more than:

```text
2 sentences
or
30 words
```

Do not compress away required legal precision, safety boundaries, exceptions, or information necessary for correct execution.

If explanation, rationale, examples, or history require more space:

1. keep the minimum irreducible rule/fact in the `always` file;
2. move deeper detail into an appropriate conditional context file;
3. link to that file only when the deeper material may sometimes be needed.

### Step 4  -  Refactor redundancy

Whenever an `always` file is materially modified, perform a quick whole-file redundancy check and remove or consolidate superseded duplication while preserving current meaning.

This gate applies to **all** `always`-file mutations, not only daily rollover.

## 17.4 Automated index and table formatting

Whenever an agent creates or updates a routing index:

1. preserve the index's established section structure;
2. preserve existing Markdown table columns, column order, header separators, and pipe-based row form;
3. do not switch a table to bullets or bullets to a table merely while appending an entry;
4. write routing descriptions in present tense;
5. keep each routing description at 20 words or fewer;
6. sort entries alphabetically by canonical filename or relative path **within the same category, scope, and policy section**;
7. never alphabetically mix `always`, `conditional`, and `historical` groups merely to obtain one global sort;
8. preserve valid Markdown syntax;
9. do not spend tool calls aligning cosmetic whitespace between pipes when the table already renders correctly;
10. when generating a brand-new subject-category `index.md`:
   - rule indexes must use exactly `| File | Policy | Scope | Load when |`;
   - context indexes must use exactly `| File | Policy | Contains | Load when |`;
   - do not invent alternate header names such as `Trigger`, `Description`, or `Applies to` unless the schema is intentionally migrated vault-wide.

When archive consolidation creates a new rule/context file or discovers a new routable item, apply this formatting standard immediately to the affected index.

## 17.5 Rule versus context decision

Use this test:

> Does this information prescribe how future work must operate?

If yes, it belongs in a rule file.

Otherwise ask:

> Does this information materially help a future agent understand what is true, why it is true, or where the project currently stands, and is it not trivial to rediscover?

If yes, it belongs in context.

If information contains both:

- put the minimum normative requirement in a rule file;
- put necessary rationale or explanatory background in context;
- cross-link rather than duplicate the full content.

## 17.6 Archive move

Only after promotion is complete:

1. determine the note's local year;
2. determine the fixed month abbreviation;
3. determine the zero-padded day;
4. acquire all required file locks;
5. move `daily/active/YYYY-MM-DD.md` to `daily/archive/YYYY/Mon/DD.md`;
6. create missing archive directories;
7. update `daily/archive/YYYY/Mon/index.md`;
8. update `daily/archive/YYYY/index.md` if the month was newly introduced;
9. update `daily/archive/index.md` if the year was newly introduced;
10. create `daily/active/YYYY-MM-DD.md`, where `YYYY-MM-DD` exactly matches the current local calendar date;
11. update `daily/index.md` to the new active note;
12. verify links;
13. commit the multi-file transaction;
14. release locks.

If promotion or locking fails, do not silently discard or overwrite the old note.

---

# 18. Archive Indexing

## `daily/archive/index.md`

List years only:

```markdown
# Daily Archive

- [2026](./2026/index.md)
- [2027](./2027/index.md)
```

## `daily/archive/YYYY/index.md`

List months only:

```markdown
# 2026

- [Aug](./Aug/index.md)
- [Sep](./Sep/index.md)
```

## `daily/archive/YYYY/Mon/index.md`

List daily notes:

```markdown
# Aug 2026

- [18](./18.md)  -  Implemented validation changes; deployment remained open.
- [19](./19.md)  -  Resolved deployment issue and updated deployment rules.
```

Each description should be one concise sentence that helps an agent choose whether the note is relevant without opening it.

---

# 19. Historical Context Loading

Archived daily notes are cold context.

Default archived-note load count:

```text
0
```

If history is necessary:

1. inspect archive indexes;
2. identify the smallest likely date range;
3. open the most directly relevant note;
4. stop when sufficient context is found;
5. prefer current authoritative rule/context files when they already contain the necessary information.

Do not automatically load the last 7 days, the last 30 days, or the entire project history.

History is expanded only because the task requires it.

---