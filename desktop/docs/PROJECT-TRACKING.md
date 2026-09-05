# Project Tracking: ADO Board Sync Desktop

**Status:** Live — the schedule, order, risk, and decision views. Updated in the same change that moves a ticket.

The tracking system splits by question, and every document answers exactly one:

| Question | Document |
| --- | --- |
| Is this ticket done? | `STATUS.md` (authoritative per-ticket state) |
| Is this requirement tested? | `TRACEABILITY.md` |
| What is broken that no ticket caught? | `GAPS.md` |
| When, in what order, and what could derail it? | **This document** |
| How is the board itself structured? | `GITHUB-PROJECT.md` |
| What exactly does each ticket require? | `BACKLOG.md` |

## 1. Milestones

Milestones are the PRD's release slices, restated with exit criteria that can be
checked from `STATUS.md` and `TRACEABILITY.md` alone.

| Milestone | Exit criterion | State |
| --- | --- | --- |
| R1 Desktop foundation | A profile opens from a file or from onboarding, parses, renders; config and credential paths validated. | **Partial.** Host, shell, both onboarding routes, scaffold, typed import errors, the file gateway, the composition root and central build properties are all done and committed. Two remainders: the OS credential store is written but untested (ABSD-103), and the profile registry has no view (ABSD-502). |
| R2 Backlog editor | Edit with live preview, inline validation, atomic save, CSV export. | **Done but for one row.** Editing, live recompute, atomic save with external-change refusal, and byte-identical CSV export are committed and tested. Open remainder: line-level gutter markers inside a description (ABSD-203). |
| R3 Plan & apply | Import/resync/resync-tasks/dedup/sync planned, reviewed, applied; Audit view matches the CLI. | **Partial.** All nine CLI commands now plan; Apply is gated, concurrent and ordered; the Audit view reports drift read-only and hands closure back through the same gate. Remainder is not features but proof: the write path has never run against a real board. |
| R4 Sprints, assignees & operations | Sprint/assignee tables with config write-back, close-children review, history store + timeline. | **Partial.** Every engine and view model exists and is tested — `BuildSprints`, `BuildAssign`, `BuildCloseChildren`, `SqliteOperationHistory`. The sprint, assignee and history views landed mid-audit and their nav sections are live; all three are uncommitted and none is opened by a test. Config write-back is still unticketed (GAPS `config-writeback-unticketed`). |
| R5 Distribution | Signed installable package per OS, installable without a toolchain (PRD-AC-17). | Not started (ABSD-601/602). The only slice with no code at all. |
| R6 Agent-assisted authoring | Agent CLIs spawn, edit as reviewed diff, plan consequences, runs recorded. | **Partial.** Providers, runner, edit session, diff review and run history are built and tested; the shell has no agent section to reach any of it. |

## 2. Burn-down

Counts from `STATUS.md` (44 tickets). The 2026-08-26 column is a recount of
that revision's own rows — its totals line said 13 Partial / 26 Not started,
which its rows contradicted (14 / 25); all three columns below sum to 44.

| State | 2026-08-26 (recounted) | 2026-09-01 | 2026-09-05 |
| --- | --- | --- | --- |
| Done | 5 | 5 | 21 |
| Partial | 14 | 20 | 21 |
| Not started | 25 | 19 | 2 |

The 2026-09-05 jump is one commit and one audit, not a week of delivery. The
tree was committed (`9f54b70`), which discharged the "uncommitted work is not
delivered" rule for ten rows whose evidence was already written. A further six
rows were found recorded as Not started while fully built, and eleven were found
recorded as Not started while their engines existed and were tested — those
eleven became Partial, not Done, because none of them has a view.

Read the Done column with that in mind: it counts tickets whose Outcome is
built and tested, not features a user can reach. The gap between those two
readings is the eleven Partial rows in R4 and R6.

Acceptance-criteria coverage (`TRACEABILITY.md`) is the number that has not
moved and should be re-derived next: it was 9 of 20 Covered before this audit.

## 3. Dependency map (remaining work)

```
push ──→ first CI run on ubuntu/headless ──→ ABSD-506 closed
ABSD-103 (test the OS credential store) ────────────→ R1 closed
ABSD-502 (registry view) ───────────────────────────┘

ABSD-108 (interaction test harness, xunit.v3)
        ├─→ tests for the three views that already landed:
        │     ABSD-401 sprints, ABSD-402 assignees, ABSD-508 timeline ─→ R4
        └─→ the two views still missing:
              ABSD-502 registry switcher ─→ R1
              ABSD-701–706 surfaces ─────→ R6

live write tests (throwaway project) ──→ ABSD-301/303 ──→ R3 closed
R3 + R4 closed ──→ ABSD-503 acceptance half ──→ ABSD-601/602 ──→ R5
```

The critical path to a shippable v1 is now: **push and read CI → ABSD-108's
harness → the five views → live write proof → ABSD-503 → packaging.** The
engine work that used to sit on this path is done.

## 4. Risks

| Risk | Likelihood | Impact | Response | Owner |
| --- | --- | --- | --- | --- |
| The write path has never touched a real board — patch shapes, parent links and retry rules are ports, not observations (GAPS: `write-path-never-run-against-a-real-board`, the one High) | Certain today | High — first real Apply is the first real test | Throwaway project + the three gated `[LiveFact(Writes = true)]` tests, before any R3 close | next slice |
| CI has still never executed this code: it is committed but not pushed | Certain | High — local green on macOS proves nothing about ubuntu or the headless platform | Push and read the first workflow run before trusting any parity or coverage claim | next slice |
| Views are arriving faster than the harness that can test them: three landed mid-audit with no test able to open them, and two more (ABSD-502, ABSD-700) are still to come | Certain | Medium — untouched code paths, and a burn-down that reads healthier than the application feels | ABSD-108's interaction harness, now urgent rather than tidy: it was meant to precede the views and did not | R4 |
| The OS credential store is written, wired and has no test — the suite only ever substitutes `UnavailableCredentialStore` | Certain | Medium — it is the one component that touches a real secret | Cover the three platform stores behind the process seam; the store is already split so the subprocess can be faked | R1 close |
| `PlanViewModel` constructs `OsCredentialStore.ForThisPlatform()` itself instead of resolving the registered port, so the composition root is not the only place a port meets its adapter | Certain | Low — works today, but it is the seam ABSD-106 exists to enforce, and the second such bypass will be harder to see | Inject `ICredentialStore`; `AppServices` already registers it and `CompositionRootTests` already resolves it | R1 close |
| Perf bounds (FSD NFR-2/3) are untested on the desktop side | Possible | Low — fixture backlogs recompute instantly today | Add a 500-item fixture benchmark when the editor's recompute path stabilises | R3/R4 |

## 5. Decision log

| Date | Decision | Why | Recorded in |
| --- | --- | --- | --- |
| 2026-08-19 | Full .NET port, not a Python wrapper | A wrapper would inherit CLI startup costs and could never share the live-preview path; parity tests keep the port honest | PRD §10, ARCHITECTURE §1 |
| 2026-08-20 | Avalonia 12.1.1 on net10.0 | Matches the reference implementation; verified against 11.3.20 | GAPS (closed rows) |
| 2026-08-26 | Preview parses generated markup, never Markdown | A second renderer would lie exactly when it is load-bearing | ARCHITECTURE §2 |
| 2026-09-01 | Editor edits description blocks, not whole files; headings stay file edits in v1 | Keeps the write path inside the parser's own coordinates — the splice cannot disagree with the parse | FSD §3.2.10, ARCHITECTURE §2 |
| 2026-09-01 | Unsaved buffers block Plan and Apply (not auto-save) | The file is the source of truth; auto-saving would write on every keystroke what the CLI requires `--go` to write | FSD §3.2.8, PRD principle 1 |
| 2026-09-01 | External-change refusal at save time now; proactive watcher later | The save-side guard is testable and complete today; the watcher (ABSD-504) adds earliness, not safety | FSD §3.11.4, TRACEABILITY AC-15 |
| 2026-09-01 | Onboarding scaffolds a starter backlog, opt-out | A brand-new organisation should reach an open, parseable backlog in one pass; an existing file is never touched | PRD AC-20, FSD §3.1 |
| 2026-09-01 | CSV export is artifact-only: Plans read the backlog, never the CSV | A stale CSV must not be able to change a Plan — inherited from the CLI's own `audit` rule | FSD §3.9 |
| 2026-09-01 | One profile at a time in v1; registry arrives as ABSD-502 | Multi-profile without a history store would mix credentials and plans across profiles | PRD §10 |
| 2026-09-01 | Specs approved (rev 2) while delivery continues | The implementation has been running against them for two weeks; Draft status was blocking nothing but honesty | PRD/FSD/ARCHITECTURE/DESIGN-SYSTEM headers |
| 2026-09-05 | Commit the tree as one checkpoint, not the §7 split | 174 files from four concurrent sessions with no commit behind any of them; splitting first would have meant reconstructing intent with no recovery point, and the slices would not each have built | §7 below |
| 2026-09-05 | Eleven engine-complete tickets are Partial, not Done, for want of a view | The vocabulary at the top of STATUS.md counts a ticket's Outcome, and every one of these names a surface a user can reach; a tested view model nobody can open is not the Outcome | STATUS.md ABSD-401/402/403/502/508/701–706 |

## 6. Suggested next-slice plan

1. **Push, and read the first CI run.** The tree is committed but unpushed, so
   the ubuntu and headless lanes have still never seen it. Fix whatever they
   find before anything else — every claim below assumes they are green.
2. **ABSD-108's interaction harness** — the separate xunit.v3 project that
   `Avalonia.Headless.XUnit` needs. It was meant to come before the views; three
   have now landed without it, so it is the thing standing between them and a
   Done row.
3. **The two views still missing** (ABSD-502's switcher and the ABSD-700
   surfaces), and tests for the three that just arrived.
4. **Test the OS credential store** (ABSD-103) and inject it through the
   composition root — the last R1 remainder, and the only Done-blocker that is
   not a view.
5. **Throwaway-project live writes** — retire the one High gap and close R3.

## 7. Commit history note

The tree was committed on 2026-09-05 as a single checkpoint, `9f54b70`, rather
than the six-commit split this section previously proposed. The reason is worth
keeping: by then the tree held 174 files written by four concurrent sessions in
one worktree, and no commit at all stood behind any of it. Splitting first would
have meant reconstructing four sessions' intent into per-slice commits with no
recovery point while doing so, and several of the proposed slices could not have
been made to build independently.

The tree was verified green before the commit — zero warnings, 554 tests passing
across Core (159), Desktop (321) and Parity (74), with 8 live-board tests skipped
for want of credentials.

The split remains the right shape for future work. It is retired here only
because the history it describes cannot now be written.

