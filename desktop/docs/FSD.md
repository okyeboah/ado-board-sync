# Functional Specification: ADO Board Sync Desktop

**Status:** Approved — delivered against in place; changes land as tickets

**Date:** 2026-09-01 (rev 2; first approved 2026-08-20)

## 1. Terms

| Term | Meaning |
| --- | --- |
| Board profile | A local reference to one `board.config.json` and the backlog Markdown file it points to. An unsaved profile has no config file yet; its config is complete in memory and its backlog is on disk. |
| Backlog | The Markdown file that is the single source of truth for Epics, Issues, and Tasks. |
| Plan | A typed, computed set of create/update/delete/unchanged operations for a given command, not yet applied. |
| Apply | The explicit action that executes a previously shown Plan against Azure DevOps. |
| Drift | A difference between the backlog and the board, or between a parent's state and its descendants' states. |
| Code | The stable `<PREFIX>-<n>` identity used to match a backlog Issue to a board work item. |
| Conversion | The Markdown-to-HTML transform applied to a description before it is written to Azure DevOps. |
| Description block | The lines between one item's heading and the next heading (or a stop heading, or end of file), as parsed. An item's Task bullets are inside this block. |
| Buffer | The editor's in-memory copy of one item's description block. A dirty buffer differs from the file. |
| Operation history | The local, append-only record of what an Apply changed. |

## 2. Local user model

The desktop application has one local user. It stores one or more Board profiles. Each profile has its own config, backlog path, credential reference, and operation history.

Version 1 has no shared server, remote account, or role-based authorization. Operating-system file and credential-store permissions protect local application data, matching the CLI's existing trust boundary — a PAT with Work Items: Read & Write scope, held locally.

## 3. Functional behavior

### 3.1 Onboarding and Board profile configuration

The first-run screen offers two routes that end in the same schema-validated `BoardConfig`:

1. **Open an existing `board.config.json`.** The file is validated against the same schema the CLI uses. A failure — missing file, schema violation, unreadable backlog — keeps the first-run screen up and reports the failure beside the route that produced it, with a stable error code (§6.1). Once a profile is open, opening a different one goes through the shell's replace semantics instead.
2. **Describe the board.** Organization, project, issue-code prefix, backlog file, optional team. The form composes the same JSON a config file holds and validates it through the same parse, so a saved profile is byte-compatible with a hand-written one. The profile may also be written to disk so the CLI can share it; the PAT is never part of it.

3. **Starter backlog scaffold.** On the form route, when the chosen backlog file does not exist, the app offers to write a starter backlog — one Epic, one Issue with the profile's own prefix, two Tasks, one table — that parses with zero markup problems. The scaffold is opt-out; with it cleared, a missing file is the same error the CLI gives. An existing file is never overwritten. The prefix in the scaffolded headings is exactly the prefix the config will hold (the heading regex is case-sensitive).
4. The app resolves a PAT in this order: a token typed this session, then `pat_env`, then `pat_file` next to the config — the CLI's fallback order, with the session token first. It never writes the PAT to the backlog, logs, exports, or operation history.

### 3.2 Backlog editing and preview

1. The backlog tree shows every Epic and Issue in document order, with markup problems badged per item. Selecting an item opens the split editor: the item's description block as editable source on the left; the rendered preview, or the exact generated HTML, on the right.
2. Editing recomputes, on every keystroke, the same four derived views from the buffer's text — the HTML the connector would send, the preview parsed from that HTML, the Task list mined from top-level `- ` bullets, and the item's markup problems — using the same Core functions the CLI calls. What the user sees is always what this text would send.
3. The preview renders each description exactly as the CLI's conversion would: `**bold**`, `*italics*`, `` `code` ``, `-`/`*` bullets (nested by indentation), pipe tables (row one as header), and `---` as a rule. Line-wrap and blank-line-as-block-boundary rules match the CLI exactly. Authored angle brackets are escaped, never treated as markup.
4. The preview parses the generated markup, never the Markdown source, so the preview cannot disagree with what would be written.
5. **Save** splices every dirty buffer back into the backlog text at the parser's own line ranges, writes the file atomically (a temp file in the same directory, renamed over the original), re-parses, rebuilds the tree, keeps the selection on the same item (matched by level, code, and heading text), and clears the dirty state. Save is explicit (button, Ctrl+S); the app never auto-saves to the file on a keystroke or an idle timer.
6. Splicing preserves everything outside the edited blocks — headings, other items, content after a stop heading — and the file's line-ending style and trailing newline. The blank lines between items are separators: they survive an edit whether or not the user retypes them.
7. If the backlog file changed on disk after the profile was opened, save refuses (§6.1 `backlog.changed_on_disk`), the external edit survives untouched, and the buffer is preserved for a later save.
8. While any buffer is dirty, Plan generation and Apply are refused (§3.3, §3.4): a Plan is computed from the file, and the file is the source of truth. The editor shows an unsaved marker per item and for the whole profile.
9. If the backlog file changes on disk outside the app (edited elsewhere, or by `git checkout`), the app detects the change on the next save attempt and requires a reload (ABSD-504 adds proactive watching).
10. Structural editing — adding or renaming Epics and Issues — is done by editing the headings in the backlog file itself in v1; the editor edits description blocks. This keeps the editor's write path inside the parser's own coordinates.

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
7. Generation is refused while unsaved editor changes exist (`backlog.unsaved`), before any board read is attempted.

### 3.4 Apply

1. Apply executes exactly the Plan shown to the user — the app never recomputes or silently substitutes a new plan at Apply time. If the board changed since the Plan was computed, Apply fails closed and asks the user to regenerate the Plan.
2. Apply reports, per item, the same outcome categories the CLI prints (created, updated, unchanged, failed with reason).
3. `close-children` and `assign` are never included in a `sync` Plan; they are separate, explicitly invoked flows, matching the CLI's exclusion of both from `sync`.
4. Every Apply is recorded in Operation history: timestamp, command, Board profile, item-level outcomes, and the Plan that was executed.
5. Apply is refused while unsaved editor changes exist, even if a confirmation was already given — the confirmation is not a licence to write against a file the user cannot see.

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
2. Lets the user add/remove Issue codes per iteration and add/remove iterations, then writes the change back to `board.config.json` atomically.
3. Generates a Plan for iteration-node creation and Issue/Task assignment, matching the `sprints` command, including `--assign-only` (skip node creation) and `--no-tasks` (Issues only) as Plan options.
4. If a code appears in two iterations, the app flags it and applies the same "earliest listed wins" rule as the CLI.

### 3.8 Assignees

1. Shows the `assignees` config as a table: identity, and the Issue codes they own.
2. Generates a Plan for `assign`, including `--no-tasks` and `--only-unassigned` as Plan options.
3. Already-correct assignments show as Unchanged, matching the CLI's no-op behavior on re-run.

### 3.9 CSV export

1. The app can write the import CSV from the current backlog, matching the CLI's `gen-csv` byte for byte: same four columns (`Work Item Type, Title 1, Title 2, Description`), minimal quoting, doubled quotes, CRLF records, Epics in `Title 1` and Issues in `Title 2`, type names from the config.
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
4. Reloading discards no unsaved editor content silently: the user is told the file changed and chooses between keeping their buffer and taking the version on disk. Until the watcher exists, save's conflict check (§3.2.7) is the external-change safety net.

## 4. Data model

| Entity | Important fields |
| --- | --- |
| BoardProfileRecord | id, configPath, backlogPath, org, project, codePrefix, credentialReference, lastOpenedUtc. |
| ParsedBacklogItem | code (nullable for Epics), kind (Epic/Issue), title, descriptionLines, bullets, descriptionRange (start line inclusive, end line exclusive — the editor's splice coordinates). |
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
| Open Board profile (onboarding) | Config path | On failure: typed error on the first-run screen; on success: the profile is adopted. |
| Create profile from the form | Form fields, scaffold choice, optional save path | A validated config (optionally written to disk) and an open workspace; scaffolds a starter backlog when the file does not exist and the option is ticked. |
| Edit description buffer | Item identity, buffer text | Live recompute of preview, HTML, tasks, and markup problems; the file is untouched. |
| Save backlog | The open profile | Splice dirty buffers, write the file atomically, re-parse, rebuild, preserve selection. Refuses on external change. |
| Write import CSV | Board profile ID, destination | Write the import CSV from the current backlog; no network call. |
| Generate Plan | Board profile ID, command, options | Compute a Plan without mutating Azure DevOps. Refused while unsaved edits exist. |
| Apply Plan | Plan ID | Execute the Plan; record an ApplyRun. Refused while unsaved edits exist or the plan is stale. |
| Run Audit | Board profile ID | Compute and return current AuditFindings. |
| Save iteration config | Board profile ID, iterations | Write `iterations` back to `board.config.json`. |
| Save assignee config | Board profile ID, assignees | Write `assignees` back to `board.config.json`. |
| Read operation history | Board profile ID, filters | Return ApplyRuns and outcomes. |
| Switch Board profile | Board profile ID | Make a profile active; every later call is scoped to it. |
| Reload after external change | Board profile ID | Re-read the backlog and config from disk after a detected change. |

Apply requires a Plan ID from a Plan generated against the currently open backlog and last board read; it is rejected if the backlog has changed since. Errors use a stable code, a safe message (no PAT, no full description bodies unless the user has opted to show them), and reference the affected item code.

### 5.1 Error codes

| Code | Kind | Meaning |
| --- | --- | --- |
| `config.not_found` | NotFound | The config file does not exist. |
| `config.invalid` / schema codes | Validation | The config failed schema or value validation. |
| `backlog.not_found` | NotFound | The backlog file named by the config does not exist. |
| `backlog.unreadable` | SourceFailure | The backlog could not be read. |
| `backlog.changed_on_disk` | Conflict | Save refused: the file changed outside the app since it was opened. |
| `backlog.unsaved` | Validation | Plan/Apply refused: the editor holds changes the file does not. |
| `backlog.unsavable` | SourceFailure | The starter backlog (or a save) could not be written. |
| `csv.unwritten` | SourceFailure | The import CSV could not be written to the chosen path. |
| `markup.invalid` | Validation | `check-html`-equivalent audit found problems; Apply is blocked. |
| `plan.stale_backlog` / `plan.stale_board` | Conflict | The reviewed Plan no longer matches the backlog/board; regenerate. |
| `board.unauthorized` | Authorization | The PAT was rejected (including the redirect-to-sign-in masquerade). |
| `profile.*` | Validation | Onboarding form validation (org/project/prefix/backlog required, save path required or unwritable). |

## 6. Non-functional requirements

1. The desktop parser, HTML conversion, config loader, CSV export, and command logic are verified against the CLI with golden-file parity tests: same backlog and config in, identical CSV/plan/HTML out. Parity runs the real Python modules on every build.
2. Preview re-render completes within 150ms of an edit for a backlog of up to 500 items.
3. Plan generation for a 500-item backlog completes within the same bounds as the CLI's dry-run for an equivalent backlog.
4. The PAT is never written to logs, exports, operation history, or the backlog file.
5. Apply uses the same retry/backoff behavior as the CLI's REST client (`max_retries`, `backoff`, `timeout` from config); a create's parent-link write is never retried.
6. The app writes structured local audit events for every Plan generation and Apply.
7. File writes to the backlog and to `board.config.json` are atomic (write-temp-then-rename) to avoid partial writes on crash.
8. The app detects and refuses to Apply a stale Plan (backlog or board changed since generation), and refuses to Save over an external change.

## 7. Open decisions

1. ~~Should the desktop app be able to create a new Board profile from scratch?~~ **Resolved 2026-09-01:** yes — the form composes a profile and can scaffold a starter backlog (PRD-AC-20).
2. Should `dedup`'s Plan let the user choose which duplicate survives, or always keep the CLI's existing "first wins" rule?
3. How should the preview pane represent `stop_headings` content — hidden, or shown but visually marked as excluded from parsing?
4. Should Operation history sync across machines for the same Board profile, or stay strictly local per install?
5. Should the editor grow structural editing (adding/renaming Issues in the buffer), or stay description-block-only with headings edited in the file? §3.2.10 states the v1 answer; revisit for R4.
