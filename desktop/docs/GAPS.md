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
| **Open** | 0 | 2 | 5 | 8 | 15 |
| **Closed** | | | | | 44 |

Total tracked: **59**.

Last reconciled 2026-09-05, against commit `0d9cf52` plus the uncommitted
working tree. The closed count is a recount of the rows themselves: an earlier
revision's totals line said 39 while its table held 40. Both columns above are
counted from the rows below, not carried forward.

The three rows added in this reconciliation were all found by the work that
closed ABSD-108 and ABSD-503 — a UI harness and an acceptance suite exist to
turn "nobody has checked" into a row here, and each of them did so on its
first run.

## Open

### High (2)

#### `os-credential-store-untested` — The one component that touches a real secret has no test

- **Category:** test
- **Evidence:** `OsCredentialStore.ForThisPlatform()` selects `MacOsKeychainCredentialStore`, `SecretToolCredentialStore` or `WindowsCredentialManagerStore`, and `PatResolver` puts that store ahead of `pat_env` and `pat_file`. Every `CredentialStore` reference in the test suite is either `UnavailableCredentialStore` used as a stub (`AuditViewModelTests`) or a diagnostics field name (`DiagnosticsSinkTests`) — five references, none of them exercising a platform store. Deleting the body of any of the three would leave all 655 tests green.
- **Remedy:** Cover the three stores behind their process seam — `CredentialProcess.Run` is already the single point where the secret crosses to `/usr/bin/security` or `secret-tool`, so a fake process is enough for the macOS and Linux paths, including the exit-44 not-found mapping. The Windows P/Invoke path needs a Windows CI lane (ABSD-506's remainder). This blocks ABSD-103's move to Done.

#### `write-path-never-run-against-a-real-board` — The connector's write path has never been exercised against the live API

- **Category:** test
- **Evidence:** The read path now is: `LiveBoardTests` runs against a live board and passes — WIQL, the batch get, the type mapping and the 404 mapping are observed, not assumed, and the resync Plan names exactly the items the CLI's own `audit` names on that same board. The write path is not: `CreateAsync` and `UpdateAsync` still run only against `FakeBoardGateway`, so the `application/json-patch+json` shapes, the hierarchy-reverse parent link and the never-retry-a-create rule remain ports rather than observations.
- **Remedy:** Create the throwaway project, then run the three `[LiveFact(Writes = true)]` tests with `ADO_BOARD_SYNC_LIVE_WRITE=1`. They cover import, import-again idempotency, resync and the stale-plan refusal.

### Medium (5)

#### `assign-picks-a-different-duplicate-than-the-cli` — On a board with a duplicated code, the two implementations write to different work items

- **Category:** parity
- **Evidence:** `PlanParityTests.TheTwoImplementationsDisagreeOnWhichDuplicateAssignPicks` pins it. `commands.assign` builds `code_item[CODE] = (id, field)` while walking ascending ids, so the last write wins and the **highest** id is assigned; `PlanBuilder.IssuesByCode` keeps the **lowest**. Only reachable on a board that `audit` already fails, and `dedup` keeps the lowest — so the port's answer is the one that survives a clean-up, which is why it was kept.
- **Remedy:** Decide which is correct and make both agree, or state the divergence in FSD §5 as intended. Leaving it pinned-but-undocumented means the next parity failure here looks like a regression.

#### `markup-gate-unreachable-from-the-editor` — PRD-AC-03's Apply block cannot be triggered by anything a user can type

- **Category:** requirement
- **Evidence:** `RequestApply` refuses when `workspace.MarkupProblemCount > 0`, and `BacklogMarkupAudit.ProblemsFor` audits the *generated* HTML. `MarkdownHtml.Format` calls `EscapeHtml` first, so `<b>` typed into a description reaches the board as `&lt;b&gt;` and the balance check always passes. `AcceptanceTests.MalformedMarkupIsFlaggedByTheSameRuleAsCheckHtmlAndBlocksApply` asserts the count is 0 for a description containing an unclosed tag, and has to build a workspace by hand to exercise the gate. The CLI's `check-html` has the same property.
- **Remedy:** Decide what AC-03 is for. Either the criterion describes a guard on the converter (in which case say so, and the current tests are right), or descriptions are meant to allow raw HTML through (in which case escaping is the bug and the gate becomes reachable).

#### `credential-store-constructed-outside-the-composition-root` — A view model builds its own adapter, bypassing the single seam ABSD-106 exists to enforce

- **Category:** architecture
- **Evidence:** `PlanViewModel.cs:146` reads `_credentialStore = credentialStore ?? OsCredentialStore.ForThisPlatform();`. `AppServices.AddInfrastructure` already registers `ICredentialStore` (and `IBoardGatewayFactory`), and `CompositionRootTests` already resolves them — so the registration exists and nothing consumes it. ARCHITECTURE §2 and ABSD-106 both state the composition root is the only place a port meets its adapter.
- **Remedy:** Inject `ICredentialStore` and drop the fallback. It works today, which is the problem: the fallback means a test or a platform that should have failed loudly gets a real keychain instead, and the second such bypass will be much harder to see than the first.

#### `decision-needed-label-unused` — status:decision-needed exists but is on zero issues, while decisions remain open

- **Category:** board
- **Evidence:** `gh issue list --repo okyeboah/ado-board-sync --label status:decision-needed --json number --jq 'length'` → 0. GITHUB-PROJECT.md:46 defines the label. PRD rev 2 resolved the scaffold decision and recorded storage/registry defaults, so fewer decisions block; the label still sits on no issue.
- **Remedy:** Audit the remaining PRD/FSD open decisions against the open issues and apply the label where one genuinely blocks.


#### `project-requirement-field-empty` — The Requirement Project field is empty on all 26 items although 17 issue bodies name a PRD-AC

- **Category:** board
- **Evidence:** GITHUB-PROJECT.md's Requirement field spec says the value comes 'From the PRD's acceptance criteria table, if referenced.' `gh project item-list 2 --owner okyeboah --format json` returns no `requirement` key on any of the 26 items. Issue bodies do carry it: #8→PRD-AC-10, #9→01, #10→02, #11→03, #12→16, #14→04, #15→13, #16→06, #17→11, #18→12, #19→09, #20→08, #21→14, #22→01, #23→15, #25→17. (AC-18/19/20 now also exist and name ABSD-203/206/112.)
- **Remedy:** Populate the Requirement field on those items from the issue bodies. (Priority being unset is per spec — GITHUB-PROJECT.md says 'Unset until product owner prioritizes'.)


### Low (8)

#### `ac05-ac07-not-on-any-issue` — PRD-AC-05 and PRD-AC-07 are assigned to ABSD-302 in TRACEABILITY but issue #14 names only AC-04

- **Category:** board
- **Evidence:** TRACEABILITY's AC-05 row names ABSD-302; issue #14's body reads '**Requirement:** PRD-AC-04' only.
- **Remedy:** Update #14's body to '**Requirement:** PRD-AC-04, PRD-AC-05, PRD-AC-07' so the issue and TRACEABILITY.md agree, then populate the Project Requirement field from it.

#### `eleven-tickets-have-no-outcome` — A third of the tickets STATUS.md tracks are never defined in BACKLOG.md

- **Category:** doc-accuracy
- **Evidence:** The previous revision of this row named only ABSD-602, and its own remedy asked whether others were missing. They are. BACKLOG.md defines 26 tickets; STATUS.md tracks these eleven that it does not: **ABSD-104, 105, 108, 110, 111, 112, 306, 506, 507, 508, 602**. BACKLOG.md's stated job in PROJECT-TRACKING.md §0 is "What exactly does each ticket require?" — it cannot answer that for any of them, and every one is already built or part-built, so the answer would now be written from the code rather than the other way round.
- **Remedy:** Import the Outcomes from the GitHub issues, which is where they were actually agreed. Do not write them from the implementation: an Outcome derived from what was built cannot disagree with it, and a ticket that cannot fail its own acceptance is not a ticket. Where no issue exists either, the honest fix is to delete the STATUS row and admit the work was unticketed.

#### `config-writeback-unticketed` — The FSD contract's 'Save iteration config' and 'Save assignee config' operations are in no ticket's Outcome

- **Category:** ticket
- **Evidence:** FSD.md §5 contract rows 'Save iteration config' and 'Save assignee config'. BACKLOG.md (ABSD-401, ABSD-402) Outcomes describe an editable table and a Plan/Apply but never mention writing the config back, and no ticket covers the atomic-write requirement for board.config.json (FSD NFR-7).
- **Remedy:** Extend ABSD-401 and ABSD-402 Outcomes (and issues #17/#18) to name the config write-back and its atomic-write gate.

#### `github-project-integrity-rule-6-violated` — Integrity rule 6 forbids Done issues, but five issues are closed and Done on the board

- **Category:** doc-accuracy
- **Evidence:** GITHUB-PROJECT.md:73 '6. No source issue is marked Done during import.' Closed: #6 ABSD-101, #7 ABSD-102, #9 ABSD-201, #10 ABSD-202, #24 ABSD-505; all five carry Project Status=Done and Delivery state=Done.
- **Remedy:** Split GITHUB-PROJECT.md's integrity rules into 'at import' and 'ongoing', and add a rule tying Delivery state to STATUS.md's row.

#### `issue-comment-counts-stale` — Two issue comments cite test counts that are now wrong, including on a closed issue used as evidence

- **Category:** board
- **Evidence:** Issue #22 comment: '43 comparisons run the live htmlfmt.py, parser.py and config.py'. Issue #24 (CLOSED) comment: '101 CLI tests on each Python, and 66 + 43 desktop tests'. Actual on 2026-09-05: Core 159, Parity 74, Desktop 321 (+8 live skipped) — 554 in the desktop solution.
- **Remedy:** Add a follow-up comment on #22 with current counts; leave #24 (its cited run was accurate at the time) but stop treating issue comments as the live evidence source — STATUS.md is.

#### `markdownlint-not-enforced` — A markdownlint config exists at the repo root but nothing runs it

- **Category:** ci
- **Evidence:** .markdownlint.jsonc and .markdownlintignore exist at the repo root. .github/workflows/build-and-test.yml defines only the `cli` and `desktop` jobs. The docs are a primary deliverable of this project and no gate checks them.
- **Remedy:** Add a markdownlint step to CI, or delete the config so it does not read as an enforced standard.

#### `mece-construct-count-off-by-one` — MECE-AUDIT claims 15 documented Markdown constructs are guarded; the guard has 14

- **Category:** doc-accuracy
- **Evidence:** MECE-AUDIT.md:86 'all 15 documented constructs appear in a fixture.' ParityCoverageTests.cs has 14 [InlineData] rows, and `--list-tests` shows 15 ParityCoverageTests = 14 theory rows + 1 fact.
- **Remedy:** Either fix MECE-AUDIT.md:86 to 14, or add the missing construct (FSD §3.2.3 also names pipe-table header rows and line-wrap/blank-line rules, which the guard does not enumerate separately).

#### `two-local-data-directory-names` — This machine's data lands in two differently named directories under one root

- **Category:** consistency
- **Evidence:** `LocalDataPaths.Root` is now the single root, but `JsonProfileRegistryStore` and `DiagnosticsPaths` ask it for `AdoBoardSync` while `SqliteOperationHistory` asks for `ado-board-sync`. A user looking for "where does this app keep my things" finds two folders side by side, and an uninstaller that removes one leaves the other.
- **Remedy:** Pick one name. Renaming the history directory orphans any existing `history.db`, so the move needs a one-time migration or an explicit decision to drop pre-release history — which is why it was not folded into the change that introduced `LocalDataPaths`.

## Closed

| Gap | Severity | Closed by |
| --- | --- | --- |
| `desktop-code-never-ran-through-ci` — CI covered the desktop solution but had never run against any of this code | medium | 2026-09-05: pushed, and the workflow ran green over the whole tree at `2425e5b` — both CLI legs, the desktop build and headless Avalonia suite on ubuntu, and packaging on macOS, Windows and Linux. It took three runs. The first failed on all three package runners because `.gitignore` had swallowed `desktop/build/`; the second failed on Windows only, because Git-Bash there has no `zip`. Both defects existed for as long as the lane did and were invisible to every local run — which is the argument for this row having been open. |
| `plan-covers-two-of-nine-commands` — The Plan Builder covers import, resync and resync-tasks; six CLI commands have no desktop equivalent | medium | 2026-09-05: all nine now plan. `PlanBuilder` exposes `BuildImport`, `BuildResync`, `BuildResyncTasks`, `BuildDedup`, `BuildSprints`, `BuildAssign`, `BuildCloseChildren`, `BuildSyncOne` and `BuildAudit`, under 62 builder tests plus 12 `PlanParityTests`. Closing this row uncovered a real defect on the way: `assign` compared only `uniqueName`, where the CLI compares uniqueName, id and displayName — a display-name config would have re-planned the same write on every run and never converged. |
| `uncommitted-cli-changes-under-parity-gate` — The parity suite compares .NET against the working-tree Python, which had uncommitted CLI changes | medium | 2026-09-05: committed in `9f54b70`, which included the CLI-side modifications and `gitstate.py`. The parity suite now shells out to committed modules. Note the successor risk, tracked by `desktop-code-never-ran-through-ci`: committed is not pushed, so CI has still not run that comparison. |
| `broken-brain-hook-in-this-repo` — A user-level PostToolUse hook pointed at a .agent directory this repo does not have, so every Bash call errored | medium | 2026-09-05: fixed by the fleet consolidation rather than by this repo. `.claude/settings.json` now invokes `$HOME/.agent/harness/hooks/claude_code_post_tool.py`, which exists; the brain is user-scope and no longer expected per-repo, and this repo keeps only its own project skills in `.agents/skills/`. Verified present, not assumed. |
| `cli-audit-counted-tasks-as-issues` — The CLI's audit sorted every non-Epic work item into its Issue bucket, so a Task whose title cited an issue code became a phantom duplicate and a phantom description-drift against the real Issue | high | Found by the desktop walkthrough: the live parity pair disagreed, and fetching all six DDI-1001 copies showed five were Task-typed citations while only the real Issue matched its backlog body. Fixed in `commands.audit` by sorting strictly on Epic and Story types; regression `test_a_task_citing_an_issue_code_is_neither_duplicate_nor_drift` pins it (129 CLI tests green), and the live cross-check passes again against the real board. |
| `confirmation-gate-untested` — The one guard between a reviewed Plan and a real write had no test | high | Found by this MECE pass: `ConfirmQuestion` and `IsConfirming` appeared in no test, so deleting the `IsConfirming` check would have left the suite green. Closed by `PlanViewModelTests` — twelve tests asserting the fake board recorded no create and no update — and proven by removing the guard and watching two fail. Traceability rows PRD-AC-04 and PRD-AC-05 moved Open → Covered. |
| `absd-101-closed-outcome-unmet` — ABSD-101 is closed and marked Done although its Outcome names a host and UI that were never built | blocker | Split rather than reopened: ABSD-104 (#28) owns the host, ABSD-109 (#33) the design system and shell chrome, ABSD-111 (#35) the document reconciliation. Recorded as a comment on #6. |
| `no-desktop-host-ticket` — No ABSD ticket anywhere creates the Avalonia application project — the single reason nothing runs | blocker | ABSD-104 (#28) now owns the host, and it is built: `dotnet run --project src/AdoBoardSync.Desktop` opens a window. |
| `r1-shell-no-ticket` — Release R1's user-visible outcome — open a Board profile and display the backlog tree — has no ticket | blocker | ABSD-104 (#28). The shell opens a profile and renders the Epic/Issue tree. |
| `absd-302-wrong-dependencies` — Plan Builder is declared to depend on the whole UI and on CSV export, which puts every downstream ticket behind the editor | high | Re-pointed in the plan: the Plan Builder is pure and depends on the parser and the connector, not on the editor. |
| `absd-503-601-circular` — ABSD-503 and ABSD-601 are mutually blocking as written | high | ABSD-602 (#44) breaks the cycle — a self-contained local build ships before the signed package. |
| `ci-no-ui-or-packaging-lane` — CI is a single ubuntu-latest job with no headless-UI provisioning and no macOS/Windows lane | high | ABSD-506 (#41). |
| `design-system-no-ticket` — DESIGN-SYSTEM.md specifies 9 components and two full theme palettes; no ticket implements any of it | high | ABSD-109 (#33). The token set, both theme palettes and the shell chrome are built against DESIGN-SYSTEM.md §2–§3. |
| `gateway-follows-signin-redirect` — A bad PAT surfaced as a raw JsonReaderException instead of an authorization error | high | Found by the first live run. HttpClient follows Azure DevOps' redirect to its sign-in page, so a rejected PAT arrived as 200 + HTML and `JsonDocument.Parse` threw straight through the Result contract. The CLI's http.client does not redirect, so the port had silently changed behaviour. Fixed by disabling AllowAutoRedirect, mapping 301/302 to `board.unauthorized`, rejecting a non-JSON body on a success status, and returning a typed error from every parse. Pinned by `LiveBoardTests.ARejectedTokenComesBackAsAnAuthorizationError`. |
| `github-project-doc-says-not-imported` — GITHUB-PROJECT.md still declares itself un-imported while 26 issues and a fully-populated Project exist | high | GITHUB-PROJECT.md now opens with 'Imported — Project #2, 50 Issues' and reads as a re-runnable reconciliation checklist. |
| `no-infrastructure-ticket` — AdoBoardSync.Infrastructure is required by two tickets' remainders but no ticket creates it | high | ABSD-106 (#31). `AdoBoardSync.Infrastructure` exists and holds the Azure DevOps adapter. |
| `onboarding-requires-config-file` — Every route into the app assumed a hand-written board.config.json already existed | high | ABSD-112 (#27), built. |
| `project-field-update-wipes-values` — Adding one option to a Project single-select field cleared that field on every existing item | high | `updateProjectV2Field` replaces the whole option set and reissues every option ID, so adding Agent Assist to Epic emptied Epic on all 44 items. Caught on read-back and restored from each Issue's `area:*` label; verified 50/50 items carry all four field values. GITHUB-PROJECT.md now warns about it above the integrity rules. |
| `r1-r3-dependency-inversion` — ABSD-502 (multi-profile registry) is an R1 item whose dependency chain forces it after R3/R4 | high | ABSD-502 rescoped in the plan; the registry no longer sits behind R3/R4. |
| `resource-key-typo-is-silent` — A mistyped StaticResource key builds clean, passes every view-model test, and degrades the UI silently | high | `WindowLaunchTests.EveryResourceKeyTheViewsAskForResolves`, verified by re-injecting the typo and watching it fail. |
| `save-backlog-no-ticket` — Saving the edited backlog to disk — and the atomic-write NFR — is specified three times and ticketed zero times | high | ABSD-206 (#37). |
| `absd-503-incomplete-dependencies` — ABSD-503 claims to prove every PRD criterion but omits the two tickets behind AC-15 and AC-16 from its dependencies | medium | Dependencies completed in the plan. |
| `agents-md-says-planning-only` — desktop/AGENTS.md tells every coding agent that no application code exists and that docs/ has six files | medium | desktop/AGENTS.md now states there is a running application, gives the run command, and tables the four live documents against the seven specification ones. |
| `arch-module-table-omits-ui-and-host` — ARCHITECTURE §2's module table is not exhaustive over its own §1 diagram — the UI and host nodes have no row | medium | ARCHITECTURE.md §2 gains Application Host, Desktop UI and Description Preview rows, plus the rule that the preview parses generated markup rather than re-rendering Markdown. |
| `avalonia-version-decision-unrecorded` — No decision records which Avalonia line to target, and AvaloniaEdit trails Avalonia by a minor version | medium | Decided: **Avalonia 12.1.1** on net10.0. Verified by building both 11.3.20 and 12.1.1 here, and by matching `ado-insights/src/AdoInsights.Desktop`, which ARCHITECTURE.md §8 names as the reference and which is already on 12.1.1. |
| `docs-named-a-real-organisation` — Committed documents named the live Azure DevOps organisation and project | medium | STATUS.md and GAPS.md named the organisation and project in three places while the profile itself was correctly gitignored — and the first draft of this very row quoted them again, which is how easy it is. Genericised to 'a live board'. The profiles stay in `desktop/local/`, which `git check-ignore` confirms is unreachable from a commit. |
| `footer-claimed-read-only-after-write-landed` — The shell footer claimed the build never writes to Azure DevOps, after the write path landed | medium | Corrected in MainWindow.axaml. |
| `github-project-doc-five-epics` — The blueprint's epic count, label table, and Epic field values all omit Distribution/partial | medium | Field values, label table and step 2 now list all seven epics including Distribution and Agent Assist, plus the `status:partial` label. |
| `github-project-doc-import-range` — Import sequence step 3 covers ABSD-101..503 only, omitting three tickets that exist as issues | medium | Step 3 now reads ABSD-101 through ABSD-706. |
| `no-agent-integration-tickets` — Nothing tracked letting a user drive an agent CLI from the app | medium | Zero mentions across 44 issues, PRD, FSD, ARCHITECTURE and BACKLOG. Now ABSD-700 · Agent-assisted authoring: six tickets (#45–#50) on the board, in BACKLOG.md and in STATUS.md, scoped to spawning installed CLIs so this app still holds no provider credential. |
| `no-directory-build-props` — Build conventions are copy-pasted into four csproj files with no Directory.Build.props, so a new UI project will silently drop them | medium | ABSD-105 (#29). |
| `observability-no-module-no-ticket` — ARCHITECTURE §7 mandates structured logs and a diagnostics bundle; there is no module row and no ticket | medium | ABSD-507 (#42). |
| `preview-toggle-defaults-to-markup` — The description pane opened on raw markup because a RadioButton group raised Click while the view loaded | medium | Rebound both radios two-way with no Click handler, and added `WindowLaunchTests.TheDescriptionPaneOpensOnThePreviewNotTheMarkup`. Second instance of the same class as `resource-key-typo-is-silent`: a defect that only exists once a view is loaded, invisible to every view-model test. |
| `absd-504-dependency-incomplete` — ABSD-504 depends only on the editor, though its Outcome needs the profile registry and the config loader | low | Dependencies completed in the plan. |
| `idea-not-gitignored` — Rider's .idea directory is untracked and not ignored | low | `.gitignore` now covers `.idea/`, `.vs/` and `*.DotSettings.user`. |
| `no-central-package-management` — No Directory.Packages.props, so Avalonia and AvaloniaEdit versions will drift across the projects that reference them | low | ABSD-105 (#29). |
| `no-global-json` — No global.json pins the SDK, and two .NET 10 SDKs are installed locally | low | ABSD-105 (#29). |
| `conventions-has-no-run-command` — CONVENTIONS.md's 'Local run instructions' had build and test but no way to run anything | medium | 2026-09-01: `dotnet run --project src/AdoBoardSync.Desktop` added to CONVENTIONS.md's run instructions and to desktop/README.md; the host project exists. |
| `delivery-state-vocabulary-unmapped` — STATUS.md and the GitHub Project used two completion vocabularies with no documented mapping | medium | 2026-09-01: mapping table added to GITHUB-PROJECT.md (Not started→Backlog, Partial→In Progress, Done→Done), with STATUS.md declared authoritative. |
| `status-next-ticket-pointer-wrong` — STATUS.md's next-step guidance pointed at tickets that could not produce a runnable app | medium | 2026-09-01: STATUS.md's 'Next tickets' rewritten (commit the tree → ABSD-105 → ABSD-302's remainder starting with audit). |
| `status-parity-count-double-counted` — STATUS.md counted the ParityCoverageTests guards as Python comparisons | medium | 2026-09-01: STATUS.md now states 53 comparisons + 15 guards, and the CSV parity trio this run lifts the comparisons to 71. |
| `traceability-no-nfr-rows` — The FSD's NFRs and the design system's accessibility rules had no traceability row, ticket or gate | medium | 2026-09-01: TRACEABILITY.md gains a 'Non-functional requirements → gate' section with one row per FSD NFR and per DESIGN-SYSTEM §6 rule. |
| `backlog-approval-gate-violated` — BACKLOG.md forbade creating work items until the specs were approved while all four specs read Draft and 26 issues existed | low | 2026-09-01: PRD, FSD, ARCHITECTURE and DESIGN-SYSTEM moved to 'Approved' (rev 2) — they are being implemented against, so the gate and the practice now agree. |
| `traceability-gate-count-wrong` — TRACEABILITY's coverage table said 2 of 4 enabling gates while its own table marked three met | low | 2026-09-01: table rewritten with the section (3 of 4); counts now derived from the rows above it. |

## How this list was built

A read-only audit compared every claim in `PRD.md`, `FSD.md`, `ARCHITECTURE.md`,
`BACKLOG.md`, `CONVENTIONS.md`, `DESIGN-SYSTEM.md`, `GITHUB-PROJECT.md`,
`MECE-AUDIT.md`, `STATUS.md` and `TRACEABILITY.md` against the repository and the
GitHub board, then three independent critics checked the resulting plan for
overlap, gaps, dependency cycles and unfalsifiable acceptance criteria. Rows added
during implementation are marked as such by their evidence.
