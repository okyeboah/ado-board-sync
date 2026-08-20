# Changelog

All notable changes to `ado-board-sync`. Versions follow [semantic versioning](https://semver.org/).

## Unreleased

### Added

- `resync-tasks` takes an optional Issue code, so `resync-tasks PROJ-101 --go`
  reconciles one Issue's Tasks instead of the whole board. Without it, syncing one
  ticket also applied every other ticket's pending bullets, which pushed people
  towards one-off scripts.
- A desktop companion application under `desktop/`. This release carries the
  planning package and a parity-verified Core library: the config loader, backlog
  parser, Markdown-to-HTML conversion, and markup validator ported to .NET 10 and
  compared against the Python modules on every build. The UI is not started.
- Continuous integration. Every push and pull request runs the CLI suite on
  Python 3.9 and 3.13, and the desktop build, unit, and parity suites.

### Changed

- `sync-one` scopes its lookup to the Issue work-item type. A Task that cites
  another ticket's code in its own title — "…surfaced to monitoring (PROJ-101)" —
  matched the unscoped `CONTAINS`, so the command reported the cited ticket as
  ambiguous and it became unsyncable.
- `resync-tasks` scoped to one code now queries only that Issue instead of reading
  every Issue on the board to look one up.

### Fixed

- The `max_retries` default is documented as `3`, which is what the code has
  always used. The README said `0`.

## 0.2.0

### Added

- `check-html` command. Converts every Epic, Issue, and Task description offline
  and exits 1 on markup no browser can render. Needs no PAT and no network.
- `sync` now runs `check-html` after `gen-csv` and aborts before the first write
  if any description is malformed. `audit` cannot catch this afterwards, because
  it compares tag-stripped text: a description malformed on both sides compares
  equal.
- `audit` checks state against the hierarchy. Azure DevOps never cascades state,
  so a done Epic can sit above open Issues and Tasks indefinitely. A done parent
  with open descendants now fails the audit. The reverse — every child done while
  the parent is not — is reported but does not fail, because closing a parent is
  a judgement call.
- Markdown tables in a description convert to `<table>`, with the header row as
  `<th>` and inline border styles. Azure DevOps applies no stylesheet to the
  field, so an unstyled table arrives borderless.

### Changed

- `close-children` closes every open descendant of any done ancestor, Epic
  through Issue to Task, at any depth. It previously handled Issue to Task only.
  `--assign-from-parent` follows the same ancestor.
- `audit` reads the board hierarchy once and serves both the Task-parity check
  and the state check from it. It previously issued two requests per Issue.
- A description line that wraps in the backlog is joined to the block above it.
  It previously closed the surrounding list and started a paragraph, which split
  one bullet into two blocks.
- Nested bullets nest. They were flattened to a single level.

### Fixed

- The italics pass ran across text already inside a code span, so the asterisks
  in `` `Shared.*` `` and `` `Other.*` `` paired into an `<i>` that interleaved
  with `<code>`. Code spans are now lifted out before the bold and italic passes.

## 0.1.0

- Initial release.
