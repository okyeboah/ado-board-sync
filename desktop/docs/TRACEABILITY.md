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
| PRD-AC-03 malformed markup blocks Apply | §3.2 | ABSD-203, ABSD-305 | `HtmlBalanceTests.*`, `MarkdownHtmlParityTests.Problems_MatchThePythonImplementation`, `PlanViewModelTests.AConfirmationIsNeverOfferedWhileBacklogMarkupIsMalformed`, `.TheApplyPathRefusesAgainEvenIfAConfirmationWasObtained`, `.ResolvingTheMarkupUnblocksApply`; the buffer's problems recompute live through the same `BacklogMarkupAudit` (`BacklogNodeViewModelTests`) | Partial — the Apply gate is enforced from one audited count, and the buffer recomputes through the same audit; no test feeds a *malformed* edit live because no natural authored input produces unbalanced HTML (the converter escapes and protects code spans — the audit is defence-in-depth), so the live-flag path is pinned only via well-formed recomputation |
| PRD-AC-04 plan counts shown before any write | §3.3 | ABSD-302, ABSD-305 | `PlanViewModelTests.TheConfirmationRestatesWhatWouldBeCreated`, `.TheConfirmationRestatesWhatWouldBeUpdated`, `.TheConfirmationAgreesInNumber` | Covered |
| PRD-AC-05 no mutation before Apply | §3.3.5 | ABSD-302, ABSD-303, ABSD-305 | `PlanViewModelTests.GeneratingAPlanWritesNothing`, `.AskingToApplyWritesNothingUntilItIsConfirmed`, `.ApplyingWithoutConfirmingWritesNothing`, `.CancellingClosesTheConfirmationAndWritesNothing` — each asserting the fake board recorded no create and no update | Covered |
| PRD-AC-06 audit reports hierarchy drift | §3.5 | ABSD-304 | — | Open |
| PRD-AC-07 desktop and CLI plans are identical | §3.3.3 | ABSD-302, ABSD-503 | `LiveBoardTests.TheResyncPlanNamesTheSameDescriptionsTheCliAuditNames` — both run against the same live board and the same backlog, and must name the same items | Partial — resync is cross-checked against the CLI's `audit` on a live board; resync-tasks pins the CLI's `resync_tasks` rules in `PlanBuilderTests.ResyncTasks*`; the six remaining commands have nothing to compare |
| — | — | ABSD-503 | The end-to-end suite proves every criterion above against a fixture organisation; it closes only when no row is Open | Open |
| PRD-AC-08 operation history shows what changed | §3.10 | ABSD-501 | — | Open |
| PRD-AC-09 close-children assignee inheritance | §3.6 | ABSD-403 | — | Open |
| PRD-AC-10 no PAT blocks board actions | §3.1 | ABSD-103 | `PatResolverTests.*` — source order, whitespace handling, `DescribeSources` leaks no token | Partial — resolution is tested; the gate that blocks board actions does not exist yet |
| PRD-AC-11 sprint plan assigns iterations | §3.7 | ABSD-401 | — | Open |
| PRD-AC-12 assignee plan sets owners | §3.8 | ABSD-402 | — | Open |
| PRD-AC-13 a stale plan is refused | §3.4.1 | ABSD-303 | `ApplyExecutorTests.ApplyIsRefusedWhenTheBacklogChangedAfterTheReview`, `.ApplyIsRefusedWhenTheBoardChangedAfterTheReview`, `PlanViewModelTests.AConfirmedApplyIsStillRefusedWhenTheBoardMoved`, `LiveBoardTests.ApplyIsRefusedWhenTheBacklogMovedAfterThePlanWasBuilt` — each asserting nothing was written before the refusal | Covered |
| PRD-AC-14 profiles do not mix | §3.11 | ABSD-502 | — | Open |
| PRD-AC-15 external change forces a reload | §3.11.3 | ABSD-504 | `MainWindowViewModelTests.ASaveRefusesToOverwriteAnExternalChange` asserts the refusal, that the external edit survives byte-for-byte, and that the buffer is preserved | Partial — the save-side guard is tested; the proactive watcher that marks a profile stale before any save attempt does not exist yet |
| PRD-AC-16 CSV matches `gen-csv` | §3.9 | ABSD-204, ABSD-207 | `ImportCsvParityTests.Csv_MatchesThePythonImplementation` over every backlog fixture — byte-for-byte against the live Python csv writer; `ImportCsvTests.*` pin quoting/type-name rules; `MainWindowViewModelTests.ExportCsvWritesTheGenCsvBytesWithoutACredential` covers the app path | Covered |
| PRD-AC-17 released package runs without a toolchain | — | ABSD-601 | — | Open |
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
| NFR-6 structured audit events for Plan/Apply | ABSD-507 | — | Open |
| NFR-7 atomic writes to backlog and config | ABSD-206 | `MainWindowViewModelTests` save tests write through `SaveMarkdown` (temp + rename); the config write-back (ABSD-401/402) is not built yet | Partial |
| NFR-8 stale-plan refusal; save refuses external change | ABSD-303, ABSD-504 | AC-13 and AC-15 rows | Covered |
| DS §6.1 keyboard-reachable editor and plan flow | ABSD-109 | `Ctrl+S` bound in XAML; no traversal test | Partial |
| DS §6.3/6.6 no colour-alone states | ABSD-109 | Unsaved chips and plan badges carry glyph + word by construction (`BacklogNodeViewModelTests` pins the summaries); no automated accessibility audit | Partial |
| DS §6.4 reduced-motion respected | ABSD-109 | No transitions exist to suppress; nothing to test | Met by absence — revisit when motion is added |

## Coverage today

| Measure | Value |
| --- | --- |
| Criteria total | 20 |
| Covered | 9 |
| Partial | 4 |
| Open | 7 |
| Enabling gates met | 3 of 4 |

The Covered rows are the parity-critical ones first — parser, conversion,
validator, CSV export, stale-plan refusal — then this run's editing/-saving and
onboarding criteria (AC-18/19/20). Everything downstream computes against the
engine, so the engine was pinned against the CLI before anything was built on
it; the editing gate now protects the file that engine reads.
