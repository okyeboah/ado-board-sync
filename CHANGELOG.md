# Changelog

All notable changes to `ado-board-sync`. Versions follow [semantic versioning](https://semver.org/).

## Unreleased

### Added

- `set-state ID ... --go`: set one or more work items' state directly by id,
  for stragglers outside any backlog reconcile. A leading `[ ]` title checkbox
  is ticked when the target is the configured terminal state; `--state NAME`
  targets any state, `--no-tick` never rewrites titles.
- `advance --repo PATH ...`: read local repositories and move each backlog Issue
  whose branch holds commits beyond the base ref from `states.todo` to
  `states.doing`. It only ever makes that one transition — it never sets done
  from git evidence, because merge detection fails in both directions and
  completing an item is a human decision backed by a merged pull request. The
  start/working state names must be configured per process template via
  `states.todo` / `states.doing`.
- `resync-tasks` takes an optional Issue code, so `resync-tasks PROJ-101 --go`
  reconciles one Issue's Tasks instead of the whole board. Without it, syncing one
  ticket also applied every other ticket's pending bullets, which pushed people
  towards one-off scripts.
- A desktop companion application under `desktop/`: an Avalonia (net10.0) app
  whose engine is a .NET port of the CLI's config loader, backlog parser,
  Markdown-to-HTML conversion, markup validator, and import/resync/resync-tasks
  plan-and-apply logic, compared against the Python modules on every build by a
  parity suite. It opens a Board profile from a `board.config.json` or from an
  onboarding form (which can scaffold a starter backlog), edits descriptions in
  a split-pane editor whose preview, task list and markup problems recompute
  live, saves them atomically with an external-change refusal, writes the
  import CSV byte-identical to `gen-csv`, and applies plans behind an explicit
  confirmation with a stale-plan guard.
- Continuous integration. Every push and pull request runs the CLI suite on
  Python 3.9 and 3.13, and the desktop build, unit, and parity suites.
- The desktop application's remaining surfaces: sprint and assignee tables that
  edit `board.config.json` atomically and schema-validated, an Apply timeline
  scoped to the open profile, and a profile switcher backed by a registry kept
  under the user's own local data — never beside a backlog, where it would
  eventually be committed, and holding no credential by construction.
- A headless UI interaction harness. Tests now open the real window, click its
  buttons and type into its fields, and fail on any binding Avalonia could not
  resolve — which a compiled binding's build-time check cannot cover for
  `$parent`-relative paths or for bindings evaluated before their data exists.
- An acceptance suite: one test per PRD acceptance criterion, each tagged with
  its identifier, plus a guard that reads `PRD.md` and fails when a criterion has
  no test or a test names one that no longer exists.

### Changed

- The desktop application's assignee plan now shows an already-correctly-owned
  item as **Unchanged** instead of omitting it. A plan listing two of the five
  codes you configured was indistinguishable from one that had lost the other
  three. Unchanged rows are never written, so the board sees no difference.

- Apply phases fan their independent work-item writes out over pooled worker
  threads — the REST client hands each thread its own keep-alive connection —
  and the fixed inter-write sleeps are gone; throttling is handled by the
  existing 429 retry policy. Boards of several hundred items no longer pay
  tens of seconds of pure waiting inside `import`, `resync`, `resync-tasks`,
  `close-children`, `dedup`, `sprints`, or `assign`.
- `audit` issues one WIQL query plus one batched get where it used to issue two
  of each: reading Epics/Issues for identity and description parity, then the
  hierarchy walk again for Task parity and state agreement, doubled work the
  single read does once.
- `dedup` resolves child Task parents through `System.Parent` on its existing
  batched read instead of paying a relations-expand round trip per Task.
- `sync-one` scopes its lookup to the Issue work-item type. A Task that cites
  another ticket's code in its own title — "…surfaced to monitoring (PROJ-101)" —
  matched the unscoped `CONTAINS`, so the command reported the cited ticket as
  ambiguous and it became unsyncable.
- `resync-tasks` scoped to one code now queries only that Issue instead of reading
  every Issue on the board to look one up.
- The desktop's sprint and assignee tables share one implementation. They were the
  same file twice — identical dirty tracking, row subscription, clear, save
  sequencing and coverage reporting — differing only in their nouns. The mechanism
  moves to a generic base; the assignee table's refusal to save two rows for one
  identity becomes a named hook. A row's codes are now parsed once rather than
  three times per row per keystroke.

### Fixed

- The desktop application recorded no operation history at all in the running
  app. The shell built its own Plan gate rather than resolving the one the
  composition root had configured, so the history recorder and the diagnostics
  redactor registered there were never reached.
- Even once reached, an Apply lost its per-item outcomes. They were recorded
  from inside a `Progress<T>` callback, which the UI dispatcher delivers after
  the reporting call returns — by which time the run had been closed, and a
  closed run refuses outcomes. The timeline showed runs with no rows in them.
- Running the desktop test suite wrote into the developer's own profile registry
  and operation history, because the three local-data paths each resolved the OS
  data directory for themselves with no way to redirect them.
- The desktop application's board connector never came from its composition root.
  `IBoardGatewayFactory` and its adapter were registered and resolved by nobody;
  the Plan gate and the Audit view each constructed an `AzureDevOpsGateway`
  themselves. Port and adapter removed, the container binds the delegate both
  callers already took, and a view model built outside it now refuses to reach a
  board rather than building a real connector — the same fix as the credential
  store before it.
- One acceptance test called `dev.azure.com` on every run. It set a session token
  and generated a Plan with no gateway supplied, passing only because the network
  failure landed in `ErrorText`, which it never asserted on.
- ABSD-507's diagnostics were declared and never emitted. The one class that
  logged anything built its events by hand and put backlog item titles — the
  user's prose — into the file users are asked to attach to support
  conversations. The Plan gate and the profile loader now emit the declared
  events, which name failed rows by issue code only.

- The Sprints and Assignees tables reloaded through a profile loader they built
  themselves, so saving from either emitted none of the file-write diagnostics
  above: the loader they constructed was wired to a null sink, while the
  registered one logs. The container now supplies the reload to both. This is
  the same defect as the board connector and the credential store, in the third
  place it occurred.

- `board.config.json` was written atomically but not durably: the rename could
  reach the disk before the bytes it pointed at, so a power cut during a save
  could leave the config truncated. It now flushes to the device before the
  rename, as the backlog store already did (FSD NFR-7).

### Removed

- `CompositeDiagnostics`: its only caller was its own test, and the application
  registers a single sink. `InMemoryDiagnostics` moved from the shipped
  Infrastructure assembly into the test kit.

- The CLI's `audit` sorted every non-Epic work item into its Issue bucket, so on boards where Task titles cite issue codes — which is common — each citation became a phantom duplicate finding and a phantom description drift against the cited Issue, failing audits that were healthy. Audit now sorts strictly on Epic and Story types; found by, and re-validated live against, the desktop's plan-parity check.
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
