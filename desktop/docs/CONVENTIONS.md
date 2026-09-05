# Solution and Delivery Conventions

**Status:** Effective

**Ticket:** ABSD-101

## Repository layout

| Path | Purpose |
| --- | --- |
| `desktop/src/AdoBoardSync.Core` | Config loading, credential resolution, backlog parsing, Markdown/HTML conversion, and (as they land) the Plan Builder. No HTTP, storage, or UI dependencies. |
| `desktop/src/AdoBoardSync.Core/PythonCompat.cs` | The Python semantics the port depends on — `re.match` anchoring and text-mode line splitting — stated once for every module ported from the CLI. |
| `desktop/src/AdoBoardSync.Infrastructure` | The only project that touches the filesystem or the network on the application's behalf: the Azure DevOps connector, the backlog/config file store, the OS credential store, and the SQLite operation history. References Core and nothing else. |
| `desktop/src/AdoBoardSync.Desktop` | Avalonia desktop host, views, view models, and the single composition root that binds every port to its adapter. |
| `desktop/Directory.Build.props` | The build conventions every project inherits: target framework, nullable, `TreatWarningsAsErrors`. A new project gets the quality gate without restating it. |
| `desktop/Directory.Packages.props` | Every package version, once. A csproj names a package and never its version. |
| `desktop/global.json` | The SDK feature-band pin, and the file CI's `setup-dotnet` reads. |
| `desktop/tests/Directory.Build.targets` | Adds the xunit packages and the `Xunit` global using to test projects only. Imported after each csproj so it can read that project's own `IsTestProject`; `AdoBoardSync.TestKit` sets it false and is skipped. |
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

dotnet run --project src/AdoBoardSync.Desktop   # opens the application
```

The parity suite shells out to Python. It uses `.venv/bin/python3` at the
repository root when that exists, otherwise `python3` from `PATH`. Override it
with `ADO_BOARD_SYNC_PYTHON=/path/to/python3`.

## Quality commands

```bash
dotnet build --configuration Release
dotnet test --configuration Release
```

Every project inherits `TreatWarningsAsErrors` from `Directory.Build.props`. A
warning fails the build, and a project added later cannot opt out by omission.

## Package versions

Versions live in `desktop/Directory.Packages.props` and nowhere else —
`ManagePackageVersionsCentrally` is on, so a `Version=` attribute in a csproj
fails the restore with NU1008 rather than drifting quietly.

**All Avalonia packages move in lockstep.** They are pinned through the single
`$(AvaloniaVersion)` property; bumping one and not the others binds a mismatched
pair at runtime rather than failing at build. The line tracks 12.1.x: Avalonia
11.3.7 resolves a vulnerable `Tmds.DBus.Protocol` 0.21.2 and fails `dotnet
restore` with NU1903 under this repository's warning policy, while 12.1.1
resolves 0.94.1 and restores clean with no suppression to remember to remove.

The SDK is pinned by `desktop/global.json` with `rollForward: latestFeature`,
and `.github/workflows/build-and-test.yml` reads that same file through
`global-json-file` — one authoritative pin, so CI and a development machine
cannot disagree about which SDK built a green run.

## Publishing

```bash
./build/publish.sh                 # this machine's runtime identifier
./build/publish.sh osx-arm64       # or win-x64, linux-x64, osx-x64
./build/verify.sh osx-arm64        # assert the binary is for the machine it claims
./build/package.sh osx-arm64       # per-user installable package
```

Output lands in `desktop/artifacts/publish/<rid>/` and `desktop/artifacts/packages/`.

Use the script rather than a bare `dotnet publish`: the properties that make the
result runnable on a machine with no .NET — single-file, self-contained, no
trimming — live in it, and CI runs the same three scripts. A hand-rolled publish
produces a framework-dependent build in a different directory, which
`package.sh` will not find. Packages are unsigned; the scripts print the signing
commands rather than pretending (ABSD-601).

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
8. Record the ticket in `docs/STATUS.md` in the same change that moves it, and
   close its GitHub issue only when that row reads Done. A ticket whose Outcome
   names two deliverables needs a gate for each; "mostly working" is Partial.

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
