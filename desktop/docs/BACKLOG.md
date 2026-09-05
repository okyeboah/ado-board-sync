# ADO Board Sync Desktop Initial Delivery Backlog

**Status:** Approved (2026-09-01) — this backlog is being delivered against; work items exist on the GitHub board and STATUS.md tracks each ticket's state.

This Markdown backlog is the initial source of truth. It follows the same format `ado-board-sync` itself parses, and is a candidate to be reconciled by the CLI onto a board once a target project exists.

## Epic ABSD-100: Product foundation

### ABSD-101 · Create desktop solution and delivery conventions

**Outcome:** A repository skeleton under `desktop/` supports the host, application modules, UI, tests, local development, and documentation, following the same layout conventions as ADO Insights.

**Acceptance criteria:**

- Define repository layout, local run instructions, quality commands, and contribution rules.
- Add a testable configuration model with no credentials in source control.

### ABSD-102 · Port config loader and schema validation

**Outcome:** The Config module loads and validates `board.config.json` against `board.config.schema.json`, with parity tests against the Python config loader.

**Depends on:** ABSD-101.

### ABSD-103 · Implement credential resolution and OS credential store

**Outcome:** The PAT resolves from the OS credential store, then `pat_env`, then `pat_file`, and is never persisted anywhere else.

**Depends on:** ABSD-102.

## Epic ABSD-200: Backlog engine

### ABSD-201 · Port backlog parser

**Outcome:** Parses Epics, Issues, and Tasks from Markdown using the same rules as `parser.py` (`epic_heading_regex`, code prefix, `stop_headings`, top-level bullets as Tasks), with golden-file parity tests.

**Depends on:** ABSD-102.

### ABSD-202 · Port Markdown-to-HTML conversion

**Outcome:** Converts descriptions to the same HTML `htmlfmt.py` produces (bold/italic/code, bullets, tables, `<hr>`, line-wrap and blank-line rules), with parity tests.

**Depends on:** ABSD-201.

### ABSD-203 · Build split-pane editor with live preview

**Outcome:** A source/preview split editor renders live using the ported conversion; inline validation flags malformed markup and blocks Apply.

**Depends on:** ABSD-202.

### ABSD-204 · Port CSV export

**Outcome:** Writes the same import CSV `csvio.py` produces from parsed items, with parity tests.

**Depends on:** ABSD-201.

## Epic ABSD-300: Plan, apply & audit

### ABSD-301 · Port Azure DevOps connector

**Outcome:** A read/write REST connector matching `client.py`'s WIQL, batch get/update, retry/backoff, and API-version behavior.

**Depends on:** ABSD-102.

### ABSD-302 · Implement Plan Builder for import/resync/resync-tasks/dedup/sync

**Outcome:** Computes a typed create/update/delete/unchanged Plan identical to the CLI's dry-run output for the same backlog and config.

**Depends on:** ABSD-203, ABSD-204, ABSD-301.

### ABSD-303 · Implement Apply Executor with stale-plan guard

**Outcome:** Executes a given Plan, rejects a Plan that is stale against the current backlog or board state, and records outcomes.

**Depends on:** ABSD-302.

### ABSD-304 · Implement Audit view

**Outcome:** Reports backlog-vs-board drift and Done-parent/open-descendant drift, matching the CLI's `audit`.

**Depends on:** ABSD-302.

## Epic ABSD-400: Sprint, ownership & closure planning

### ABSD-401 · Implement sprint (iteration) planning view and Plan

**Outcome:** An editable `iterations` table; Plan/Apply for iteration-node creation and Issue/Task assignment, matching `sprints`, `--assign-only`, and `--no-tasks`.

**Depends on:** ABSD-303.

### ABSD-402 · Implement assignee planning view and Plan

**Outcome:** An editable `assignees` table; Plan/Apply for `assign`, matching `--no-tasks` and `--only-unassigned`.

**Depends on:** ABSD-303.

### ABSD-403 · Implement Close-children review and apply

**Outcome:** Lists open descendants of Done items at any depth; supports "assign from done parent," matching `--assign-from-parent`.

**Depends on:** ABSD-304.

## Epic ABSD-500: Operations and delivery

### ABSD-501 · Implement Operation history store

**Outcome:** A SQLite-backed, append-only log of every ApplyRun and its per-item outcomes, viewable per Board profile.

**Depends on:** ABSD-303.

### ABSD-502 · Implement multi-profile registry and switching

**Outcome:** The app holds several Board profiles and switches the active one without restarting; every query, Plan, Apply, and history read is scoped to the active profile, and no view mixes two.

**Depends on:** ABSD-102, ABSD-501.

### ABSD-503 · Add end-to-end parity and acceptance suite

**Outcome:** Fixture backlogs prove the desktop app's Plan/Apply/Audit output matches the CLI's output byte-for-byte, and prove the PRD acceptance criteria, without live Azure DevOps access.

**Depends on:** ABSD-302, ABSD-401, ABSD-402, ABSD-403, ABSD-502.

### ABSD-504 · Detect external backlog and config changes

**Outcome:** A backlog file or `board.config.json` changed on disk outside the app marks the profile stale, blocks editing and Apply, and offers a reload that never discards an unsaved buffer silently.

**Depends on:** ABSD-203.

### ABSD-505 · Add continuous integration

**Outcome:** Every push and pull request runs the Python CLI suite and the .NET build, unit, and parity suites on a clean checkout, so the parity gate cannot be bypassed by a local-only pass.

**Depends on:** ABSD-101.

## Epic ABSD-600: Distribution

### ABSD-601 · Produce an installable desktop package

**Outcome:** A signed, per-user installable package for macOS, Windows, and Linux, produced by a repeatable build script, with documented install and upgrade steps.

**Depends on:** ABSD-503, ABSD-505.

## Epic ABSD-700: Agent-assisted authoring

Let a user prompt an agent CLI they already have installed to draft or revise
backlog items, review the result as a diff, and see its board consequences —
without this app ever holding a provider credential, and without shortening the
Plan/Apply gate.

### ABSD-701 · Add agent provider port and discovery

**Outcome:** An `IAgentProvider` port in Core, with an Infrastructure adapter reporting which agent CLIs are installed on this machine (`claude`, `codex`, `opencode`, `gemini`) and their versions. Each CLI uses its own existing authentication; this app reads, stores and logs no provider credential, exactly as ARCHITECTURE.md §6 requires of the PAT.

**Depends on:** ABSD-106.

### ABSD-702 · Run an agent CLI as a subprocess

**Outcome:** A chosen provider runs with a prompt in the open profile's directory, streaming output, cancellable, with a timeout, and with its exit status mapped to a typed `Error` rather than an exception. The PAT never reaches the child's environment, arguments or stdin.

**Depends on:** ABSD-701, ABSD-107.

### ABSD-703 · Add a prompt surface scoped to the selection

**Outcome:** A prompt box scoped to the selected Epic or Issue, or to the whole backlog, stating before it runs which provider will run, what it can read, and what it may change.

**Depends on:** ABSD-702, ABSD-109.

### ABSD-704 · Review an agent's backlog edit as a diff

**Outcome:** Whatever the agent changed is shown as a diff against the file as it was, accepted or rejected whole, never written silently. Rejecting restores the file byte for byte; accepting re-parses and re-validates before the editor shows it.

**Depends on:** ABSD-703, ABSD-203, ABSD-206.

### ABSD-705 · Plan the board consequences of an agent's draft

**Outcome:** From an accepted agent edit, generate a Plan through the same read-only path as ABSD-302, so the board consequences are visible before any write. Apply still requires its own confirmation; agent involvement never shortens the gate.

**Depends on:** ABSD-704, ABSD-302, ABSD-305.

### ABSD-706 · Record every agent run

**Outcome:** Provider, version, prompt, scope, exit status and whether the edit was accepted, recorded in the same local store as ApplyRuns — so a change in an agent's behaviour is attributable rather than guessed at.

**Depends on:** ABSD-702, ABSD-501.
