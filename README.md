# ado-board-sync

Drive an **Azure DevOps** board (Epics → Issues/Stories → Tasks) from a Markdown
backlog. You keep one document up to date; running the tool reconciles the board
to match it.

- **Zero dependencies.** Python 3.8+ standard library, nothing to install.
- **The PAT never touches the repo.** It's read from an environment variable or
  a gitignored token file at run time, and never written to the config, the
  logs, or the CSV.
- **Dry-run by default.** Anything that would change the board prints its plan
  first and only writes when you add `--go`.
- **Not tied to one process template.** Hierarchy comes from
  `System.LinkTypes.Hierarchy` links, so moving between Basic, Agile, and Scrum
  only means changing the type *names* in `types`.

## Install

```bash
pip install -e .          # from a clone, installs the `ado-board-sync` command
```

To try it without installing, put the package on the path and run it as a
module: `PYTHONPATH=src python3 -m ado_board_sync ...`.

## Backlog format

The backlog is a Markdown file. Its structure maps to work items as follows:

| Markdown | Becomes |
| --- | --- |
| `## Epic <n> — <title>` (matched by `epic_heading_regex`) | an **Epic** |
| `### <PREFIX>-<n> · <title>` (e.g. `### PROJ-101 · …`) | an **Issue/Story**, code `PROJ-101` |
| Top-level `-` bullet under an Issue | a child **Task** of that Issue |
| any other line between two headings | the **description** of the preceding item (rendered to HTML) |

Rules:

- Parsing begins at the **first** Epic heading. Anything before it is ignored.
- Parsing stops at the first line that begins with any `stop_headings` entry
  (use these for appendices, registers, traceability tables, etc.).
- Only top-level `-` (dash) bullets become Tasks. Nested bullets (indented
  `*`) stay in the description.
- Issue codes are matched by `<PREFIX>-<digits>`; the prefix is set by
  `code_prefix`. Codes are the stable identity used to match backlog items to
  board items across renames.

### What a description can contain

Azure DevOps stores a description as HTML, so the backlog Markdown is converted
before it is written. These constructs are supported:

| Markdown | Rendered as |
| --- | --- |
| `**bold**`, `*italics*`, `` `code` `` | `<b>`, `<i>`, `<code>` |
| `-` / `*` bullets | `<ul>`, nested by indentation |
| a pipe table, with or without a separator row | `<table>`; row one becomes `<th>` |
| `---` | `<hr>` |

Two things follow from the conversion:

- A line that wraps is joined to the block above it. A blank line ends a block,
  which is what separates one paragraph or bullet from the next.
- Tables carry inline border styles, because Azure DevOps applies no stylesheet
  of its own to the field.

Run `check-html` after changing the backlog. It converts every description
offline and fails on markup that no browser can render.

Example:

```markdown
## Epic 1 — Platform Foundations
*Context: shared libraries every other layer depends on.*

### PROJ-101 · Build the core event store
*Reference: ADR-002*
- Implement the append-only `EventStore`
- Add optimistic-concurrency checks
  * note: covered by the snapshot policy below

### PROJ-102 · Wire up local orchestration

## Appendix — Deferred items   <-- listed in stop_headings, parsing stops here
```

## Configuration

Copy `board.config.example.json` to your project as `board.config.json` and edit
it. `board.config.schema.json` documents and validates the structure.

| Key | Required | Default | Meaning |
| --- | --- | --- | --- |
| `org` | yes | — | Azure DevOps organisation name |
| `project` | yes | — | Azure DevOps project name |
| `code_prefix` | yes | — | Issue code prefix, e.g. `PROJ` matches `PROJ-101` |
| `board_file` | no | `docs/backlog.md` | Backlog Markdown path (relative to the config file) |
| `csv_file` | no | `build/work-items.csv` | Where the import CSV is written by `gen-csv` and read by `import` (the ADO web importer can also use it). `resync` and `audit` read the backlog directly, not this file. |
| `types` | no | `{epic:Epic, story:Issue, task:Task}` | Work-item type names for the three levels (Agile uses `User Story`) |
| `states` | no | `{done:Done}` | Terminal state name used by `close-children` and by the `audit` state check (Agile/CMMI use `Closed`/`Resolved`) |
| `epic_heading_regex` | no | `^##\s+(Epic\b.*)$` | Regex matching an Epic heading; capture group 1 is the title |
| `stop_headings` | no | `[]` | Line prefixes that end the backlog body |
| `api_version` | no | `7.1` | Azure DevOps REST API version |
| `pat_env` | no | `AZURE_DEVOPS_PAT` | Env var the PAT is read from |
| `pat_file` | no | `.ado_pat` | Token file checked if the env var is unset (gitignore it) |
| `task_title_max` | no | `250` | Max Task title length (bullets are truncated to this) |
| `team` | no | `null` | Team whose sprint view iterations are added to; `null` auto-detects `<Project> Team` |
| `iterations` | no | `[]` | Sprints for the `sprints` command (see below) |
| `assignees` | no | `{}` | Owners for the `assign` command (see below) |
| `max_retries` | no | `0` | Maximum number of retries for idempotent REST client requests |
| `backoff` | no | `1.5` | Base exponential-ish backoff sleep duration in seconds |
| `timeout` | no | `20` | REST client request timeout in seconds |

### Sprints (`iterations`)

The `sprints` command creates iteration (sprint) nodes and assigns Issues to
them. Declare them in the config — each entry lists the Issue codes it owns:

```json
"iterations": [
  { "name": "Sprint 1", "start": "2026-06-29", "finish": "2026-07-10", "items": ["PROJ-101", "PROJ-102"] },
  { "name": "Sprint 2", "start": "2026-07-13", "finish": "2026-07-24", "items": ["PROJ-201"] }
]
```

- `start` / `finish` (`YYYY-MM-DD`) are optional — a sprint can be dateless.
- Each Issue's child Tasks inherit the Issue's sprint (unless `--no-tasks`).
- If a code appears in two sprints the **earliest listed** wins.
- Creating iteration nodes needs the **Create child nodes** permission on the
  project's iteration tree (a project-settings ACL, *not* a PAT scope). If it's
  denied, have a project admin create the nodes, then run `sprints --go --assign-only`.

### Assignees (`assignees`)

The `assign` command sets each Issue's owner (`System.AssignedTo`) from the
config so a planned work split is reproducible instead of hand-clicked in the
UI. Each key is an ADO identity; its value lists the Issue codes that person owns:

```json
"assignees": {
  "alice@your-organisation.com": ["PROJ-101", "PROJ-102"],
  "bob@your-organisation.com": ["PROJ-201"]
}
```

- The identity is an ADO account string — a `uniqueName`/email, or a display
  name the organisation resolves.
- Each Issue's child Tasks inherit the Issue's owner (unless `--no-tasks`).
- Already-correct assignments are skipped, so re-running is a no-op.
- If a code appears under two identities the **first listed** wins.
- Setting `System.AssignedTo` needs only the PAT's **Work Items: Read & Write**
  scope — no special project ACL (unlike `sprints`).

### Credentials

Provide a Personal Access Token with **Work Items: Read & Write** scope, either:

- `export AZURE_DEVOPS_PAT=...`, or
- write it to a `.ado_pat` file next to `board.config.json` (gitignored).

## Commands

Run from the project directory (it reads `./board.config.json`), or pass
`-c /path/to/board.config.json` to run from anywhere:

```bash
ado-board-sync <command> [--go]                       # installed console script
PYTHONPATH=src python3 -m ado_board_sync <command>    # or as a module, no install
```

| Command | What it does |
| --- | --- |
| `gen-csv` | Parse the backlog and (re)write the import CSV |
| `import` | Create Epics/Issues that are missing from the board (idempotent) |
| `resync` | Update Epic/Issue titles + descriptions to match the backlog |
| `resync-tasks` | Add/delete each Issue's child Tasks to match the backlog bullets |
| `close-children` | Close every open descendant of a work item already Done — Epic → Issue → Task, at any depth (Azure DevOps doesn't cascade state downward). `--assign-from-parent` also copies the done ancestor's assignee onto each closed item that is currently unassigned (never overwrites an existing assignee) |
| `dedup` | Delete duplicate work items (same code, or same title under one parent) |
| `check-html` | Read-only, offline check that every description converts to valid HTML; exit 1 on malformed markup. Needs no PAT and no network |
| `audit` | Read-only check that the board matches the backlog **and that its states agree with its hierarchy**; exit 1 on drift |
| `sync` | `gen-csv → check-html → import → resync → resync-tasks → audit`. Aborts before the first write if any description is malformed |
| `sprints` | Create the configured iterations and assign Issues (+ child Tasks) to them |
| `assign` | Set each Issue's (and child Tasks') assignee from the `assignees` config |

`gen-csv`, `check-html`, and `audit` never modify the board. `import`, `resync`,
`resync-tasks`, `close-children`, `dedup`, `sprints`, `assign`, and `sync` print their plan and require `--go` to write.
`close-children` and `assign` are intentionally excluded from `sync`: `sync` is a structural
reconcile, whereas closing items or setting owners changes workflow/ownership metadata, so each
must be run explicitly.

One consequence: `sync` returns the exit code of `audit`, and `audit` fails when a done parent
still has open descendants. So `sync` reports state drift but never repairs it — it exits 1 until
you clear the drift with `close-children --go`. That is deliberate. Closing someone's work item is
a decision, not a reconcile, so the tool surfaces it and waits.

The backlog Markdown is the single source of truth: `resync`, `resync-tasks`, and
`audit` all read it directly, so editing the backlog and running any of them updates the
board even if you never regenerate the CSV. The CSV is only an artifact for `import`
(and the ADO web importer). Because `audit` compares the board against the backlog, a
stale CSV can never produce a false PASS.

`sprints` also accepts `--assign-only` (skip node creation; the iterations must
already exist), `--no-tasks` (assign Issues only, don't cascade to Tasks), and
`--reset-on-missing` (reset Issue iteration path to project root if sprint assignment fails).

`assign` accepts `--no-tasks` (set the Issue owner only, don't cascade to child
Tasks) and `--only-unassigned` (fill an owner only where none is set; never
overwrite a deliberate assignment).

### Typical flow

```bash
ado-board-sync gen-csv            # refresh CSV from the backlog
ado-board-sync import             # preview new items...
ado-board-sync import --go        # ...then create them
ado-board-sync resync --go        # bring titles/descriptions in line
ado-board-sync resync-tasks --go  # reconcile child tasks
ado-board-sync audit              # confirm board == backlog
```

## Tests

```bash
pip install -e .                                  # then:
python3 -m unittest discover -s tests -t . -v
# ...or without installing:
PYTHONPATH=src python3 -m unittest discover -s tests -t . -v
```

The suite covers the parser, Markdown/HTML conversion, CSV round-trip, config
loading/credential resolution, and the command logic (against an in-memory fake
Azure DevOps client — no network access required).

## Troubleshooting

| Symptom | Likely cause / fix |
| --- | --- |
| `No PAT found. Set $AZURE_DEVOPS_PAT …` | No token resolved. Export the env var or create the `.ado_pat` file next to `board.config.json`. |
| `WIQL failed: 401` / `Batch get failed: 401` | PAT is invalid, expired, or missing the **Work Items: Read & Write** scope. Regenerate it. |
| `WIQL failed: 404` (or all items missing) | Wrong `org` / `project` in the config, or the PAT belongs to a different organisation. |
| `FAIL create Issue … 400 … work item type 'Issue' … does not exist` | `types` don't match your project's process template. Basic → `Epic/Issue/Task`; Agile → `Epic/User Story/Task`; Scrum → `Epic/Product Backlog Item/Task`. |
| `audit` reports a done parent with open descendants | Azure DevOps does not cascade state, so a closed Epic or Issue leaves its children open. Run `close-children --go`. |
| `audit` reports `Epic count` / `Issues … missing` drift | The board and backlog disagree — run `import --go` then `resync --go` / `resync-tasks --go`, or fix the backlog, then re-audit. |
| `sprints`: `create failed: … TF50309 … Create child nodes` | Your account lacks the **Create child nodes** ACL on the iteration tree (a project-settings permission, not a PAT scope). Have an admin grant it or create the nodes, then run `sprints --go --assign-only`. |
| `sprints`: `TF401347: Invalid tree name … System.IterationPath` | The target iteration node doesn't exist yet — create the sprints first (drop `--assign-only`) or have an admin add them. |
| `board.config.json must set: …` / `Config not found: …` | Required key missing, or you're not in the project dir — pass `-c /path/to/board.config.json`. |
| `ModuleNotFoundError: ado_board_sync` | Package not on the path — `pip install -e .`, or prefix the module form with `PYTHONPATH=src`. |
