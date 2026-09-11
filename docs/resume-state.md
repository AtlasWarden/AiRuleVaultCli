# Resume state

Updated 2026-09-10.

- Windows win-x64 package `artifacts/package-win-x64-r33` and `artifacts/RuleVault-0.1.0-dev-win-x64-r33.zip` are the validated deliverables.
- The package is self-contained: it includes `rv.exe`, runtime payload, manifest, guide, migrations, and installers; it contains no CLI source project.
- Validation used only `artifacts/` fixtures. The live vault and AppData registry were copied/read for comparison but never changed.
- The copied-vault update, fresh install, idempotent rerun, global daily creation, project creation, managed vault authoring, protected re-anchoring, link-aware delete refusal/completion, Git `.agents` adapter, branch-local manifest update, and directory-symlink escape rejection passed. R31 additionally verified installed opaque agent session registration, freshness invalidation/refresh, per-file token usage, table export, private-vault path non-disclosure, and installer-delegated bootstrap discovery/replacement/idempotence against a synthetic user home. R33 adds the structured table-array renderer.
- macOS and Linux package installation/execution remain unvalidated because those hosts are unavailable.
