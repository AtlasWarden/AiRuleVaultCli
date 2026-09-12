---
file_id: "8d71fdcf-c5cc-4701-a944-2d16635b4b46"
title: "Rule Vault Index"
project: "global"
kind: "index"
status: "active"
aliases: []
hasInboundLinks: false
created: "2026-09-10 00:00:00 UTC"
updated: "2026-09-10 00:00:00 UTC"
---

# Rule Vault Index

## Purpose

This vault is the authoritative durable memory and rule system for agents working for the user.

## Required Loading Behavior

1. Read this file.
2. Load the applicable global runtime files before task-specific context.
3. Identify only the relevant project or projects.
4. Open each relevant project router, rule index, context index, and daily pointer.
5. Do not read archived history unless the task requires it.

## Global Runtime

- [Core Runtime](./global/core-runtime.md)  -  Always; authority, bootstrap, and task-start procedure.
- [Vault Discovery and Integrity](./global/vault-discovery-and-integrity.md)  -  Always; identity and protected-content verification.
- [File and Context Management](./global/file-and-context-management.md)  -  Create, move, link, route, or maintain durable content.
- [Daily Continuity](./global/daily-continuity.md)  -  Daily handoff, rollover, archival, and promotion.
- [Project Management](./global/project-management.md)  -  Project creation, recovery, Git promotion, or shared rules.
- [Write Safety](./global/write-safety.md)  -  Any durable vault mutation.
- [Runtime Conformance](./global/conformance.md)  -  Conditional; runtime/delegation conformance checks.
- [Validation and Remediation](./.vault-system/validation-and-remediation.md)  -  Conditional; validation, repair, or recovery work.

## Projects

No projects have been created yet.

## Historical Notes

Historical work is indexed inside each project's `daily/archive/` tree and is not default context.
