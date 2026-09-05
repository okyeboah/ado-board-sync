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
| R1 Desktop foundation | A profile opens from a file or from onboarding, parses, renders; config and credential paths validated. | **Functionally complete, uncommitted.** Host, shell, both onboarding routes, scaffold, typed import errors all exist; the slice stays open until the tree is committed and the profile registry (ABSD-502) and OS credential store (ABSD-103) land. |
| R2 Backlog editor | Edit with live preview, inline validation, atomic save, CSV export. | **Functionally complete, uncommitted.** Editing, live recompute, atomic save with external-change refusal, and byte-identical CSV export are all built and tested. Open remainder: line-level gutter markers inside a description. |
| R3 Plan & apply | Import/resync/resync-tasks/dedup/sync planned, reviewed, applied; Audit view matches the CLI. | **In progress.** Three of the five structural commands plan and apply behind the confirmation and stale-plan guards; `audit` and `dedup` (and `sync-one`) remain, then the read-only Audit view (ABSD-304/306). |
| R4 Sprints, assignees & operations | Sprint/assignee tables with config write-back, close-children review, history store + timeline. | Not started (ABSD-401–403, 501, 508). |
| R5 Distribution | Signed installable package per OS, installable without a toolchain (PRD-AC-17). | Not started (ABSD-601/602). |
| R6 Agent-assisted authoring | Agent CLIs spawn, edit as reviewed diff, plan consequences, runs recorded. | Not started (ABSD-701–706); provider model decided: spawn installed CLIs, never hold a key. |

## 2. Burn-down

Counts from `STATUS.md` (44 tickets). The 2026-08-26 column is a recount of
that revision's own rows — its totals line said 13 Partial / 26 Not started,
which its rows contradicted (14 / 25); both columns below sum to 44.

| State | 2026-08-26 (recounted) | 2026-09-01 (this run) |
| --- | --- | --- |
| Done | 5 | 5 |
| Partial | 14 | 20 |
| Not started | 25 | 19 |

Every row this run moved is Partial, not Done, on purpose: the project's rule
is that uncommitted work is not delivered. The moment the tree is committed and
reviewed, ABSD-203/204/206/207 (and the host/onboarding rows from earlier runs)
are candidates to flip — their evidence is already written in `STATUS.md`.

Acceptance-criteria coverage (`TRACEABILITY.md`): 9 of 20 Covered, 4 Partial,
7 Open — up from 2 Covered of 17. The three new criteria (AC-18/19/20) were
born Covered; AC-16 (CSV parity) went Open → Covered this run.

## 3. Dependency map (remaining work)

```
ABSD-105 (build props/CPM) ─────────────────────────┐
ABSD-107 (load off UI thread) ──┐                   │
ABSD-502 (profile registry) ────┼─→ R1 truly closed │
ABSD-103 (OS credential store) ─┘                   │
ABSD-302 remainder: audit ──→ ABSD-304 (audit view) ─┼─→ R3 closed
                  └── dedup, sync-one ───────────────┘
ABSD-401/402/403 (sprints/assignees/close-children)
        └─ depend on ABSD-302/303 + config write-back → R4
ABSD-501 (history store) ──→ ABSD-508 (timeline) ────→ R4
ABSD-503 (acceptance suite) ← needs R3+R4 views ──→ ABSD-601/602 → R5
ABSD-701–706 (agents) ← needs ABSD-106/107 + 203/206 + 302/305, 501 → R6
```

The critical path to a shippable v1 is: **commit → ABSD-105 → audit (ABSD-302)
→ ABSD-304 → R3 close → R4's three views + history → ABSD-503 → packaging.**

## 4. Risks

| Risk | Likelihood | Impact | Response | Owner |
| --- | --- | --- | --- | --- |
| The write path has never touched a real board — patch shapes, parent links and retry rules are ports, not observations (GAPS: `write-path-never-run-against-a-real-board`, the one High) | Certain today | High — first real Apply is the first real test | Throwaway project + the three gated `[LiveFact(Writes = true)]` tests, before any R3 close | next slice |
| The whole desktop tree is uncommitted; CI has never executed it | Certain | High — local green proves nothing about ubuntu/headless | Commit in the §7 slices; read the first workflow run before trusting the parity claim | next slice |
| Six CLI commands still have no desktop equivalent, so "plan for every command" (PRD §5) is not yet true | Certain | Medium — users fall back to the CLI for those | `audit` next (read-only, unlocks the Audit view); dedup/sync-one follow | R3 |
| No structured diagnostics (ABSD-507): failures reach users through the status bar only | Certain | Medium — supportability, not correctness | Land with the history store; the error-code table (FSD §5.1) is already the vocabulary | R4 |
| Perf bounds (FSD NFR-2/3) are untested on the desktop side | Possible | Low — fixture backlogs recompute instantly today | Add a 500-item fixture benchmark when the editor's recompute path stabilises | R3/R4 |
| Two SDKs installed locally; package versions pinned per-csproj (ABSD-105 undone) | Possible | Low — drift, not breakage | ABSD-105 next after the commit | R2/R3 boundary |

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

## 6. Suggested next-slice plan

1. **Commit and let CI speak.** The §7 slices, then fix whatever ubuntu/headless
   finds. Everything else is built on sand until this lands.
2. **ABSD-105** — `Directory.Build.props` + `Directory.Packages.props` +
   `global.json`; small, and it de-risks every later project.
3. **`audit` in the Plan Builder** — read-only, so it needs no new Apply
   machinery; it unlocks ABSD-304's Audit view and closes the biggest PRD §5
   gap after the commit.
4. **ABSD-103/107** — credential store + background loading; two R1 remainders
   that the R4 history work will otherwise trip over.
5. **Throwaway-project live writes** — retire the one High gap.

## 7. Suggested commit split for the current tree

The tree mixes this run's work with earlier uncommitted work. A readable
history that keeps every commit's tests green:

1. `feat(desktop): editable split-pane editor with atomic save and unsaved gate` —
   Core parser ranges + BacklogSplicer + BacklogWorkspace.SaveMarkdown +
   BacklogNodeViewModel/MainWindowViewModel/PlanViewModel + XAML + their tests.
2. `feat(desktop): import CSV export matching gen-csv` — ImportCsv + parity
   driver mode + parity/Core/VM tests + rail button and picker.
3. `feat(desktop): onboarding scaffold and typed import errors` — StarterBacklog
   + OnboardingViewModel + view + OpenFromOnboarding + tests.
4. `docs(desktop): approve and re-ground PRD/FSD/ARCHITECTURE/DESIGN-SYSTEM` —
   the four specs, rev 2.
5. `docs(desktop): add project tracking and reconcile the live documents` —
   PROJECT-TRACKING.md + STATUS/TRACEABILITY/GAPS/BACKLOG/GITHUB-PROJECT/
   CONVENTIONS + root README + CHANGELOG.
6. The pre-existing CLI-side modifications (`src/`, `tests/`) as their own
   commit, first — they are someone else's in-flight work and belong at the
   front of the stack, not mixed into desktop commits.
