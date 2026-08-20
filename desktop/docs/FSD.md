# Functional Specification: ADO Board Sync Desktop

**Status:** Draft

**Date:** 2026-08-20

## 1. Terms

| Term | Meaning |
| --- | --- |
| Board profile | A local reference to one `board.config.json` and the backlog Markdown file it points to. |
| Backlog | The Markdown file that is the single source of truth for Epics, Issues, and Tasks. |
| Plan | A typed, computed set of create/update/delete/unchanged operations for a given command, not yet applied. |
| Apply | The explicit action that executes a previously shown Plan against Azure DevOps. |
| Drift | A difference between the backlog and the board, or between a parent's state and its descendants' states. |
| Code | The stable `<PREFIX>-<n>` identity used to match a backlog Issue to a board work item. |
| Conversion | The Markdown-to-HTML transform applied to a description before it is written to Azure DevOps. |
| Operation history | The local, append-only record of what an Apply changed. |

## 2. Local user model

The desktop application has one local user. It stores one or more Board profiles. Each profile has its own config, backlog path, credential reference, and operation history.

Version 1 has no shared server, remote account, or role-based authorization. Operating-system file and credential-store permissions protect local application data, matching the CLI's existing trust boundary — a PAT with Work Items: Read & Write scope, held locally.

## 3. Functional behavior

### 3.1 Board profile configuration

The local user adds a Board profile by pointing to a `board.config.json`. The app validates it against the same schema the CLI uses (`board.config.schema.json`): required keys (`org`, `project`, `code_prefix`), and defaults for `board_file`, `csv_file`, `types`, `states`, `epic_heading_regex`, `stop_headings`, `api_version`, `pat_env`, `pat_file`, `task_title_max`, `team`, `iterations`, `assignees`, `max_retries`, `backoff`, `timeout`.

The app resolves a PAT in this order: an OS credential store entry for the profile, then `pat_env`, then `pat_file` next to the config — the same fallback the CLI uses, plus the credential store as a desktop-native option. It never writes the PAT to the backlog, logs, exports, or operation history.

### 3.2 Backlog editing and preview

1. The editor shows the backlog Markdown as source text on one side and a live-rendered preview on the other.
2. The preview renders each description exactly as the CLI's conversion would: `**bold**`, `*italics*`, `` `code` ``, `-`/`*` bullets (nested by indentation), pipe tables (row one as header), and `---` as a rule. Line-wrap and blank-line-as-block-boundary rules match the CLI exactly.
3. Structural elements — Epic headings matched by `epic_heading_regex`, `### <PREFIX>-<n> · <title>` Issue headings, top-level `-` bullets as Tasks, `stop_headings` — are highlighted distinctly from free-text description content.
4. On every edit, the app re-parses the backlog and re-runs validation inline; malformed markup that would fail `check-html` is flagged at its location and blocks Apply for any plan touching that item.
5. Saving writes the edited Markdown back to the backlog file on disk. The app does not maintain a separate authoritative copy — the file is always what the next parse reads, matching the CLI's "the backlog is the single source of truth" behavior.
6. If the backlog file changes on disk outside the app (edited elsewhere, or by `git checkout`), the app detects the change and requires the user to reload before editing further, to avoid silently overwriting external edits.

### 3.3 Plan generation

Every mutating command in the application produces a Plan first. There is no
second mechanism: sprints (§3.7), assignees (§3.8), and close-children (§3.6)
are Plan-producing commands like any other, and differ only in which command
options they offer and in being excluded from the `sync` chain.

1. The user selects a command. The Plan-producing commands are:

   | Command | Scope | In the `sync` chain |
   | --- | --- | --- |
   | `import` | Create missing Epics/Issues | yes |
   | `resync` | Epic/Issue titles + descriptions | yes |
   | `resync-tasks [CODE]` | Child Tasks of every Issue, or of one Issue when a code is given | yes (unscoped form) |
   | `dedup` | Delete duplicate work items | no |
   | `sync` | The structural reconcile chain | — |
   | `sync-one CODE` | One Issue and its iteration only; never Tasks or assignees | no |
   | `sprints` | Iteration nodes and Issue/Task iteration paths | no |
   | `assign` | Issue and child-Task owners | no |
   | `close-children` | Open descendants of a Done item | no |

2. The app parses the current backlog, reads the current board state, and computes a Plan: one row per affected item, each labeled Create, Update, Delete, or Unchanged, with a field-level diff for Update rows (title, description, task list).
3. The Plan is computed the same way the CLI computes its dry-run output; it is never shown until it has been fully computed, and it is never partially applied.
4. `sync`'s Plan reflects its documented order — `gen-csv → check-html → import → resync → resync-tasks → audit` — and aborts before showing a Plan for any write step if `check-html` fails.
5. Generating a Plan makes no mutating request to Azure DevOps.
6. `close-children` and `assign` are never folded into a `sync` Plan, matching the CLI's exclusion of both from `sync`: closing an item or setting an owner is a workflow decision, not a structural reconcile.

### 3.4 Apply

1. Apply executes exactly the Plan shown to the user — the app never recomputes or silently substitutes a new plan at Apply time. If the board changed since the Plan was computed, Apply fails closed and asks the user to regenerate the Plan.
2. Apply reports, per item, the same outcome categories the CLI prints (created, updated, unchanged, failed with reason).
3. `close-children` and `assign` are never included in a `sync` Plan; they are separate, explicitly invoked flows, matching the CLI's exclusion of both from `sync`.
4. Every Apply is recorded in Operation history: timestamp, command, Board profile, item-level outcomes, and the Plan that was executed.

### 3.5 Audit

1. Audit is read-only. It reports the same two drift categories the CLI's `audit` reports: backlog-vs-board mismatch (missing/extra Epics or Issues, title/description drift) and state-hierarchy drift (a Done parent with an open descendant, at any depth).
2. Audit never triggers a write. Clearing hierarchy drift requires the user to explicitly run Close-children.
3. Audit can be run at any time and does not require a prior Plan.

### 3.6 Close-children

1. Shows every open descendant of an already-Done work item — Epic → Issue → Task, at any depth.
2. Offers "assign from done parent": copies the done ancestor's assignee onto each closed item that is currently unassigned, never overwriting an existing assignee — matching `--assign-from-parent`.
3. Requires an explicit Apply, separate from any `sync` Plan.

### 3.7 Sprints (iterations)

1. Shows the `iterations` config as a table: name, start, finish, and assigned Issue codes.
2. Lets the user add/remove Issue codes per iteration and add/remove iterations, then writes the change back to `board.config.json`.
3. Generates a Plan for iteration-node creation and Issue/Task assignment, matching the `sprints` command, including `--assign-only` (skip node creation) and `--no-tasks` (Issues only) as Plan options.
4. If a code appears in two iterations, the app flags it and applies the same "earliest listed wins" rule as the CLI.

### 3.8 Assignees

1. Shows the `assignees` config as a table: identity, and the Issue codes they own.
2. Generates a Plan for `assign`, including `--no-tasks` and `--only-unassigned` as Plan options.
3. Already-correct assignments show as Unchanged, matching the CLI's no-op behavior on re-run.

### 3.9 CSV export

1. The app can write the import CSV from the current backlog, matching the CLI's `gen-csv`.
2. The CSV is an artifact, never a source of truth: Plan generation reads the backlog directly, so a stale CSV can never change what a Plan does. It exists because the Azure DevOps web importer consumes it, and because a team may want the file under review before any board write.
3. Writing the CSV touches no Azure DevOps endpoint and needs no PAT.

### 3.10 Operation history

1. Every Apply appends an ApplyRun with its per-item outcomes; the history is append-only and never rewritten by a later run.
2. The user can read history filtered by Board profile, command, and date, and can open the Plan that a given run executed.
3. History records item codes, operations, and outcomes. It never records a PAT, and it does not store full description bodies.
4. History is local to the installation. It is not synchronised between machines in version 1.

### 3.11 Multi-profile switching and external change detection

1. The app holds a registry of Board profiles; the user switches the active profile without restarting.
2. Every query, Plan, Apply, and history read is scoped to the active profile. No view mixes records from two profiles.
3. The app watches the active backlog file and `board.config.json`. When either changes on disk outside the app, it marks the profile stale, blocks further editing and Apply, and offers a reload.
4. Reloading discards no unsaved editor content silently: the user is told the file changed and chooses between keeping their buffer and taking the version on disk.

## 4. Data model

| Entity | Important fields |
| --- | --- |
| BoardProfileRecord | id, configPath, backlogPath, org, project, codePrefix, credentialReference, lastOpenedUtc. |
| ParsedBacklogItem | code (nullable for Epics), kind (Epic/Issue/Task), title, descriptionMarkdown, descriptionHtml, parentCode, sourceRange. |
| Plan | id, boardProfileId, command, generatedAtUtc, backlogHash, boardStateHash. |
| PlanItem | planId, itemCode, operation (Create/Update/Delete/Unchanged), fieldDiffs. |
| ApplyRun | id, planId, startedAtUtc, completedAtUtc, status (Succeeded/Partial/Failed). |
| ApplyOutcome | applyRunId, itemCode, result (Created/Updated/Unchanged/Failed), failureReason. |
| AuditFinding | boardProfileId, kind (BacklogDrift/HierarchyDrift), subjectCode, evidence, observedAtUtc. |
| IterationConfigEntry | boardProfileId, name, start, finish, itemCodes. |
| AssigneeConfigEntry | boardProfileId, identity, itemCodes. |

`ParsedBacklogItem` and the `Plan`/`PlanItem` pair are always derived from the current backlog file and current board read; the app persists only `ApplyRun`/`ApplyOutcome` (history) and `AuditFinding` (last observed) locally. It never persists a shadow copy of the backlog or the board as its source of truth.

## 5. Local application contract

| Operation | Input | Result |
| --- | --- | --- |
| Open Board profile | Config path | Validate config/schema, resolve credential, parse backlog. |
| Save backlog | Board profile ID, edited Markdown | Write to the backlog file; re-parse; re-validate. |
| Write import CSV | Board profile ID | Write the import CSV from the current backlog; no network call. |
| Generate Plan | Board profile ID, command, options | Compute a Plan without mutating Azure DevOps. |
| Apply Plan | Plan ID | Execute the Plan; record an ApplyRun. |
| Run Audit | Board profile ID | Compute and return current AuditFindings. |
| Save iteration config | Board profile ID, iterations | Write `iterations` back to `board.config.json`. |
| Save assignee config | Board profile ID, assignees | Write `assignees` back to `board.config.json`. |
| Read operation history | Board profile ID, filters | Return ApplyRuns and outcomes. |
| Switch Board profile | Board profile ID | Make a profile active; every later call is scoped to it. |
| Reload after external change | Board profile ID | Re-read the backlog and config from disk after a detected change. |

Apply requires a Plan ID from a Plan generated against the currently open backlog and last board read; it is rejected if the backlog has changed since. Errors use a stable code, a safe message (no PAT, no full description bodies unless the user has opted to show them), and reference the affected item code.

## 6. Non-functional requirements

1. The desktop parser, HTML conversion, config loader, and command logic are verified against the CLI with golden-file parity tests: same backlog and config in, identical CSV/plan/HTML out.
2. Preview re-render completes within 150ms of an edit for a backlog of up to 500 items.
3. Plan generation for a 500-item backlog completes within the same bounds as the CLI's dry-run for an equivalent backlog.
4. The PAT is never written to logs, exports, operation history, or the backlog file.
5. Apply uses the same retry/backoff behavior as the CLI's REST client (`max_retries`, `backoff`, `timeout` from config).
6. The app writes structured local audit events for every Plan generation and Apply.
7. File writes to the backlog and to `board.config.json` are atomic (write-temp-then-rename) to avoid partial writes on crash.
8. The app detects and refuses to Apply a stale Plan (backlog or board changed since generation).

## 7. Open decisions

1. Should the desktop app be able to create a new Board profile from scratch (scaffold `board.config.json` plus an empty backlog), or only open an existing one in v1?
2. Should `dedup`'s Plan let the user choose which duplicate survives, or always keep the CLI's existing "first wins" rule?
3. How should the preview pane represent `stop_headings` content — hidden, or shown but visually marked as excluded from parsing?
4. Should Operation history sync across machines for the same Board profile, or stay strictly local per install?
