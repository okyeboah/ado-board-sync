# AGENTS.md — ADO Board Sync Desktop

This file is the entry point for any coding agent working in `desktop/` —
Claude Code, Codex, Cursor, Copilot CLI, or any other AGENTS.md-compatible
tool. It is self-contained: it does not depend on a shared "brain" directory,
because none exists in this repository. Read this file before making changes
here, whichever agent you are.

## Current status

There is a running, editing application. `dotnet run --project src/AdoBoardSync.Desktop --
<path-to-board.config.json>` opens a window that loads a Board profile, parses the
backlog with the same Core engine the CLI uses, previews each description as it
will read on the board, lets the user edit a description with that preview
recomputing live and save it back atomically (Ctrl+S), exports the import CSV
byte-identical to `gen-csv`, and can plan and apply Import/Resync/ResyncTasks
against Azure DevOps behind a confirmation gate. Onboarding can describe a
board without a config file and scaffold a starter backlog.

`desktop/docs/` holds twelve documents. Five are live and must be updated in the
same change that makes them wrong:

| Document | Answers |
| --- | --- |
| `STATUS.md` | Is this ticket done? |
| `GAPS.md` | What do we know is broken that no ticket has caught? |
| `TRACEABILITY.md` | Is this requirement tested? |
| `PROJECT-TRACKING.md` | When, in what order, and what could derail it? |
| `BACKLOG.md` | What are the tickets? |

The rest — PRD, FSD, ARCHITECTURE, CONVENTIONS, DESIGN-SYSTEM, GITHUB-PROJECT,
MECE-AUDIT — are the specification (Approved, rev 2). Read ARCHITECTURE.md and
DESIGN-SYSTEM.md before changing structure or UI; they carry constraints the
code does not restate.

## The one rule that matters most

This app's entire value is being a second, trustworthy surface over the same
backlog format the Python CLI (`../src/ado_board_sync/`) already parses and
writes. Any change to parsing, HTML conversion, config loading, or plan
computation must stay byte-for-byte identical to the CLI's behavior for the
same input. Prefer reading the CLI's Python source over guessing what a rule
does — `parser.py`, `htmlfmt.py`, `config.py`, `csvio.py`, `client.py`, and
`commands.py` are the reference implementation. When the .NET port and the
CLI could disagree, write a fixture and run both to check — do not assume.

## Repository layout (planned; create as work proceeds)

| Path | Purpose |
| --- | --- |
| `desktop/src/AdoBoardSync.Core` | Config, Backlog Engine (parser + HTML conversion), Plan Builder. No HTTP, storage, or UI dependencies. |
| `desktop/src/AdoBoardSync.Infrastructure` | Azure DevOps connector, OS credential store, SQLite operation history. |
| `desktop/src/AdoBoardSync.Desktop` | Avalonia desktop host and UI. |
| `desktop/tests/AdoBoardSync.Core.Tests` | Unit tests for parsing, conversion, and plan logic. |
| `desktop/tests/AdoBoardSync.Parity.Tests` | Golden-file tests comparing .NET output to the Python CLI's output for shared fixtures. |
| `desktop/tests/AdoBoardSync.Acceptance.Tests` | End-to-end suite proving the PRD's acceptance criteria against a fixture Azure DevOps connector. |
| `desktop/docs/` | PRD, FSD, Architecture, Design System, Backlog, GitHub Project blueprint. |

## Contribution rules

1. Do not mutate real Azure DevOps data from a test. Use a fixture connector.
2. Every mutating command (`import`, `resync`, `resync-tasks`, `dedup`,
   `sync`, `sprints`, `assign`, close-children) is a Plan first, Apply
   second. Never wire a UI action straight to a write.
3. Keep credentials and PAT values out of source control, logs, exported
   files, and the operation-history store. See `docs/ARCHITECTURE.md` §6.
4. Keep `AdoBoardSync.Core` free of HTTP, UI, and storage framework types —
   it must stay testable in isolation, matching the CLI's zero-dependency
   philosophy for its equivalent modules.
5. Do not add a comment unless it explains a non-obvious decision.
6. Write a parity test before closing any ticket that touches parsing, HTML
   conversion, config loading, or plan computation (ABSD-201, ABSD-202,
   ABSD-204, ABSD-302 and anything that changes their behavior later).
7. A delivery ticket from `docs/BACKLOG.md` is done only when its linked PRD
   acceptance criterion (see `docs/PRD.md` §8) has a passing test.
8. If a fact about the existing CLI, the config schema, or a command's
   behavior is not directly verifiable by reading `../src/ado_board_sync/`
   or `../README.md`, do not guess it — read the source or ask.

## Local run instructions (once code exists)

```bash
cd desktop
dotnet restore
dotnet build
dotnet test
dotnet run --project src/AdoBoardSync.Desktop
```

## Reference implementation

The Python CLI lives at `../src/ado_board_sync/` with its own README at
`../README.md`. It is the ground truth for backlog format, config keys,
command behavior, and error messages — read it before implementing the
equivalent .NET module, not after.
