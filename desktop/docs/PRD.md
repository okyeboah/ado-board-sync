# Product Requirements Document: ADO Board Sync Desktop

**Status:** Draft

**Date:** 2026-08-20

## 1. Problem

Reconciling an Azure DevOps board to a Markdown backlog today means editing the file blind, then running one of the CLI's commands and reading a text plan in a terminal. There is no way to see how a description will render on the board before writing it, no visual diff of what a sync will create, update, or delete, and no guided place to manage sprints, assignees, or hierarchy drift outside raw JSON and CLI flags.

## 2. Product goal

Provide a desktop companion to `ado-board-sync` that lets a user author the Markdown backlog with a live preview of its rendered Azure DevOps description, preview every board mutation as an explicit plan, and apply that plan only on confirmation — using the exact same backlog format, config, and reconcile semantics as the CLI.

## 3. Users

| User | Need |
| --- | --- |
| Backlog author | Edit the Markdown backlog and see exactly how each description will render on the board before syncing. |
| Delivery/Scrum lead | Review a plan (creates/updates/deletes) before it touches the board, and clear hierarchy drift deliberately. |
| Engineering manager | Manage sprint and assignee mapping without hand-editing JSON. |
| New team member | Learn the backlog format and commands without memorizing CLI flags. |
| Auditor | Confirm the board matches the backlog and see exactly what the last apply changed. |

## 4. Success measures

1. A user can open a Board profile (config + backlog) and see a live-rendered preview of every description in under one second per item.
2. Every board mutation is shown as a typed plan (create/update/delete/unchanged counts) before any write, matching the CLI's dry-run output exactly.
3. No write reaches Azure DevOps without an explicit Apply confirmation — there is no autosave-to-board and no scheduled write.
4. A backlog saved from the desktop editor produces an identical CSV/plan to running the CLI against the same file (parity, not reinterpretation).
5. `check-html`-equivalent validation surfaces malformed markup inline, in the editor, before Apply is offered.
6. The Audit view reports the same drift the CLI's `audit` command would report, from the same backlog and board state.

## 5. Scope

### In scope for version 1

- Open a Board profile: `board.config.json` and the Markdown backlog it points to.
- Backlog Markdown editor with a live preview pane rendered by the same conversion rules as the CLI's description conversion.
- Inline validation of malformed markup — the desktop equivalent of `check-html` — before any write is offered.
- Plan generation for every mutating command — `import`, `resync`, `resync-tasks [CODE]`, `dedup`, `sync`, `sync-one CODE`, `sprints`, `assign`, and `close-children` — shown as a typed diff (create/update/delete/unchanged) before Apply.
- Writing the import CSV from the backlog, matching `gen-csv`, for the Azure DevOps web importer and for review before a board write.
- An explicit Apply step that executes a previously shown plan — never a silent write.
- A read-only Audit view: backlog-vs-board drift and hierarchy state drift (a Done parent with open descendants), matching the CLI's `audit`.
- Close-children review and apply, including the "assign from done parent" option.
- A sprint (iteration) planning view backed by the `iterations` config.
- An assignee planning view backed by the `assignees` config.
- Operation history: a local, append-only log of what each Apply changed and when.
- A PAT stored in OS credential storage; the app also recognizes an existing `AZURE_DEVOPS_PAT` env var or `.ado_pat` file for projects already set up for CLI use.

### Out of scope for version 1

- Any interpretation of the backlog that diverges from the CLI's parser — the desktop app is a second surface over the same format, not a new one.
- Cross-organization or cross-project analytics/reporting (that is ADO Insights' scope, not this app's).
- Multi-user, shared-server, or unattended background sync — the app only writes while open and a human clicks Apply.
- A GUI wizard for editing `board.config.json`'s org/project/PAT-source keys beyond validating and displaying them (advanced config edits stay in the JSON file in v1).
- Creating or restructuring Azure DevOps process templates or field configuration.
- Real-time collaborative editing of the backlog file.
- Auto-update from a hosted feed. R5 ships an installable package; upgrading is a deliberate re-install in version 1.

## 6. Product principles

1. The Markdown backlog stays the single source of truth; the app never keeps a shadow copy that can drift silently from the file on disk.
2. Every write is preceded by a plan and requires an explicit Apply — the desktop app never removes the CLI's dry-run/`--go` gate, it visualizes it.
3. The desktop parser and the CLI parser agree on every backlog byte-for-byte; parity is verified, not assumed.
4. The PAT never touches the repo, logs, or exported files.
5. Closing a work item or reassigning ownership is a reviewed decision, shown separately from structural sync, exactly as the CLI excludes `close-children` and `assign` from `sync`.
6. The preview pane renders precisely what Azure DevOps will render — no generic Markdown renderer, no divergence from the actual conversion rules.

## 7. Release slices

| Release | Outcome |
| --- | --- |
| R1: Desktop foundation | Open a Board profile, parse and display the backlog tree read-only, validate config and credentials. |
| R2: Backlog editor | Markdown source editor with live preview and inline `check-html`-equivalent validation; save to disk. |
| R3: Plan & apply | Visual plan/diff for import/resync/resync-tasks/dedup/sync, with an explicit Apply step; Audit view. |
| R4: Sprints, assignees & operations | Sprint and assignee planning views, close-children review/apply, operation history log. |
| R5: Distribution | A signed, installable per-user package for macOS, Windows, and Linux, with documented install and upgrade steps. |

## 8. Acceptance criteria

| ID | Criterion |
| --- | --- |
| PRD-AC-01 | Given a valid Board profile, when the user opens it, then the app parses the backlog with the same rules as the CLI and shows the same Epic/Issue/Task tree `gen-csv` would produce. |
| PRD-AC-02 | Given an edited description, when the user views the preview pane, then it shows the same HTML the CLI's conversion would write to Azure DevOps. |
| PRD-AC-03 | Given malformed markup, when the user edits the backlog, then the app flags it inline and blocks Apply until it is resolved, matching `check-html`'s pass/fail rule. |
| PRD-AC-04 | Given a backlog and a board, when the user requests a plan, then the app shows exact create/update/delete/unchanged counts before any write occurs. |
| PRD-AC-05 | Given a shown plan, when the user has not clicked Apply, then no request that mutates Azure DevOps has been sent. |
| PRD-AC-06 | Given a Done parent with an open descendant, when the user opens Audit, then the app reports the exact parent and descendant items, matching `audit`. |
| PRD-AC-07 | Given the same backlog and config, when the desktop app and the CLI each compute a plan, then the plans are identical. |
| PRD-AC-08 | Given a completed Apply, when the user opens Operation History, then it shows what changed, when, and the outcome of every affected item. |
| PRD-AC-09 | Given a Close-children review, when the user applies it, then already-assigned items keep their assignee and only unassigned items inherit the done ancestor's assignee, matching `--assign-from-parent`. |
| PRD-AC-10 | Given no PAT is resolved, when the user opens a Board profile, then the app blocks any board-reading or board-writing action and states which credential sources it checked. |
| PRD-AC-11 | Given an `iterations` configuration, when the user applies a sprint Plan, then each listed Issue and (unless tasks are excluded) its child Tasks carry that iteration path, and a code listed in two sprints resolves to the earliest listed one. |
| PRD-AC-12 | Given an `assignees` configuration, when the user applies an assignee Plan, then each listed Issue and (unless tasks are excluded) its child Tasks carry that owner, already-correct assignments report Unchanged, and a code listed under two identities resolves to the first listed one. |
| PRD-AC-13 | Given a Plan generated earlier, when the backlog or the board has changed since it was computed, then Apply is refused and the user is asked to regenerate the Plan. |
| PRD-AC-14 | Given two local Board profiles, when the user switches profile, then no view mixes backlog items, plans, history, or credentials between them. |
| PRD-AC-15 | Given the backlog file is changed on disk outside the application, when the user next edits or applies, then the app reports the external change and requires an explicit reload before continuing. |
| PRD-AC-16 | Given a backlog, when the user writes the import CSV, then it is byte-for-byte identical to the CSV the CLI's `gen-csv` writes for the same backlog and config. |
| PRD-AC-17 | Given a supported operating system, when a user installs the released package, then the application starts and opens an existing Board profile without a developer toolchain present. |

## 9. Deferred decisions

| Decision | Options | Default for draft | Revisit trigger |
| --- | --- | --- | --- |
| Desktop engine implementation | Full .NET port of parser/config/client/commands, or a thin process wrapper over the Python CLI | Full .NET port, parity verified by golden-file tests against the CLI | Resolved 2026-08-19 |
| Credential storage | OS credential store only, or env var/file only | OS credential store, with env var/`.ado_pat` file recognized for existing projects | Before R1 implementation |
| Config editing | Read/validate only, or a full GUI editor for `board.config.json` | Read/validate only in v1 | Before R4 |
| Multi-profile support | One Board profile open at a time, or multiple profiles with a switcher | Multiple profiles, mirroring ADO Insights' Organization profiles | Before R1 implementation |
| Live ADO validation while typing (e.g., confirming `types`/`states` names exist) | On every keystroke, or on demand only | On demand only | Before R3 |
| Operation history storage | SQLite, or a flat file | SQLite | Before R4 implementation |
