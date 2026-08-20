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
| PRD-AC-02 preview HTML matches the CLI | §3.2 | ABSD-202 | `MarkdownHtmlParityTests.ToHtml_MatchesThePythonImplementation` over every fixture directory | Covered |
| PRD-AC-03 malformed markup blocks Apply | §3.2 | ABSD-203 | `HtmlBalanceTests.*`, `MarkdownHtmlParityTests.Problems_MatchThePythonImplementation` | Partial — detection is tested; the editor gate that blocks Apply does not exist yet |
| PRD-AC-04 plan counts shown before any write | §3.3 | ABSD-302 | — | Open |
| PRD-AC-05 no mutation before Apply | §3.3.5 | ABSD-302 | — | Open |
| PRD-AC-06 audit reports hierarchy drift | §3.5 | ABSD-304 | — | Open |
| PRD-AC-07 desktop and CLI plans are identical | §3.3.3 | ABSD-302, ABSD-503 | — | Open |
| — | — | ABSD-503 | The end-to-end suite proves every criterion above against a fixture organisation; it closes only when no row is Open | Open |
| PRD-AC-08 operation history shows what changed | §3.10 | ABSD-501 | — | Open |
| PRD-AC-09 close-children assignee inheritance | §3.6 | ABSD-403 | — | Open |
| PRD-AC-10 no PAT blocks board actions | §3.1 | ABSD-103 | `PatResolverTests.*` — seven cases covering source order, whitespace handling, and that `DescribeSources` leaks no token | Partial — resolution is tested; the gate that blocks board actions does not exist yet |
| PRD-AC-11 sprint plan assigns iterations | §3.7 | ABSD-401 | — | Open |
| PRD-AC-12 assignee plan sets owners | §3.8 | ABSD-402 | — | Open |
| PRD-AC-13 a stale plan is refused | §3.4.1 | ABSD-303 | — | Open |
| PRD-AC-14 profiles do not mix | §3.11 | ABSD-502 | — | Open |
| PRD-AC-15 external change forces a reload | §3.11.3 | ABSD-504 | — | Open |
| PRD-AC-16 CSV matches `gen-csv` | §3.9 | ABSD-204 | — | Open |
| PRD-AC-17 released package runs without a toolchain | — | ABSD-601 | — | Open |

## Enabling tickets

These carry no PRD acceptance criterion because they deliver no user-visible
behaviour on their own. Their gate is named explicitly so "no AC" never means
"no standard".

| Ticket | Gate |
| --- | --- |
| ABSD-101 Solution and conventions | `dotnet build` succeeds with `TreatWarningsAsErrors` on every project. **Met.** |
| ABSD-102 Config loader and schema validation | Two gates, because the ticket names two things: `BoardConfigParityTests.*` for resolution matching `config.py`, and `BoardConfigSchemaTests.*` for the constraints in `board.config.schema.json` that a deserialize does not enforce. **Both met.** |
| ABSD-301 Azure DevOps connector | Contract tests against a fixture connector; no live Azure DevOps call in any test. |
| ABSD-505 Continuous integration | `.github/workflows/build-and-test.yml` runs the CLI suite and the .NET build, unit, and parity suites on a clean checkout. **Met.** |

An earlier revision of this file marked ABSD-102 **Met** on the parity tests
alone, while the schema validation its Outcome names did not exist. A gate that
covers part of a ticket and is recorded as covering all of it is worse than no
gate, so a ticket whose Outcome names more than one deliverable now lists one
gate per deliverable. `STATUS.md` is the per-ticket view.

## Coverage today

| Measure | Value |
| --- | --- |
| Criteria total | 17 |
| Covered | 2 |
| Partial | 2 |
| Open | 13 |
| Enabling gates met | 2 of 4 |

The two Covered rows and both Partial rows are the parity-critical ones: the
backlog parser, the HTML conversion, the markup validator, and credential
resolution. That is deliberate — everything downstream computes against them, so
they were built and pinned against the CLI first.
