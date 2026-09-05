# ADO Board Sync Desktop

A desktop companion to [`ado-board-sync`](../README.md): edit the Markdown
backlog with a live preview of its rendered Azure DevOps description, preview
every board mutation as an explicit plan, and apply it only on confirmation.
Same backlog format, same config, same reconcile semantics as the CLI — a
second surface over the same source of truth, not a new one.

## What works today

- **Open a board.** From a `board.config.json`, or by describing the board in
  onboarding — with a starter backlog scaffolded when the backlog file does not
  exist yet. A config that fails validation is explained on the spot, with a
  typed error code.
- **Edit with a live preview.** Select an item; its description is editable.
  The preview, the generated HTML, the task list, and the markup problems
  recompute on every keystroke through the same Core functions the CLI calls.
- **Save atomically.** `Ctrl+S` (or the Save button) splices every edited block
  back into the backlog file — temp file, then rename — refuses to overwrite an
  external change, re-parses, and keeps the selection where it was. While edits
  are unsaved, Plan and Apply are refused: the file is the source of truth.
- **Plan and apply.** Import, resync and resync-tasks generate a typed
  plan (create/update/delete/unchanged) that only reads; Apply runs it after an
  explicit confirmation, and is refused if the backlog or the board moved since.
- **Export the import CSV.** Byte-identical to `gen-csv` — same columns,
  quoting, and CRLF records — for the Azure DevOps web importer. No credential
  needed.

## Run it

```bash
cd desktop
dotnet restore
dotnet build
dotnet test

dotnet run --project src/AdoBoardSync.Desktop
```

The parity suite runs the real Python modules through
[`tests/parity/parity_driver.py`](tests/parity/parity_driver.py) and compares
them to the .NET port, so a divergence fails the build. It uses
`.venv/bin/python3` at the repository root when present, otherwise `python3`
from `PATH`; override with `ADO_BOARD_SYNC_PYTHON`.

## State

`docs/STATUS.md` is the per-ticket source of truth and carries the evidence for
each; this section deliberately does not repeat it, so the two cannot disagree.
Delivery is planned in [docs/PROJECT-TRACKING.md](docs/PROJECT-TRACKING.md).

## Documents

- [Product requirements](docs/PRD.md)
- [Functional specification](docs/FSD.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Design system](docs/DESIGN-SYSTEM.md)
- [Project tracking](docs/PROJECT-TRACKING.md)
- [Solution and delivery conventions](docs/CONVENTIONS.md)
- [Delivery status](docs/STATUS.md) — what is built, with evidence
- [Initial delivery backlog](docs/BACKLOG.md)
- [Requirements traceability](docs/TRACEABILITY.md)
- [Gap register](docs/GAPS.md)
- [MECE audit and repair record](docs/MECE-AUDIT.md)
- [GitHub Project import blueprint](docs/GITHUB-PROJECT.md)

## Shape of the app

- .NET 10 desktop application with Avalonia UI (Windows, macOS, Linux).
- A full .NET port of the CLI's parser, HTML conversion, config loader, CSV
  export, and plan/apply logic, verified against the Python implementation with
  golden-file parity tests.
- The Markdown backlog and `board.config.json` remain the only sources of
  truth; local storage is for operation history only (planned).
- A PAT resolved from the session, then the CLI's existing env-var/file
  fallback; OS credential storage is planned. The PAT is never written to the
  config, the backlog, logs, or exports.

See [AGENTS.md](AGENTS.md) for the conventions any coding agent should follow,
and [docs/CONVENTIONS.md](docs/CONVENTIONS.md) for layout, quality commands, and
the CLI behaviours that are easy to get wrong.
