# Solution and Delivery Conventions

**Status:** Effective

**Ticket:** ABSD-101

## Repository layout

| Path | Purpose |
| --- | --- |
| `desktop/src/AdoBoardSync.Core` | Config loading, credential resolution, backlog parsing, Markdown/HTML conversion, and (as they land) the Plan Builder. No HTTP, storage, or UI dependencies. |
| `desktop/src/AdoBoardSync.Core/PythonCompat.cs` | The Python semantics the port depends on — `re.match` anchoring and text-mode line splitting — stated once for every module ported from the CLI. |
| `desktop/src/AdoBoardSync.Infrastructure` | Azure DevOps connector, OS credential store, SQLite operation history. Not yet created. |
| `desktop/src/AdoBoardSync.Desktop` | Avalonia desktop host and UI. Not yet created. |
| `desktop/tests/AdoBoardSync.Core.Tests` | Unit tests stating the behaviour the Core guarantees. |
| `desktop/tests/AdoBoardSync.Parity.Tests` | Golden-file tests comparing the .NET output to the Python CLI's output. |
| `desktop/tests/AdoBoardSync.TestKit` | Shared test helpers: repository paths, the Python reference runner, temp Board profiles. |
| `desktop/tests/parity/parity_driver.py` | Emits reference output from the CLI implementation for the parity suite. |
| `desktop/tests/fixtures/` | Backlog and markup fixtures fed to both implementations. Excluded from markdownlint on purpose. |
| `desktop/docs/` | Product documents, backlog, and the GitHub Project blueprint. |

## Local run instructions

```bash
cd desktop
dotnet restore
dotnet build
dotnet test
```

The parity suite shells out to Python. It uses `.venv/bin/python3` at the
repository root when that exists, otherwise `python3` from `PATH`. Override it
with `ADO_BOARD_SYNC_PYTHON=/path/to/python3`.

## Quality commands

```bash
dotnet build --configuration Release
dotnet test --configuration Release
```

Every project sets `TreatWarningsAsErrors`. A warning fails the build.

## Contribution rules

1. Every mutating command is a Plan first and an Apply second. Never wire a UI
   action straight to a write.
2. Keep credentials and PAT values out of source control, logs, exported files,
   and the operation-history store.
3. Keep `AdoBoardSync.Core` free of HTTP, UI, and storage framework types.
4. Do not add a comment unless it explains a non-obvious decision.
5. Write a parity test before closing any ticket that touches parsing, HTML
   conversion, config loading, or plan computation.
6. A delivery ticket is Done only when its linked PRD acceptance criterion has a
   passing test. `docs/TRACEABILITY.md` is that gate — update it in the same
   change that moves a criterion from Open to Covered.
7. A ticket with neither an acceptance criterion nor a named enabling gate in
   `docs/TRACEABILITY.md` is not ready to start.

## Parity is the load-bearing rule

The desktop app's value is being a second surface over the same backlog format
the CLI already parses. `AdoBoardSync.Parity.Tests` runs the real Python modules
through `parity_driver.py` and compares them to the .NET port on every build, so
a divergence fails `dotnet test` rather than reaching a user as a preview that
does not match what the board will show.

Two consequences:

- Prefer reading `src/ado_board_sync/*.py` over guessing what a rule does.
- When the CLI's behaviour and its README disagree, the code is the truth. Port
  the code, and fix the README.

## Behaviour worth knowing before you port more

These are real CLI behaviours that surprise people. They are pinned by tests in
`AdoBoardSync.Core.Tests` so a port cannot quietly "fix" them:

- Issue-code matching is **case-sensitive** against `code_prefix`. With prefix
  `PROJ`, a `### proj-101` heading is not an Issue, and its bullets fold into the
  previous Issue's description.
- An **indented** `-` bullet still becomes a Task. The parser checks the stripped
  line, so only the bullet *character* distinguishes a Task from description text.
- A wrapped line joins the block above it. A blank line is what ends a block.
- Only CR, LF, and CRLF end a line. Python's text mode leaves FF, VT, NEL, LS, and
  PS inside the line, so `string.ReplaceLineEndings` and `string.Split` are both
  wrong here — use `PythonCompat.SplitLines`.

## Layering

`BacklogParser` takes the backlog as text, never a path: the editor parses an
unsaved buffer on every keystroke, and reading files belongs to the profile layer.
Credential resolution lives in `PatResolver`, not on `BoardConfig`, so that
Infrastructure can prepend an operating-system credential-store source without
Core ever referencing it.
