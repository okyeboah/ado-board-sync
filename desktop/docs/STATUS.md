# Delivery Status

**Status:** Live — the single source of truth for what is built. Update it in the
same change that moves a ticket.

`TRACEABILITY.md` answers "is this requirement tested?". This file answers "is
this ticket done?". Both existed only as prose before, in three vocabularies, so
the question "are the issues complete?" had no answerable source. One vocabulary,
here:

| State | Meaning |
| --- | --- |
| Done | Everything the ticket's Outcome names is built, with a passing test or a named gate met. Its GitHub issue is closed. |
| Partial | Some of the Outcome is built. The remainder is named in the row. The issue stays open. |
| Not started | No code exists for it. |

A ticket is never Done because most of it works. If the row needs the word
"except", it is Partial.

## Delivery tickets

| Ticket | State | Evidence, or what remains |
| --- | --- | --- |
| ABSD-101 Solution and conventions | Done | `AdoBoardSync.slnx`, `docs/CONVENTIONS.md`; every project builds with `TreatWarningsAsErrors`. |
| ABSD-102 Config loader and schema validation | Done | `BoardConfig` + `BoardConfigSchema`; 4 parity scenarios against `config.py`, 25 schema tests, and a guard that fails the build if `board.config.schema.json` gains a key the validator does not know. |
| ABSD-103 Credential resolution and OS credential store | Partial | `PatResolver` resolves `pat_env` then `pat_file`, matching the CLI. **Remaining:** the operating-system credential store, which needs `AdoBoardSync.Infrastructure`. |
| ABSD-201 Backlog parser | Done | `BacklogParser`; `BacklogParserParityTests` compares the parsed tree and tasks-by-code against `parser.py`. |
| ABSD-202 Markdown-to-HTML conversion | Done | `MarkdownHtml`; `MarkdownHtmlParityTests` compares HTML, plain, inline, and norm against `htmlfmt.py`. |
| ABSD-203 Split-pane editor with live preview | Partial | `HtmlBalance` implements the validation half, parity-checked against `htmlfmt.unbalanced`. **Remaining:** the editor, the preview pane, and inline markers — all of the UI. No Avalonia code exists. |
| ABSD-204 CSV export | Not started | — |
| ABSD-301 Azure DevOps connector | Not started | — |
| ABSD-302 Plan Builder | Not started | — |
| ABSD-303 Apply Executor with stale-plan guard | Not started | — |
| ABSD-304 Audit view | Not started | — |
| ABSD-401 Sprint planning view and Plan | Not started | — |
| ABSD-402 Assignee planning view and Plan | Not started | — |
| ABSD-403 Close-children review and apply | Not started | — |
| ABSD-501 Operation history store | Not started | — |
| ABSD-502 Multi-profile registry and switching | Not started | — |
| ABSD-503 End-to-end parity and acceptance suite | Partial | The parity half exists: 43 comparisons against the live Python modules. **Remaining:** the acceptance half — proving each PRD criterion against a fixture organisation. |
| ABSD-504 External backlog and config change detection | Not started | — |
| ABSD-505 Continuous integration | Done | `.github/workflows/build-and-test.yml`; green on `main`, running the CLI suite on Python 3.9 and 3.13 and the desktop build, unit, and parity suites. |
| ABSD-601 Installable desktop package | Not started | — |

## Totals

| State | Count |
| --- | --- |
| Done | 5 |
| Partial | 3 |
| Not started | 12 |
| **Total** | **20** |

## What this means

The parity-critical foundation is built and pinned against the CLI: configuration,
credential resolution, backlog parsing, HTML conversion, and markup validation.
Everything that reaches Azure DevOps or draws a pixel is not.

There is no user-runnable application. `AdoBoardSync.Infrastructure` and
`AdoBoardSync.Desktop` do not exist, so nothing connects to a board and nothing
renders. The next ticket that changes that is ABSD-301, and the first one a user
would notice is ABSD-203.

## Release slices

| Release | State |
| --- | --- |
| R1 Desktop foundation | Partial — configuration and credentials done; no host, no profile registry |
| R2 Backlog editor | Partial — parser, converter, and validator done; no editor |
| R3 Plan and apply | Not started |
| R4 Sprints, assignees and operations | Not started |
| R5 Distribution | Not started |
