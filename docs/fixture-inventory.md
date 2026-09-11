# Synthetic fixture and control-test inventory

Phase 00 creates the inventory before feature code. Fixtures are disposable and must live
under test-owned temporary roots; no live vault path is a fixture.

| Fixture/control set | Planned observable coverage | Phase |
|---|---|---:|
| `fixtures/registry-single.json` | one validated vault selection | 01 |
| `fixtures/registry-multiple-no-default.json` | explicit decision required | 01 |
| `fixtures/registry-duplicate-id.json` | identity collision fails closed | 01 |
| `fixtures/metadata-duplicate-json.json` | duplicate keys rejected before deserialization | 01 |
| `fixtures/frontmatter-hostile.yaml` | YAML anchors, aliases, tags, duplicate keys, ambiguous scalars rejected | 01 |
| `fixtures/agents-paths/` | symlink, Gitlink, junction/reparse, traversal, hard-link, and escape cases | 01/05 |
| `fixtures/write-races/` | barrier-controlled replacement and stale-read conflict | 01/05 |
| `fixtures/transactions/` | prepared/committed/recoverable/unresolved journal cases | 01/03 |
| `fixtures/untrusted-command.md` | blocked inert command escalation and bounded disposition record | 02/06 |
| `fixtures/context-routing/` | mandatory ordering, scope activation, canonical packet equality | 02 |
| `fixtures/legacy-installations/` | inventory and conservative migration plans | 03 |

Platform security claims require real Windows, macOS, and Linux validation. Mocked filesystem
tests may cover decision logic but cannot mark no-follow, reparse, beneath-root, or watcher
capabilities as supported.
