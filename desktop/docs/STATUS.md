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
Partial, not Done — nobody else can run it yet. **Everything below marked
"delivered this run" is on the working tree and uncommitted**; the rows stay
Partial until the tree is committed and reviewed, however complete the evidence.

The board's own vocabulary (Backlog / Ready / In Progress / In Review / Blocked /
Done) maps onto this one as: Done → Done, In Review and In Progress → Partial,
everything else → Not started.

## Delivery tickets

### ABSD-100 · Product foundation

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-101 Solution and conventions | Partial | Modules, tests and documentation delivered. **Remaining:** nothing here — the host and UI half is split out to ABSD-104 and ABSD-109 rather than carried silently. |
| ABSD-102 Config loader and schema validation | Done | `BoardConfig` + `BoardConfigSchema`; 4 parity scenarios, 25 schema tests, and a guard that fails the build if the schema gains a key the validator does not know. |
| ABSD-103 Credential resolution | Partial | `PatResolver` resolves a session-entered token, then `pat_env`, then `pat_file`. **Remaining:** the operating-system credential store. |
| ABSD-104 Avalonia desktop host | Partial | `AdoBoardSync.Desktop` builds an executable; `dotnet run` opens a window that loads a profile and renders the tree. Launch gate + per-section render tests pass. **Remaining:** committed and reviewed. |
| ABSD-105 Central build properties and package versions | Not started | — |
| ABSD-106 Infrastructure and gateways | Partial | `AdoBoardSync.Infrastructure` exists and holds the Azure DevOps adapter behind `IBoardGateway`. **Remaining:** the backlog file gateway — reading the backlog is still a static `File.ReadAllText` inside the UI project. |
| ABSD-107 Profile loading off the UI thread | Partial | Delivered this run: the save path runs off the UI thread (`Task.Run` around the file write and re-parse). **Remaining:** profile loading itself is still synchronous on the UI thread. |
| ABSD-108 Headless UI test harness | Partial | Headless platform boots; the launch gate resolves every XAML resource key and all six nav sections render. **Remaining:** a general harness for interaction-level view tests. |
| ABSD-109 Design system and shell chrome | Partial | Both theme palettes from DESIGN-SYSTEM.md §2, the spacing/type/radius scale, and the nav-rail shell — verified in light and dark. **Remaining:** the documented contrast pass and the §6 accessibility rules. |
| ABSD-110 Credential status and board-action gating | Not started | — |
| ABSD-111 Reconcile the documents and the board | Partial | Delivered this run: PRD/FSD/ARCHITECTURE/DESIGN-SYSTEM moved to Approved (rev 2) and re-grounded in the code; PROJECT-TRACKING.md added; STATUS/TRACEABILITY/GAPS/README reconciled in the same change set. **Remaining:** committed, and a pass over the GitHub issues' bodies. |
| ABSD-112 Onboarding without a config file | Partial | Two equal routes in; the form composes the same JSON the config file holds. Delivered this run: a failed config open on the first-run screen is reported inline with a typed code instead of replacing onboarding with an error page; and the form route scaffolds a working starter backlog (1 epic, 1 issue, 2 tasks, a table) with the profile's exact prefix when the backlog file does not exist — opt-out, never overwriting an existing file. **Remaining:** committed and reviewed. |

### ABSD-200 · Backlog engine

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-201 Backlog parser | Done | `BacklogParser`; parity against `parser.py` including custom `epic_heading_regex` and relative-path scenarios. Now also carries each item's description-block range (start/end lines) — editor metadata the parity comparison does not count, pinned by `BacklogParserTests`. |
| ABSD-202 Markdown-to-HTML conversion | Done | `MarkdownHtml`; parity for HTML, plain, inline and norm against `htmlfmt.py`. |
| ABSD-203 Split-pane editor with live preview | Partial | Delivered this run: the source pane is editable. The buffer recomputes the preview, generated HTML, task list and markup problems per keystroke from the same Core functions; Save splices every dirty buffer back at the parser's own ranges, last-to-first, preserving the file's EOL style, trailing newline and the blank separators between items; the tree rebuilds with the selection kept on the edited item; and Plan/Apply are refused while edits are unsaved (`backlog.unsaved`). 19 new view-model/shell tests this run. **Remaining:** committed; line-level gutter markers inside one description. |
| ABSD-204 CSV export | Partial | Delivered this run: `ImportCsv` ports `csvio.py` plus the Python csv dialect (minimal quoting, doubled quotes, CRLF records, Epics in Title 1); `ImportCsvParityTests` compare all backlog fixtures byte-for-byte against the live Python (71 parity tests). 8 Core tests pin the rules. **Remaining:** committed. |
| ABSD-205 Description preview rendering | Done | `PreviewDocument` parses the generated markup — not the Markdown source — so the preview cannot disagree with what would be written. Paragraphs, nested bullets, rules, tables and inline bold/italic/code, covered by 11 tests including one asserting no text is lost. `HtmlLayout` indents the markup view for reading, with 14 tests pinning that it changes nothing but whitespace. |
| ABSD-206 Atomic backlog save | Partial | Delivered this run: `BacklogWorkspace.SaveMarkdown` writes temp-then-rename in the destination directory, refuses `backlog.changed_on_disk` when the file moved since it was opened (the external edit survives; the buffer is preserved), and returns the re-parsed workspace. Save runs off the UI thread; Ctrl+S is bound. **Remaining:** committed. |
| ABSD-207 Import CSV from the open profile | Partial | Delivered this run: `ExportCsvTo` writes the CSV to a picked path from the open workspace — no credential, no network — from a rail button gated on `HasProfile`. **Remaining:** committed. |

### ABSD-300 · Plan, apply & audit

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-301 Azure DevOps connector | Partial | `AzureDevOpsGateway` ports `client.py`'s WIQL, batch get, create, patch, delete and retry contract — including never retrying a create and always retrying a delete of an identified item. One read serves all three levels. Its **read** path is proven against a live board by `LiveBoardTests`. **Remaining:** iterations and teams; and the write paths still have not run against a real board. |
| ABSD-302 Plan Builder | Partial | Pure `BuildImport`, `BuildResync` and `BuildResyncTasks`, tested against a fake board (import/resync also cross-checked live against the CLI's `audit`). Resync-tasks carries the CLI's exact comparison rules. The gateway's `DeleteAsync` gives the app its first delete path. **Remaining:** `dedup`, `sprints`, `assign`, `close-children`, `audit`, `sync-one`. |
| ABSD-303 Apply Executor with stale-plan guard | Partial | Applies exactly the reviewed rows; refuses on `plan.stale_backlog` or `plan.stale_board` before the first write. Independent rows are written concurrently — bounded fan-out, ordered after dependency waves — and reported outcomes keep the Plan's row order. Delivered this run: Apply now also refuses while unsaved editor edits exist. **Remaining:** never run against a live board. |
| ABSD-304 Audit | Not started | — |
| ABSD-305 Plan review and Apply confirmation surface | Partial | Command selector, Plan rows badged with glyph and word, and a confirmation restating counts before any write — covered by view-model tests asserting against what reached the fake board. **Remaining:** committed and reviewed. |
| ABSD-306 Audit view and Close-children handoff | Not started | — |

### ABSD-400 · Sprint, ownership & closure planning

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-401 Sprint planning view | Not started | — |
| ABSD-402 Assignee planning view | Not started | — |
| ABSD-403 Close-children review | Not started | — |

### ABSD-500 · Operations and delivery

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-501 Operation history store | Not started | — |
| ABSD-502 Multi-profile registry | Not started | — |
| ABSD-503 End-to-end parity and acceptance suite | Partial | 53 parity comparisons against the live Python modules plus 15 `ParityCoverageTests` guards (68), plus the CSV parity trio this run (71 comparisons total), plus `LiveBoardTests` — gated behind `ADO_BOARD_SYNC_LIVE_CONFIG` (writes behind `ADO_BOARD_SYNC_LIVE_WRITE`). **Remaining:** the acceptance half — proving each PRD criterion against a fixture organisation. |
| ABSD-504 External change detection | Partial | Delivered this run: the save-side half — save refuses to overwrite an external change and names Reload. **Remaining:** proactive watching (marking the profile stale the moment the file moves, before any save attempt). |
| ABSD-505 Continuous integration | Done | `.github/workflows/build-and-test.yml`; green on `main`. |
| ABSD-506 Extend CI to the desktop application | Partial | `build-and-test.yml` restores, builds and tests the whole `.slnx` in Release on ubuntu, live tests skipping without the env var. **Remaining:** a packaging lane; and CI has not yet run against this code because none of it is committed. |
| ABSD-507 Structured diagnostics | Not started | — |
| ABSD-508 Operation history timeline | Not started | — |

### ABSD-600 · Distribution

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-601 Installable desktop package | Not started | — |
| ABSD-602 Self-contained local build | Not started | — |

### ABSD-700 · Agent-assisted authoring

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-701–706 | Not started | Six tickets scoped; provider model decided (spawn installed CLIs, no keys held). |

## Totals

| State | Count |
| --- | --- |
| Done | 5 |
| Partial | 20 |
| Not started | 19 |
| **Total** | **44** |

(The previous revision's totals said 13 Partial / 26 Not started, but its own
rows summed to 14 / 25 — recounted and corrected here.)

## What this means

**There is a user-runnable application, and it now edits.** `dotnet run
--project src/AdoBoardSync.Desktop` opens a window that loads a Board profile —
from a `board.config.json`, or from details typed into the app, with a starter
backlog scaffolded when none exists yet — and shows each item's source beside
the exact HTML `import` would send. Selecting an item and typing changes the
preview, the task list, and the markup problems as you type; Ctrl+S writes the
edited blocks back atomically, refuses to clobber an external edit, and keeps
the selection where it was. The import CSV can be written from the same window,
byte-identical to `gen-csv`.

It can also write to the board. Import and resync go through the Plan/Apply
gate: generating a Plan only reads, and Apply is refused unless the user
confirms, the backlog and board still match what the Plan was computed against,
and the editor holds nothing unsaved.

The connector's **read** path has run against a live board (`LiveBoardTests`),
and the resync Plan names exactly the items the CLI's own `audit` names on the
same board. Two honest limits remain. The **write** path has still not run
against a real board — the three writing live tests wait on a throwaway project
(GAPS `write-path-never-run-against-a-real-board`). And the Plan Builder covers
three of the CLI's nine commands; six remain (audit, dedup, sprints, assign,
close-children, sync-one).

Everything this run delivered is on the working tree, uncommitted — so every
row it moved says Partial, per the rule at the top. The suggested commit split
is in PROJECT-TRACKING.md §7.

## Next tickets

1. **Commit the tree** (ABSD-104/109/112/203/204/205/206/207/305/111's shared
   remainder). Nothing else can flip to Done while the work exists only here.
2. **ABSD-105** — central build properties and CPM; four csproj files still
   repeat the conventions.
3. **ABSD-302's remainder** — the six unplanned commands, `audit` first (it is
   read-only and unlocks ABSD-304).

## Release slices

| Release | State |
| --- | --- |
| R1 Desktop foundation | Partial — host, shell, onboarding (now with scaffold and typed import errors) done; no profile registry, no OS credential store |
| R2 Backlog editor | Partial — parser, converter, validator, editing with live preview, atomic save, and CSV export done, uncommitted; no line-level gutter markers |
| R3 Plan and apply | Partial — import, resync and resync-tasks planned, reviewed and applied (with concurrent apply and first deletes); six commands remain |
| R4 Sprints, assignees and operations | Not started |
| R5 Distribution | Not started |
| R6 Agent-assisted authoring | Not started — six tickets scoped; provider model decided (spawn installed CLIs, no keys held) |
