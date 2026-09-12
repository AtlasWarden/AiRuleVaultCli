# Vault Discovery, Identity, Validation Authority, and Registry Safety

This is installed runtime authority for ordinary agents. It contains only the Section 4 behaviors that remain relevant after installation.

## 4.4 Internal vault identity

Every vault contains `.vault-system/vault.json` with the same immutable `vault_id` as its registry entry. A path alone is never identity.

## 4.5 Runtime Validation Specification

Every vault contains `.vault-system/validation-spec.md`.

Install it by copying Appendix A exactly between its payload markers, excluding the markers.

Runtime validation uses this file and does **not** load the master Creation Guide.

Store its canonical hash as `validation_spec_sha256` and store one versioned copy under `<RULE_VAULT_INSTALL_DIR>/validation-specifications/`.

## 4.8 Runtime vault selection

Ordinary agents:
- explicit requested vault -> validate/use it;
- otherwise valid default -> use it;
- otherwise exactly one valid vault -> use it;
- multiple valid vaults with no default -> ask;
- no valid vault -> report that installation/bootstrap is required.

Always verify `vault_id`. Ordinary runtime does not compare Creation Guide hashes.

## 4.15 Registry safety

Registry writes preserve unrelated vault entries, re-read before mutation, use protected/atomic handling when concurrent writers are possible, parse before commit, and verify after replacement.

## 4.18 Protected Content Manifest Anchor

Each local vault maintains `.vault-system/content-integrity.json` for protected authoritative runtime content.

The selected registry entry stores the canonical SHA-256 of that manifest as `protected_content_manifest_sha256`.

During normal runtime bootstrap:

1. verify `vault_id`;
2. canonical-hash `.vault-system/content-integrity.json`;
3. compare it with registry `protected_content_manifest_sha256`;
4. if they differ, do not silently trust protected authoritative files; run targeted integrity validation and surface the mismatch;
5. when reading a protected file, verify its file hash against the manifest entry before treating it as authoritative.

This adds one small manifest-hash check at runtime and file hashes only for protected files actually loaded. Full validation verifies every manifest entry.

A legitimate protected-content change must update the protected file, the manifest, and the external registry anchor as one protected logical operation.

The manifest/hash system detects unauthorized or incomplete drift when the external anchor remains trustworthy. It does not defeat an attacker who can modify the vault, the external registry, and any controlling Git/IAM systems together.

---