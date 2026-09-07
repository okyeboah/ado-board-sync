# Requirements Traceability

**Status:** Live — update this file whenever an acceptance criterion, ticket, or test changes.

This is the gate `docs/CONVENTIONS.md` rule 6 refers to: a delivery ticket is Done
only when its linked PRD acceptance criterion has a passing test. Every row below
must reach **Covered** before the release slice that contains it can close.

## Legend

| State | Meaning |
| --- | --- |
| Covered | An automated test asserts the criterion today. |
| Partial | Some of the criterion is asserted; the remainder needs code that does not exist yet. |
| Open | No test asserts it yet. |

## Acceptance criteria → specification → ticket → test

| Criterion | FSD | Ticket | Test | State |
| --- | --- | --- | --- | --- |
| PRD-AC-01 parse tree matches `gen-csv` | §3.2 | ABSD-201 | `BacklogParserParityTests.Parse_MatchesThePythonImplementation`, `.TasksByCode_MatchesThePythonImplementation` | Covered |
| PRD-AC-02 preview HTML matches the CLI | §3.2 | ABSD-202, ABSD-205 | `MarkdownHtmlParityTests.ToHtml_MatchesThePythonImplementation` over every fixture directory; `PreviewDocumentTests.ThePreviewLosesNoTextFromTheGeneratedMarkup`; `HtmlLayoutTests.FormattingChangesNothingButWhitespace`; for an edited buffer, `BacklogNodeViewModelTests.EditingTheSourceRecomputesEveryDerivedView` pins the HTML to the same converter | Covered |
| PRD-AC-03 malformed markup blocks Apply | §3.2 | ABSD-203, ABSD-305 | `HtmlBalanceTests.*`, `MarkdownHtmlParityTests.Problems_MatchThePythonImplementation`, `PlanViewModelTests.AConfirmationIsNeverOfferedWhileBacklogMarkupIsMalformed`, `.TheApplyPathRefusesAgainEvenIfAConfirmationWasObtained`, `.ResolvingTheMarkupUnblocksApply`; the buffer's problems recompute live through the same `BacklogMarkupAudit` (`BacklogNodeViewModelTests`); `AcceptanceTests.MalformedMarkupIsFlaggedByTheSameRuleAsCheckHtmlAndBlocksApply` checks the rule itself against the CLI's and drives the gate from a workspace built by hand | Partial — and now understood rather than merely observed. No authored input can reach the gate: the converter escapes raw angle brackets, so `<b>` in a description becomes text and the balance check always passes. The audit is a guard on the converter, not on what a user can type. Tracked as `markup-gate-unreachable-from-the-editor` in GAPS, because it is a question about what the criterion means and not a missing test |
| PRD-AC-04 plan counts shown before any write | §3.3 | ABSD-302, ABSD-305 | `PlanViewModelTests.TheConfirmationRestatesWhatWouldBeCreated`, `.TheConfirmationRestatesWhatWouldBeUpdated`, `.TheConfirmationAgreesInNumber` | Covered |
| PRD-AC-05 no mutation before Apply | §3.3.5 | ABSD-302, ABSD-303, ABSD-305 | `PlanViewModelTests.GeneratingAPlanWritesNothing`, `.AskingToApplyWritesNothingUntilItIsConfirmed`, `.ApplyingWithoutConfirmingWritesNothing`, `.CancellingClosesTheConfirmationAndWritesNothing` — each asserting the fake board recorded no create and no update | Covered |
| PRD-AC-06 audit reports hierarchy drift | §3.5 | ABSD-304 | `AcceptanceTests.AuditNamesTheDoneParentAndItsOpenDescendant` — the Done parent is named by board id and the open descendant is carried on the finding, so close-children can plan exactly it; `PlanBuilderAuditTests` (12) cover the report itself, including the cycle-safe ancestor walk; `AuditViewModelTests` (12) pin that the surface authorises nothing | Covered |
| PRD-AC-07 desktop and CLI plans are identical | §3.3.3 | ABSD-302, ABSD-503 | `PlanParityTests` (12) — each command runs through the real CLI against its own `FakeClient` and through the port against `FakeBoardGateway`, and the **board each leaves behind** is compared field by field: dedup, close-children ×2, assign ×3, sprints, resync, resync-tasks and the audit verdict clean and drifted. `LiveBoardTests.TheResyncPlanNamesTheSameDescriptionsTheCliAuditNames` adds the live cross-check. `AcceptanceTests.TheDesktopPlanAndTheCliPlanAgree` fails if that gate shrinks | Covered — with one divergence pinned rather than hidden: on a board carrying a duplicated code, `assign` writes to the highest id in the CLI and the lowest in the port (`TheTwoImplementationsDisagreeOnWhichDuplicateAssignPicks`, and a GAPS row) |
| — | — | ABSD-503 | `AcceptanceTests` carries one test per criterion, tagged with its id; `EveryAcceptanceCriterionInThePrdHasATest` reads `PRD.md` and fails when a criterion has no test, or when a test claims one the PRD no longer has | Covered |
| PRD-AC-08 operation history shows what changed | §3.10 | ABSD-501 | `AcceptanceTests.AppliedChangesAppearInTheHistoryWithEveryItemsOutcome` — applies through the gate into a real SQLite store, then reads the timeline back and expands the run, asserting one outcome per applied row. It found the defect it exists to catch: outcomes were recorded from inside a `Progress<T>` callback, which the dispatcher delivers *after* Apply returns, so the run was already closed and the store refused every row. `OperationHistoryTests` (15) and `HistoryTimelineTests` (15) cover the store and the surface | Covered |
| PRD-AC-09 close-children assignee inheritance | §3.6 | ABSD-403 | `AcceptanceTests.ClosingChildrenLeavesAnAlreadyAssignedItemAloneAndGivesTheRestTheAncestorsOwner` — an owned descendant keeps its owner, an unowned one inherits the Done ancestor's, matching `--assign-from-parent`; `PlanParityTests` compares the resulting board against the CLI's for the same command | Covered |
| PRD-AC-10 no PAT blocks board actions | §3.1 | ABSD-103 | `PatResolverTests.*` — source order, whitespace handling, `DescribeSources` leaks no token; `AcceptanceTests.WithNoResolvableTokenEveryBoardActionIsBlockedAndTheSourcesAreNamed` asserts the board was never even read (`ReadCount` is 0, not merely "no writes") and that the status names the sources checked | Covered — the three platform credential stores are still untested, which is ABSD-103's own gap rather than this criterion's |
| PRD-AC-11 sprint plan assigns iterations | §3.7 | ABSD-401 | `AcceptanceTests.ASprintPlanPutsEveryListedIssueAndItsTasksOnTheEarliestSprintThatNamesIt` — a code listed in two sprints lands in the first, and the Issue's child Tasks follow it; `PlanBuilderLifecycleTests` cover the `--no-tasks` and iteration-node branches; `PlanParityTests` compares the resulting board against the CLI's | Covered |
| PRD-AC-12 assignee plan sets owners | §3.8 | ABSD-402 | `AcceptanceTests.AnAssigneePlanOwnsEveryListedIssueAndItsTasksAndReportsSettledOnesUnchanged` — the first-listed identity wins a shared code, Tasks follow their Issue, and an already-correct item is reported **Unchanged** and never written. That last clause was the criterion's own doing: settled items were previously dropped from the plan entirely | Covered |
| PRD-AC-13 a stale plan is refused | §3.4.1 | ABSD-303 | `ApplyExecutorTests.ApplyIsRefusedWhenTheBacklogChangedAfterTheReview`, `.ApplyIsRefusedWhenTheBoardChangedAfterTheReview`, `PlanViewModelTests.AConfirmedApplyIsStillRefusedWhenTheBoardMoved`, `LiveBoardTests.ApplyIsRefusedWhenTheBacklogMovedAfterThePlanWasBuilt` — each asserting nothing was written before the refusal | Covered |
| PRD-AC-14 profiles do not mix | §3.11 | ABSD-502 | `AcceptanceTests.SwitchingProfileMixesNothingFromThePreviousOne` — after opening a second profile, none of the first's backlog items survive, the code prefix follows, the Plan is discarded, and the sprint and assignee tables are the second profile's; `HistoryTimelineTests` scope every run to the active profile key; `ProfileSwitchingTests` (25) cover the registry, the file it persists to and the switcher ring | Covered |
| PRD-AC-15 external change forces a reload | §3.11.3 | ABSD-504 | `MainWindowViewModelTests.ASaveRefusesToOverwriteAnExternalChange` asserts the refusal, that the external edit survives byte-for-byte, and that the buffer is preserved | Partial — the save-side guard is tested; the proactive watcher that marks a profile stale before any save attempt does not exist yet |
| PRD-AC-16 CSV matches `gen-csv` | §3.9 | ABSD-204, ABSD-207 | `ImportCsvParityTests.Csv_MatchesThePythonImplementation` over every backlog fixture — byte-for-byte against the live Python csv writer; `ImportCsvTests.*` pin quoting/type-name rules; `MainWindowViewModelTests.ExportCsvWritesTheGenCsvBytesWithoutACredential` covers the app path | Covered |
| PRD-AC-17 released package runs without a toolchain | — | ABSD-601 | `AcceptanceTests.ThePackagingScriptsProduceASelfContainedBuildThatNeedsNoToolchain` pins what the criterion turns on — that `publish.sh` publishes `--self-contained` with `PublishSingleFile`, and that `package.sh` stamps unsigned output as such rather than letting it look installable | Partial — no in-process test can install a package. The criterion itself was checked empirically once: the published 109 MB binary was run under `env -i HOME=… PATH=/usr/bin:/bin` with no .NET toolchain reachable and started cleanly. That is a manual step, and signing remains unproven for want of certificates |
| PRD-AC-18 edit, preview, and save round trip | §3.2 | ABSD-203, ABSD-206 | `BacklogNodeViewModelTests.EditingTheSourceRecomputesEveryDerivedView`, `.ABufferEqualToTheFileIsNotDirty`, `.DiscardEditsRestoresTheParsedTextExactly`, `.AnEpicBufferIsNeverMinedForTasks`; `MainWindowViewModelTests.AnEditMarksTheProfileUnsavedAndSaveWritesTheFile` (file holds exactly the edited blocks, task count updates), `.ASaveReParsesAndKeepsTheSelectionOnTheSameItem`, `.TwoEditedItemsAreSplicedBackTogether`; Core: `BacklogSplicerTests.*` (9 tests — separators, EOL style, multi-edit ordering, round trip over the standard fixture), `BacklogParserTests.EveryItemCarriesTheRangeOfItsDescriptionBlock`, `.TheDescriptionRangeCoversExactlyTheDescriptionLines` | Covered |
| PRD-AC-19 unsaved edits gate; save refuses external change | §3.2, §3.3, §3.4 | ABSD-203, ABSD-303, ABSD-305, ABSD-504 | `PlanViewModelTests.APlanIsRefusedWhileTheBacklogHasUnsavedEdits` (gateway never built), `.AnApplyIsRefusedWhileTheBacklogHasUnsavedEdits` (even after confirmation); `MainWindowViewModelTests.ThePlanGateRefusesWhileTheBufferIsDirty` (shell wiring), `.ASaveRefusesToOverwriteAnExternalChange` | Covered |
| PRD-AC-20 onboarding scaffold and typed import errors | §3.1 | ABSD-112 | `OnboardingViewModelTests.ABacklogFileThatDoesNotExistIsScaffoldedIntoAWorkingBacklog` (parses, zero markup problems), `.AnExistingBacklogIsNeverOverwrittenByTheScaffold`, `.ClearingTheScaffoldOptionLeavesAMissingFileAnError`, `.TheScaffoldOptionAppearsOnlyWhenTheFileIsMissing`, `.TheStarterContentParsesWithAnyPrefix`; `MainWindowViewModelTests.OpeningFromOnboardingWithABadConfigStaysOnTheOnboardingScreen` | Covered |

## Enabling tickets

These carry no PRD acceptance criterion because they deliver no user-visible
behaviour on their own. Their gate is named explicitly so "no AC" never means
"no standard".

| Ticket | Gate |
| --- | --- |
| ABSD-101 Solution and conventions | `dotnet build` succeeds with `TreatWarningsAsErrors` on every project. **Met.** |
| ABSD-102 Config loader and schema validation | Two gates, because the ticket names two things: `BoardConfigParityTests.*` for resolution matching `config.py`, and `BoardConfigSchemaTests.*` for the constraints in `board.config.schema.json` that a deserialize does not enforce. **Both met.** |
| ABSD-301 Azure DevOps connector | Contract tests against a fixture connector; no live Azure DevOps call in any test. **Partially met:** the fake exercises create, update, delete, and the batched read that carries each item's parent id; the live write tests remain gated behind `ADO_BOARD_SYNC_LIVE_WRITE`. |
| ABSD-505 Continuous integration | `.github/workflows/build-and-test.yml` runs the CLI suite and the .NET build, unit, and parity suites on a clean checkout. **Met.** |

An earlier revision of this file marked ABSD-102 **Met** on the parity tests
alone, while the schema validation its Outcome names did not exist. A gate that
covers part of a ticket and is recorded as covering all of it is worse than no
gate, so a ticket whose Outcome names more than one deliverable now lists one
gate per deliverable. `STATUS.md` is the per-ticket view.

## Non-functional requirements → gate

The FSD's NFRs and the design system's accessibility rules carry no PRD-AC, so
they get their own rows — a requirement with neither a criterion nor a gate is
not ready to start (CONVENTIONS rule 7).

| Requirement | Owning ticket | Test / gate | State |
| --- | --- | --- | --- |
| NFR-1 parity for parser/conversion/config/CSV/commands | ABSD-503 | The whole parity project runs on every build; `ParityCoverageTests` fails on an unguarded schema key or Markdown construct | Met for what exists; grows with each command |
| NFR-2 150ms preview re-render at 500 items | ABSD-203 | No perf test asserts it; the buffer recompute path is the same Core functions at fixture scale. `tests/test_performance.py` pins the CLI side | Open |
| NFR-3 plan generation within CLI dry-run bounds | ABSD-302 | `tests/test_performance.py` pins the CLI's request counts; no desktop-side bound test | Open |
| NFR-4 the PAT never reaches logs/exports/history/backlog | ABSD-103, ABSD-501 | `PatResolverTests.DescribeSourcesLeaksNoToken`; no store exists yet to leak from | Partial — re-audit when the history store lands |
| NFR-5 CLI retry/backoff semantics in Apply | ABSD-301 | `LiveBoardTests` + `test_client.py` pin the CLI; the gateway ports the contract, create never retried | Partial — write path not yet run against a live board |
| NFR-6 structured audit events for Plan/Apply | ABSD-507 | `OperationsWiringTests.ThePlanGateEmitsThePlanAndApplyEventsArchitectureSectionSevenAsksFor` and `.SavingTheBacklogRecordsThatAFileReachedDisk` — the gate emits `PlanGenerated`/`ApplyStarted`/`ApplyFinished`/`OperationFailed`, `ProfileLoader` emits `FileWritten`, and the same test asserts no event carries an item's title. `.TheConfigTablesReloadThroughTheContainersProfileLoaderNotOneTheyBuild` holds the other half: both config tables reload through the registered loader, so a save from either is logged — each built its own, wired to a null sink, until 2026-09-06 | Covered |
| NFR-7 atomic writes to backlog and config | ABSD-206, ABSD-401, ABSD-402 | Backlog: `FileSystemBacklogFileStore.WriteAtomic` writes a temp file in the destination directory, flushes it to the device, then renames; `BacklogFileStoreTests.AnAbortBetweenTheTemporaryWriteAndTheRenameLeavesTheOriginalIntact`. Config: `BoardConfigWriter` does the same temp-then-rename and validates against the schema before the rename, and now flushes to the device before the rename as well, under 14 `BoardConfigWriterTests` including `NoTemporaryFileIsLeftBesideTheConfig`, `AConfigThatIsNotJsonIsReportedRatherThanOverwritten` and `AnAccentedIdentityRoundTripsThroughTheDurableWrite` — the last guarding the encoding, which is what changed hands when `File.WriteAllText` gave way to an explicit encode-and-stream; checked by mutation | Covered |
| NFR-8 stale-plan refusal; save refuses external change | ABSD-303, ABSD-504 | AC-13 and AC-15 rows | Covered |
| DS §6.1 keyboard-reachable editor and plan flow | ABSD-109 | `Ctrl+S` bound in XAML; no traversal test | Partial |
| DS §6.3/6.6 no colour-alone states | ABSD-109 | Unsaved chips and plan badges carry glyph + word by construction (`BacklogNodeViewModelTests` pins the summaries); no automated accessibility audit | Partial |
| DS §6.4 reduced-motion respected | ABSD-109 | No transitions exist to suppress; nothing to test | Met by absence — revisit when motion is added |

## Coverage today

| Measure | Value |
| --- | --- |
| Criteria total | 20 |
| Covered | 17 |
| Partial | 3 |
| Open | 0 |
| Enabling gates met | 3 of 4 |

NFR-6 moved Open → Covered on 2026-09-06. Its events had been declared and never
emitted, so the missing gate and a missing feature looked identical here.

NFR-7 moved Partial → Covered on 2026-09-07. The config writer now flushes to the
device before its rename, which the backlog store had done all along; until then
a save survived a crash but not a power cut, and the row said so rather than
claiming the requirement whole.

No criterion is Open. Eight moved to Covered in one pass, because ABSD-503's
acceptance suite was built to close exactly this table: one test per criterion,
tagged with its id, and a guard that fails when the PRD gains a criterion no
test names. That guard was verified by adding a PRD-AC-21 row and watching it
fail — an unfalsifiable coverage check would have been worse than none.

The three that remain Partial are honest about *why*, and none of them is
waiting on a test that could simply be written:

- **AC-03** cannot be reached from the editor at all — the converter escapes raw
  angle brackets, so no authored description produces unbalanced HTML. What the
  criterion is for is a question for the PRD, tracked in GAPS.
- **AC-15** has its save-side guard: a save refuses to overwrite an external
  change and the buffer survives. The proactive watcher that marks a profile
  stale *before* a save is attempted does not exist (ABSD-504).
- **AC-17** needs an installed package on a clean machine. The published binary
  was run once under `env -i` with no toolchain and started; signing is
  unproven for want of certificates.

The Covered rows were built parity-critical first — parser, conversion,
validator, CSV export, stale-plan refusal — then editing and onboarding
(AC-18/19/20), then the plan-computation gate (AC-07) that let the remaining
command criteria be asserted against the CLI's own answers rather than against
this implementation's.
