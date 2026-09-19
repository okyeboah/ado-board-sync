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
| R1 Desktop foundation | A profile opens from a file or from onboarding, parses, renders; config and credential paths validated. | **Done.** Host, shell, onboarding, scaffold, typed import errors, file gateway, composition root, central build properties, the tested OS credential store (resolved through the composition root), and the profile registry with switcher and Forget control. |
| R2 Backlog editor | Edit with live preview, inline validation, atomic save, CSV export. | **Done.** Editing, live recompute, atomic save with external-change refusal, and byte-identical CSV export are committed and tested. The gutter-marker remainder was superseded by the PRD-AC-03 decision: the converter escapes raw markup, so no authored input can put a problem on one line. |
| R3 Plan & apply | Import/resync/resync-tasks/dedup/sync planned, reviewed, applied; Audit view matches the CLI. | **Partial.** All nine commands plan; Apply is gated, concurrent, ordered and recorded; the Audit view hands closure back through the same gate. Remainder is proof, not features: the write path has never run against a real board, and the sandbox project it needs is gone (GAPS holds the blocked remedy). |
| R4 Sprints, assignees & operations | Sprint/assignee tables with config write-back, close-children review, history store + timeline. | **Done.** The tables save into the profile's own config through the atomic write (ticketed in ABSD-401/402 since 2026-09-11); close-children reviews through Audit's findings and applies through the gate; the timeline renders, filters and shows agent runs too. |
| R5 Distribution | Signed installable package per OS, installable without a toolchain (PRD-AC-17). | **Partial.** `publish.sh` and `package.sh` produce self-contained, per-user packages for macOS, Windows and Linux, and CI builds and checks all three every run. The packages are unsigned by design — signing needs a credential this repository must never hold — so ABSD-601 stays open on exactly that. |
| R6 Agent-assisted authoring | Agent CLIs spawn, edit as reviewed diff, plan consequences, runs recorded. | **Done.** The Agent section is in the nav rail: discovery, scoped prompt with the three disclosures, run and cancel, diff review, the Plan handoff, and every run readable in the history timeline. |

## 2. Burn-down

Counts from `STATUS.md`. The 2026-08-26 column is a recount of that revision's
own rows — its totals line said 13 Partial / 26 Not started, which its rows
contradicted (14 / 25). The first four columns sum to 44; the last to 45, which
is the same list plus ABSD-113, added when the work it names was done.

| State | 2026-08-26 (recounted) | 2026-09-01 | 2026-09-05 | 2026-09-11 | 2026-09-18 |
| --- | --- | --- | --- | --- | --- |
| Done | 5 | 5 | 23 | 37 | 37 |
| Partial | 14 | 20 | 21 | 7 | 8 |
| Not started | 25 | 19 | 0 | 0 | 0 |

The 2026-09-18 delta is structural debt, not features: the shell view model's
decomposition (ABSD-113), built and tested, Partial until committed.

The 2026-09-05 jump is one commit and one audit, not a week of delivery. The
2026-09-11 move is the reverse correction: the rows had drifted behind the code
they describe, and the reconciliation re-read each Outcome against what is
committed and tested. Seven rows stay Partial, each for a named reason — the
live write path (blocked on a missing sandbox project), the installed-package
proof, the tri-platform launch check, signing, pointer synthesis (declined with
a rationale), and the GitHub board itself.

Acceptance-criteria coverage is 19 of 20 Covered; AC-17 is the holdout, and it
waits on an installed package, not on a test that could be written.

## 3. Dependency map (remaining work)

```text
sandbox project recreated ──→ [LiveFact(Writes)] tests ──→ ABSD-301/303 close ──→ R3 closed
org permission granted ─────┘
ABSD-506 launch proof (3 OS lanes) ──→ ABSD-506 closed
signing credentials ────────────────→ ABSD-601 closed ──→ R5 closed
issue bodies + close 37 Done tickets ──→ ABSD-111 closed
```

The engine work that used to sit on this path is done. What remains is proof and
credentials: everything now blocked on this map is blocked on something outside
the repository.

## 4. Risks

| Risk | Likelihood | Impact | Response | Owner |
| --- | --- | --- | --- | --- |
| The write path has never touched a real board — patch shapes, parent links and retry rules are ports, not observations (GAPS, the one High) | Certain until the sandbox exists | High — first real Apply is the first real test | The gated `[LiveFact(Writes = true)]` tests are ready; the sandbox project must be recreated, which needs an org permission the token does not hold | org owner |
| The live documents drift behind the tree — STATUS's rows described the agent epic and four tickets as unbuilt weeks after they landed | Demonstrated twice | Medium — a burn-down that reads healthier or sicker than the application is | Reconcile in the same change that moves a ticket; this file's milestones now re-derive from STATUS's rows | every agent |
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
| 2026-09-18 | One `CredentialSession` behind both gate surfaces | Plan and Audit had each assembled the resolution chain and each worded the badge; the wordings had already drifted on what they report about a failed source | `CredentialSession.cs`, ABSD-113 |
| 2026-09-18 | A planning table built without a reload gets a named refusal, not a required dependency | Making the delegate required would have broken seven inert call sites for a path none of them reaches (`CanSave` needs an open profile); the refusal keeps the failure loud where it can actually fire | `PlanningTableViewModel.NoReload` |
| 2026-09-18 | The 500-line rule is met with real extractions first, partial files only for one coherent vocabulary | Tree, session and credential concerns became collaborators; what remained of the shell and the gate is one concern each, split at that seam only | ABSD-113 |

## 6. Suggested next-slice plan

1. **Recreate the sandbox project** (or grant the account Create-new-projects),
   then run the three gated live-write tests — R3 closes on it.
2. **Close the 37 issues whose STATUS.md row reads Done**, and pass over the
   issue bodies — ABSD-111's remainder.
3. **The tri-platform launch proof** for ABSD-506.
4. **Signing** for ABSD-601 when a credential the repository can hold exists.

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
