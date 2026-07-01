# Sample Backlog

Intro text before the first epic — this must be ignored by the parser.

## Epic 1 — Platform Foundations

*Context: shared libraries every other layer depends on.*

### PROJ-101 · Build the core event store

*Reference: ADR-002*

- Implement the append-only `EventStore`
- Add **optimistic-concurrency** checks
  * nested note that stays in the description only

### PROJ-102 · Wire up local orchestration

*Reference: ADR-010*

## Epic 2 — Delivery

### PROJ-201 · Ship the API

- Expose `/health`

## Appendix — Deferred items

### PROJ-999 · This issue is past the stop heading

- this bullet must never be parsed
