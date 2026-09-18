# Gap Register

**Status:** Live — the single list of everything known to be wrong, missing or
mis-stated. Update it in the same change that closes a row.

`STATUS.md` answers "is this ticket done?". `TRACEABILITY.md` answers "is this
requirement tested?". This file answers "what do we know is broken that no ticket
has caught yet?" — including gaps in the documents themselves.

A row is closed only when the thing it describes is no longer true. Creating a
ticket for a gap closes it *only* when the gap was "no ticket owns this".

## Totals

| | Blocker | High | Medium | Low | Total |
| --- | --- | --- | --- | --- | --- |
| **Open** | 0 | 1 | 0 | 0 | 1 |
| **Closed** | | | | | 64 |

Total tracked: **65**.

Last reconciled 2026-09-06, against commit `8bd65e5`. The closed count is a
recount of the rows themselves: an earlier revision's totals line said 39 while
its table held 40. Both columns above are counted from the rows below, not
carried forward.

The four rows added in this reconciliation came from a whole-repo
over-engineering audit. All four are the same shape: something built, registered
or declared, then called by nothing.

## Open

### High (1)

#### `write-path-never-run-against-a-real-board` — The connector's write path has never been exercised against the live API

- **Category:** test
- **Evidence:** The read path now is: `LiveBoardTests` runs against a live board and passes — WIQL, the batch get, the type mapping and the 404 mapping are observed, not assumed, and the resync Plan names exactly the items the CLI's own `audit` names on that same board. The write path is not: `CreateAsync` and `UpdateAsync` still run only against `FakeBoardGateway`, so the `application/json-patch+json` shapes, the hierarchy-reverse parent link and the never-retry-a-create rule remain ports rather than observations. **(2026-09-11, re-checked while attempting the remedy: the `BoardSyncSandbox` project the local profile names no longer exists — the org returns 404 for it, and its remaining projects are the four real ones. The token's account is refused project creation: "TF50309 … Create new projects." The throwaway project must be recreated by someone holding that org permission before the write tests can run.)**
- **Remedy:** Recreate the `BoardSyncSandbox` project (or grant the account Create-new-projects), then run the three `[LiveFact(Writes = true)]` tests with `ADO_BOARD_SYNC_LIVE_CONFIG` pointing at `desktop/local/sandbox.board.config.json` and `ADO_BOARD_SYNC_LIVE_WRITE=1`. They cover import, import-again idempotency, resync and the stale-plan refusal. Writing to any of the org's four real projects is not an option — desktop contribution rule 1.

### Medium (0)

### Low (0)

## Closed
