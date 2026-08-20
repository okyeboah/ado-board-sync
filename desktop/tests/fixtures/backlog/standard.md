# Project backlog

Prose before the first Epic heading is ignored by the parser.

## Epic 1 — Platform Foundations
*Context: shared libraries every other layer depends on.*

### PROJ-101 · Build the core event store
*Reference: ADR-002*
- Implement the append-only `EventStore`
- Add optimistic-concurrency checks
  * note: covered by the snapshot policy below

### PROJ-102 · Wire up local orchestration

Some description with **bold** text and a table:

| Field | Meaning |
| --- | --- |
| `id` | the identity |

- A task after a table

## Epic 2 — Delivery

### PROJ-201 · Ship the first slice
- Only one task here

### Not an issue heading without a code

## Appendix — Deferred items
### PROJ-999 · Never parsed, after the stop heading
- never a task
