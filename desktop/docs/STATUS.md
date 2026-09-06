# Delivery Status

**Status:** Live — the single source of truth for what is built. Update it in the
same change that moves a ticket.

`TRACEABILITY.md` answers "is this requirement tested?". `GAPS.md` answers "what
do we know is broken?". `PROJECT-TRACKING.md` answers "when and in what order?".
This file answers "is this ticket done?". One vocabulary, here:

| State | Meaning |
| --- | --- |
| Done | Everything the ticket's Outcome names is built, with a passing test or a named gate met. Its GitHub issue is closed. |
| Partial | Some of the Outcome is built. The remainder is named in the row. The issue stays open. |
| Not started | No code exists for it. |

A ticket is never Done because most of it works. If the row needs the word
"except", it is Partial. Work that exists only in an uncommitted working tree is
Partial, not Done — nobody else can run it yet.

**The tree was committed on 2026-09-05 (`9f54b70`), which discharged that rule
for every row that had been waiting only on it.** Ten rows flipped for that
reason alone; their evidence had already been written here.

**It was then pushed, and CI ran over it for the first time — green at
`2425e5b`**, all six jobs: both CLI matrix legs, the desktop build and its
headless Avalonia suite on ubuntu, and packaging on macOS, Windows and Linux.
Two defects surfaced only because it ran, and both are fixed: the packaging
scripts were never committed (an unanchored `build/` in `.gitignore` swallowed
them), and Git-Bash on the Windows runner has no `zip`.

The tree also moved while this pass was being written: another line of work
landed `SprintsView`, `AssigneesView` and `HistoryView` between the commit and
this revision, and then the profile switcher, `UiHarness`, `ShellInteractionTests`
and `AcceptanceTests` after it. Those rows are stated as of the working tree, and
say so. The suite stands at **655 .NET tests** (177 Core, 74 parity, 404 desktop,
8 live-board skipped) and **129 CLI tests**, Release, zero warnings.

A second correction landed in the same pass. This file was last revised
2026-09-01 and had fallen well behind the code: six tickets were recorded as
Not started while fully built, and the whole ABSD-700 epic was recorded as
scoped-only while its engine exists and is tested.

The board's own vocabulary (Backlog / Ready / In Progress / In Review / Blocked /
Done) maps onto this one as: Done → Done, In Review and In Progress → Partial,
everything else → Not started.

## The shape of what remains

Two sentences, because the per-ticket rows below no longer say it plainly:

**The engines are built; one of them has no window.** This audit found five view
models covered by tests and unreachable from the running application. Four of
them — `SprintPlanningViewModel`, `AssigneePlanningViewModel`, `HistoryViewModel`
and `ProfileRegistryViewModel` — got their surfaces while the audit was being
written, and then got tests: `ShellInteractionTests` opens each pane in a real
headless window, clicks its buttons and types into its fields. Their rows stay
Partial because they are uncommitted, not for want of a surface or a test.

One remains genuinely unreachable. `ProfileRegistryViewModel` (ABSD-502) now has
its switcher in the nav rail, but `AgentAuthoringViewModel` has no surface and no
nav section — the whole ABSD-700 epic is reachable from the test suite and
nowhere else. Its view model is covered (`AgentAuthoringViewModelTests`, 28,
including the three disclosure sentences a user reads before handing a local CLI
a directory), which is what makes the missing surface the only thing left.

That is why eleven tickets read Partial rather than Done. For eight of them the
remaining work is a view or a test around one; for none of them is it logic.

## Delivery tickets

### ABSD-100 · Product foundation

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-101 Solution and conventions | Done | Modules, tests and documentation delivered and committed. The host and UI half is carried by ABSD-104 and ABSD-109 rather than silently by this row. |
| ABSD-102 Config loader and schema validation | Done | `BoardConfig` + `BoardConfigSchema`; 4 parity scenarios, 22 config/schema tests, and a guard that fails the build if the schema gains a key the validator does not know. |
| ABSD-103 Credential resolution | Done | `PatResolver` resolves a session-entered token, the OS credential store, `pat_env`, then `pat_file`, collecting failures instead of stopping at the first (`PatResolverTests`, 7). `OsCredentialStore.ForThisPlatform()` picks the macOS Keychain, `secret-tool` or Windows Credential Manager, passing the secret on stdin and never on a command line, and `CredentialStoreTests` covers all three through an injected process seam (21 tests, every platform). The store is resolved from the composition root rather than constructed by its callers. |
| ABSD-104 Avalonia desktop host | Done | `AdoBoardSync.Desktop` builds an executable; `dotnet run` opens a window that loads a profile and renders the tree. Launch gate and per-section render tests pass (`WindowLaunchTests`, 7). |
| ABSD-105 Central build properties and package versions | Done | `Directory.Build.props` sets the shared conventions and `ManagePackageVersionsCentrally`; `Directory.Packages.props` holds every version. No csproj in `src` or `tests` carries a `Version=` attribute any more. `tests/Directory.Build.targets` is imported after each project so it can read that project's own `IsTestProject`, which is what keeps the xunit packages off `AdoBoardSync.TestKit`. |
| ABSD-106 Infrastructure and gateways | Done | `IBacklogFileStore` in Core, `FileSystemBacklogFileStore` in Infrastructure (strict UTF-8, BOM preserved to match `parser.py`, temp-then-rename with `Flush(flushToDisk: true)`), and `AppServices` as the one composition root. `BacklogFileStoreTests` (10) and `FileStoreParityTests` (3) cover it; `CompositionRootTests` (8) pins that every port resolves. |
| ABSD-107 Profile loading off the UI thread | Done | `ProfileLoader` — load, reload, save and CSV export are all asynchronous, cancellable, and run the file work off the calling thread. `ProfileLoaderTests` (9) drive the whole path through the in-memory store with no disk. |
| ABSD-108 Headless UI test harness | Partial | `UiHarness` owns the headless platform for the whole process (two classes bootstrapping their own would fail whichever ran second), finds controls across both the visual and logical trees, answers "is this actually on screen" for the collapsed panes, clicks and types. `ShellInteractionTests` (8) drive the real window. It earned itself twice on its first run: `BindingFailures` caught the shell assigning its DataContext after `InitializeComponent`, so every binding resolved against null once on the way up, and the finders caught two panes offering the same button caption. Verified live by reverting that fix and watching the assertion fail. **Remaining:** it drives controls rather than synthesising input — no pointer or keyboard events — and `Avalonia.Headless.XUnit`'s own attributes are still not in use. |
| ABSD-109 Design system and shell chrome | Partial | Both theme palettes from DESIGN-SYSTEM.md §2, the spacing/type/radius scale, and the nav-rail shell — verified in light and dark. **Remaining:** the documented contrast pass and the §6 accessibility rules. |
| ABSD-110 Credential status and board-action gating | Done | `PlanViewModel` resolves the token off the UI thread and reports which source answered; board actions are refused with that status as their message when none does. **Caveat, tracked in GAPS:** the view model constructs `OsCredentialStore.ForThisPlatform()` itself rather than resolving the registered port. |
| ABSD-111 Reconcile the documents and the board | Partial | Delivered this run: STATUS/PROJECT-TRACKING/GAPS reconciled against the committed tree — 16 rows moved to Done, 11 from Not started to Partial, and the view-gap named above stated once where it belongs. Reconciled again against the working tree that followed it: ABSD-108, 401, 402, 502, 503, 508, 703 and 705 restated, TRACEABILITY's eight newly-covered criteria recorded (no criterion is Open now), three gap rows opened, and the stale 554-test figure corrected. Also sized a gap that had been under-reported: eleven of the tickets this file tracks are defined in no BACKLOG.md Outcome, not one. **Remaining:** those eleven Outcomes, the GitHub board itself (28 cards, 16 issues to close) and a pass over the issue bodies. |
| ABSD-112 Onboarding without a config file | Done | Two equal routes in; the form composes the same JSON the config file holds. A failed config open is reported inline with a typed code instead of replacing onboarding with an error page, and the form route scaffolds a working starter backlog with the profile's exact prefix when none exists — opt-out, never overwriting an existing file (`OnboardingViewModelTests`, 5). |

### ABSD-200 · Backlog engine

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-201 Backlog parser | Done | `BacklogParser`; parity against `parser.py` including custom `epic_heading_regex` and relative-path scenarios. Carries each item's description-block range — editor metadata the parity comparison does not count, pinned by `BacklogParserTests` (17). |
| ABSD-202 Markdown-to-HTML conversion | Done | `MarkdownHtml`; parity for HTML, plain, inline and norm against `htmlfmt.py`. |
| ABSD-203 Split-pane editor with live preview | Partial | The source pane is editable; the buffer recomputes preview, generated HTML, task list and markup problems per keystroke from the same Core functions. Save splices every dirty buffer back at the parser's own ranges, last-to-first, preserving EOL style, trailing newline and blank separators (`BacklogSplicerTests`, 11). Plan and Apply are refused while edits are unsaved. **Remaining:** line-level gutter markers inside one description. |
| ABSD-204 CSV export | Done | `ImportCsv` ports `csvio.py` plus the Python csv dialect (minimal quoting, doubled quotes, CRLF records, Epics in Title 1); 8 Core tests pin the rules and `ImportCsvParityTests` compares every backlog fixture byte-for-byte against the live Python. |
| ABSD-205 Description preview rendering | Done | `PreviewDocument` parses the generated markup — not the Markdown source — so the preview cannot disagree with what would be written. 11 tests including one asserting no text is lost; `HtmlLayout` (8 tests) pins that indentation changes nothing but whitespace. |
| ABSD-206 Atomic backlog save | Done | Temp-then-rename in the destination directory, flushed to the device before the rename; refuses `backlog.changed_on_disk` when the file moved since it was opened, comparing content hashes rather than timestamps so an identical rewrite is not a false conflict. The external edit survives and the buffer is preserved. |
| ABSD-207 Import CSV from the open profile | Done | `ProfileLoader.ExportCsvAsync` writes the CSV to a picked path from the open workspace — no credential, no network — from a rail button gated on `HasProfile`. |

### ABSD-300 · Plan, apply & audit

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-301 Azure DevOps connector | Partial | `AzureDevOpsGateway` ports `client.py`'s WIQL, batch get, create, patch, delete and retry contract — including never retrying a create and always retrying a delete of an identified item. Iterations and teams have since landed (`EnsureIterationAsync`, `DefaultTeamAsync`, `AddTeamIterationAsync`), closing this row's previous remainder. Its **read** path is proven against a live board by `LiveBoardTests`. **Remaining:** the write paths have still never run against a real board. |
| ABSD-302 Plan Builder | Done | All nine CLI commands plan: `BuildImport`, `BuildResync`, `BuildResyncTasks`, `BuildDedup`, `BuildSprints`, `BuildAssign`, `BuildCloseChildren`, `BuildSyncOne` and `BuildAudit`. 62 builder tests plus 12 `PlanParityTests`. Generation is pure and reads only. |
| ABSD-303 Apply Executor with stale-plan guard | Partial | Applies exactly the reviewed rows; refuses on `plan.stale_backlog` or `plan.stale_board` before the first write, and while unsaved editor edits exist. Independent rows are written concurrently — bounded fan-out, ordered after dependency waves — and reported outcomes keep the Plan's row order (`ApplyExecutorTests`, 12). **Remaining:** never run against a live board. |
| ABSD-304 Audit | Done | `BuildAudit` + `AuditReport` port the CLI's `audit` — missing, extra, drifted, and open descendants of Done. `PlanBuilderAuditTests` (13). |
| ABSD-305 Plan review and Apply confirmation surface | Done | Command selector over all eight applicable commands with their options, Plan rows badged with glyph and word, and a confirmation restating counts before any write (`PlanViewModelTests`, 21). |
| ABSD-306 Audit view and Close-children handoff | Done | `AuditView.axaml` renders the report read-only; `RequestCloseChildren` hands off through `MainWindowViewModel` to `BoardPlan.Choose(PlanCommand.CloseChildren)`, so closure goes through the same Plan/Apply gate as every other write (`AuditViewModelTests`, 12). |

### ABSD-400 · Sprint, ownership & closure planning

Every row here has its engine and its view model, and none has a view. See
"The shape of what remains".

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-401 Sprint planning view | Partial | `BuildSprints` plans iteration creation and assignment; `SprintPlanningViewModel` drives the table (`PlanningTableTests`, 15). `SprintsView.axaml` is in the nav rail and `ShellInteractionTests` adds a row through the button and types into it, so the two-way bindings are proven rather than assumed. `Adopt` fills the table from the open profile and `Clear` empties it when that profile closes. **Remaining:** committed. |
| ABSD-402 Assignee planning view | Partial | `BuildAssign` plans assignment with the `assign-only`, `only-unassigned` and `assign-from-parent` options; `AssigneePlanningViewModel` drives the table. Comparison matches the CLI on all three identity facets — uniqueName, id and displayName — which a uniqueName-only comparison got wrong and would have re-planned the same write forever. An item that is already correctly owned is now shown as **Unchanged** rather than dropped from the plan (PRD-AC-12); it was previously omitted, which made a plan listing two of five configured codes indistinguishable from one that had lost the other three. `AssigneesView.axaml` is in the nav rail and driven by `ShellInteractionTests`. **Remaining:** committed. |
| ABSD-403 Close-children review | Partial | `BuildCloseChildren` plans the terminal state for every open descendant of a Done item, and the Audit view hands off to it. **Remaining:** the dedicated review surface the ticket names; today the handoff lands in the generic Plan table. |

### ABSD-500 · Operations and delivery

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-501 Operation history store | Done | `SqliteOperationHistory` over one SQLite file, registered under both the ports it implements so a single connection serves history and agent runs. `OperationHistoryTests` (13) and `OperationsWiringTests` (4). |
| ABSD-502 Multi-profile registry | Partial | `ProfileRegistry`, `JsonProfileRegistryStore` and `ProfileRegistryViewModel`, now tested (`ProfileRegistryTests`, 18; `ProfileSwitchingTests`, 25) and reachable: the switcher combo is in the nav rail, `Adopt` registers each profile it opens, and choosing another one opens it. `ShellInteractionTests` drives the real combo. **Remaining:** committed, and a Remove control — a profile can be registered from the app but only un-registered by editing `profiles.json`. |
| ABSD-503 End-to-end parity and acceptance suite | Partial | Both halves now exist. Parity: 74 comparisons against the live Python modules, `ParityCoverageTests` guards, `PlanParityTests` (12) comparing the board each implementation leaves behind, and `LiveBoardTests` gated behind `ADO_BOARD_SYNC_LIVE_CONFIG` (writes behind `ADO_BOARD_SYNC_LIVE_WRITE`). Acceptance: `AcceptanceTests` carries one test per PRD criterion, each tagged with its id, and `EveryAcceptanceCriterionInThePrdHasATest` reads `PRD.md` and fails when a criterion has no test or a test claims one that no longer exists — verified by adding a PRD-AC-21 row and watching it fail. **Remaining:** committed; and PRD-AC-17 is asserted about the packaging scripts rather than an installed package, which no in-process test can do. |
| ABSD-504 External change detection | Partial | The save-side half: save refuses to overwrite an external change and names Reload. **Remaining:** proactive watching — marking the profile stale the moment the file moves, before any save attempt. |
| ABSD-505 Continuous integration | Done | `.github/workflows/build-and-test.yml`; green on `main`. |
| ABSD-506 Extend CI to the desktop application | Partial | The workflow restores, builds and tests the whole `.slnx` in Release on ubuntu, live tests skipping without the env var. **Remaining:** a packaging lane; and CI has still not run against this code, because the commit has not been pushed. |
| ABSD-507 Structured diagnostics | Done | `JsonLinesDiagnosticsSink` writes Plan generation, Apply and file writes to a rolling JSONL log, on by default; `DiagnosticRedaction` registers the resolved token so it cannot reach the log. The sink never throws, so an unwritable log directory costs the log and nothing else (`DiagnosticsSinkTests`, 13). **This row was previously false and is corrected here.** It claimed those three events were written when nothing emitted one: `DiagnosticsExtensions` declared all five with no production caller, and `ApplyHistoryRecorder` built its own by hand, putting item titles in the log. The Plan gate now emits `PlanGenerated`, `ApplyStarted`, `ApplyFinished` and `OperationFailed`; `ProfileLoader` emits `FileWritten` for the backlog save and the CSV export. `OperationsWiringTests` proves each reaches a sink and that no event carries a title (NFR-6, Covered). |
| ABSD-508 Operation history timeline | Partial | `HistoryViewModel` reads the store and scopes every row to the active profile's key (`HistoryTimelineTests`, 15). `HistoryView.axaml` is in the nav rail, enabled only when the store resolved, and `ShellInteractionTests` opens it. The timeline is also now fed: `Adopt` loads it, and the recorder actually receives every row — Apply reported outcomes through a `Progress<T>`, which posts to the dispatcher and returned *after* the run was closed, so the store refused them and the per-item outcomes were being dropped (`AcceptanceTests`, PRD-AC-08, found it). **Remaining:** committed, and a view-level test. |

### ABSD-600 · Distribution

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-601 Installable desktop package | Partial | `desktop/build/package.sh` produces a per-user package on all three platforms — a `.app` in a `.dmg`, a portable `.zip` needing no admin rights, and a `.tar.gz` with a `.desktop` entry and an `install.sh` that installs under `~/.local`. CI builds all three every run and uploads them. **Remaining:** the Outcome says *signed*, and this deliberately is not: the script stamps every package unsigned and prints the exact `codesign`/`notarytool`/`signtool` invocations instead. Signing needs credentials this repository must never hold. |
| ABSD-602 Self-contained local build | Done | `desktop/build/publish.sh` publishes self-contained single-file builds with the runtime bundled, so the result runs on a machine with no .NET installed (PRD-AC-17). Trimming is off on purpose — Avalonia resolves controls reflectively, and a trimmed build fails at window construction rather than at build time. Verified by CI on osx-arm64 (109M), win-x64 (102M) and linux-x64 (97M), each checked by reading the binary's header. **Note:** this ticket has no Outcome in `BACKLOG.md` (GAPS `absd-602-has-no-outcome`); it is judged against the board title and the PRD criterion. |

### ABSD-700 · Agent-assisted authoring

The engine for this epic exists and is tested; none of it has a window, and the
shell has no agent section to put one in.

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-701 Agent provider port and discovery | Partial | `AgentProvider` and `AgentProviderRegistry` discover installed CLIs; no key is ever held. **Remaining:** the surface that shows what was discovered. |
| ABSD-702 Run an agent CLI as a subprocess | Partial | `AgentRunner` spawns the CLI with a scoped environment (`AgentEnvironment`) and captures its output (`AgentRunnerTests`, 5). **Remaining:** cancellation from the UI, and the UI. |
| ABSD-703 Prompt surface scoped to the selection | Partial | `AgentAuthoringViewModel` scopes the prompt to the selected Epic, Issue or the whole backlog, and restates the three disclosure sentences — which binary runs, what it can read, what it may change — as the provider and scope change (`AgentAuthoringViewModelTests`, 28). **Remaining:** the view. |
| ABSD-704 Review an agent's backlog edit as a diff | Partial | `AgentEditSession` takes a byte snapshot, runs, and offers the result as a reviewable diff through `TextDiff` and `AgentEditReview`; rejection restores the exact bytes via `IAgentEditFileStore`, never a decode/encode round trip (`AgentEditSessionTests`, 14; `TextDiffTests`, 11). **Remaining:** the diff view. |
| ABSD-705 Plan the board consequences of an agent's draft | Partial | The draft parses through the same `BacklogParser` and plans through the same `PlanBuilder`, so consequences are computed by the paths that already have parity. The handoff is a request and nothing more: accepting an edit changes a file, and `AgentAuthoringViewModelTests` pins that asking for a Plan carries no approval and is refused outright before an accept. **Remaining:** the surface that shows them beside the diff. |
| ABSD-706 Record every agent run | Partial | `IAgentRunHistory` is implemented by the same `SqliteOperationHistory` and shares its connection. **Remaining:** reading those runs back in the UI. |

## Totals

| State | 2026-09-01 | 2026-09-05 |
| --- | --- | --- |
| Done | 5 | 23 |
| Partial | 20 | 21 |
| Not started | 19 | 0 |
| **Total** | **44** | **44** |

Counted from the rows above, not carried forward. The previous revision's
totals said 23 Done / 21 Partial while its rows summed to 22 / 22 — a row was
moved without the table following it, which is the third time this file's
totals have drifted from its own contents.

No ticket is Not started any more. That is a statement about coverage, not about
completeness: 21 rows are Partial, and several of them are Partial for reasons
no amount of code will fix on its own — a signature needs a credential, and the
write path needs a real board.

Of the 16 rows that reached Done, ten were already complete and waiting only on
the commit; six were recorded as Not started while fully built. Eleven rows moved
from Not started to Partial for the same reason in reverse — their engines were
written and their state never updated.

## What this means

**There is a user-runnable application, and it edits, plans and applies.**
`dotnet run --project src/AdoBoardSync.Desktop` opens a window that loads a Board
profile — from a `board.config.json`, or from details typed into the app, with a
starter backlog scaffolded when none exists — and shows each item's source beside
the exact HTML `import` would send. Typing changes the preview, the task list and
the markup problems live; Ctrl+S writes the edited blocks back atomically,
refuses to clobber an external edit, and keeps the selection where it was. The
import CSV can be written from the same window, byte-identical to `gen-csv`.

Writes go through the Plan/Apply gate: generating a Plan only reads, and Apply is
refused unless the user confirms, the backlog and board still match what the Plan
was computed against, and the editor holds nothing unsaved. All nine CLI commands
now plan, and the Audit view reports drift read-only.

Three honest limits. The connector's **write** path has still not run against a
real board — the one thing CI going green does not tell us. **The OS credential
store has no test** — it is written, wired and unproven. And the entire agent
epic has no surface at all: reachable from the test suite and not from the
application.

Two of those limits closed since the last revision. Sprints, assignees, history
and the profile switcher are now all in the nav rail and all driven by
`ShellInteractionTests` through the real window, so the "views no test can open"
gap is gone. Wiring them found two defects that no view-model test could have:
the shell was building its own `PlanViewModel`, so the history recorder and the
diagnostics redactor registered in the composition root never reached Apply; and
Apply's outcomes were recorded through a `Progress<T>` that delivered after the
run had been closed, so the store refused them.

## Next tickets

1. **Throwaway-project live writes** — now the largest untested surface in the
   product, and the one High gap that code alone cannot close.
2. **Build the ABSD-700 surface** — the agent epic's view model is tested and its
   engine is proven; only the view is missing, and `UiHarness` is now there to
   test it the moment it exists.
3. **View-level tests for the three views that landed before the harness did**
   (ABSD-401/402/508) — each is built and reachable, and none is opened by a
   test.

## Release slices

| Release | State |
| --- | --- |
| R1 Desktop foundation | Partial — host, shell, onboarding, file gateway, composition root, central build properties and the OS credential store (now tested, and resolved through the composition root) all done and committed; the profile registry still has no switcher view |
| R2 Backlog editor | Done except line-level gutter markers — parser, converter, validator, live-preview editing, atomic save and byte-identical CSV export are all committed and tested |
| R3 Plan and apply | Partial — all nine commands plan, Apply is gated and concurrent, the Audit view is read-only and hands off closure; the write path has never run against a live board |
| R4 Sprints, assignees and operations | Partial — every engine and view model built and tested, and the sprint, assignee and history views landed mid-audit with their nav sections live; all three are uncommitted and none has a view-level test |
| R5 Distribution | Partial — self-contained builds and per-user packages for all three platforms, built and checked by CI every run; unsigned, which is the whole of what remains |
| R6 Agent-assisted authoring | Partial — providers, runner, edit session, diff review and run history built and tested; no agent surface anywhere in the shell |
