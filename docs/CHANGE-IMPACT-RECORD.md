# Change impact — agent-capable vault operations

## Existing behavior

The released local package exposes a constrained `agents write` command that
allows only `agent-output/<adapter>/` writes.

## New fact and required correction

The vault procedures require agents to create and maintain project/global rules,
context, roles, skills, routing indexes, daily notes, link metadata, and project
scaffolds. The current command cannot perform those required procedures.

## Impact

This changes the write authorization model, durable data handling, integrity
transaction scope, concurrency behavior, installer AppData output, repository
adapter requirements, and the agent-facing API. It invalidates any claim that the
current package is an agent-capable Rule Vault implementation.

## Direction

Replace the narrow output-only writer with explicit vault-authoring and lifecycle
operations. Preserve the path gate, strict metadata parsing, expected-hash
preconditions, lock/journal protocol, integrity anchor, and `.agents` ingress
gate. Do not substitute a generic unrestricted filesystem writer.
