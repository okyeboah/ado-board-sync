# Changelog

All notable changes to `ado-board-sync`. Versions follow [semantic versioning](https://semver.org/).

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
