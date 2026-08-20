# ADO Board Sync Desktop

A desktop companion to [`ado-board-sync`](../README.md): edit the Markdown backlog with a live preview of its rendered Azure DevOps description, preview every board mutation as an explicit plan, and apply it only on confirmation. Same backlog format, same config, same reconcile semantics as the CLI — a second surface over the same source of truth, not a new one.

## Status

**There is no runnable application yet.** The planning package is complete and the
parity-critical Core has landed: configuration, credential resolution, backlog
parsing, Markdown-to-HTML conversion, and markup validation. Nothing connects to a
board and nothing renders — `AdoBoardSync.Infrastructure` and
`AdoBoardSync.Desktop` do not exist.

5 of 20 delivery tickets are Done, 3 Partial, 12 Not started.
[docs/STATUS.md](docs/STATUS.md) is the per-ticket source of truth and carries the
evidence for each; this section deliberately does not repeat it, so the two cannot
disagree. Tickets are tracked as GitHub issues labelled `app:desktop`.

## Documents

- [Product requirements](docs/PRD.md)
- [Functional specification](docs/FSD.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Design system](docs/DESIGN-SYSTEM.md)
- [Solution and delivery conventions](docs/CONVENTIONS.md)
- [Delivery status](docs/STATUS.md) — what is built, with evidence
- [Initial delivery backlog](docs/BACKLOG.md)
- [Requirements traceability](docs/TRACEABILITY.md)
- [MECE audit and repair record](docs/MECE-AUDIT.md)
- [GitHub Project import blueprint](docs/GITHUB-PROJECT.md)

## Stack

- .NET 10 desktop application with Avalonia UI (Windows, macOS, Linux), matching [ado-insights](../../ado-insights).
- A full .NET port of the CLI's parser, HTML conversion, config loader, and command logic, verified against the Python implementation with golden-file parity tests.
- SQLite for local operation history and audit findings only — the Markdown backlog and `board.config.json` remain the sources of truth.
- A PAT resolved from OS credential storage, with the CLI's existing env-var/file fallback recognized for projects already set up for CLI use.

## Build and test

```bash
cd desktop
dotnet restore
dotnet build
dotnet test
```

The parity suite runs the real Python modules through
[`tests/parity/parity_driver.py`](tests/parity/parity_driver.py) and compares
them to the .NET port, so a divergence fails the build. It uses
`.venv/bin/python3` at the repository root when present, otherwise `python3`
from `PATH`; override with `ADO_BOARD_SYNC_PYTHON`.

## Working on this project

See [AGENTS.md](AGENTS.md) for the conventions any coding agent should follow,
and [docs/CONVENTIONS.md](docs/CONVENTIONS.md) for layout, quality commands, and
the CLI behaviours that are easy to get wrong.
