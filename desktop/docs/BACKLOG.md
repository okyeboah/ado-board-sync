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

### ABSD-104 · Land the Avalonia desktop host so the solution runs

**Outcome:** `AdoBoardSync.Desktop` builds an executable that opens a window, ships a runnable sample Board profile so the first click succeeds, opens a profile through a file picker, and shows the Epic/Issue/Task tree `gen-csv` would produce — so the solution has its first runnable entry point.

Its primary gate is an automated launch smoke test, because a mistyped `StaticResource` key still builds with zero warnings and still passes every other test: compiled bindings validate bindings, not resource lookups. The window criterion is a liveness check rather than an exit code, since a XAML load failure also exits 0.

**Depends on:** ABSD-101. Full acceptance criteria: issue #28.

### ABSD-105 · Centralise desktop build properties and package versions

**Outcome:** `desktop/Directory.Build.props` and `desktop/Directory.Packages.props` state the target framework, the warning policy and every package version once, so a project added later inherits the quality gate instead of restating it and the Avalonia packages cannot drift apart. `desktop/global.json` pins the SDK feature band, and CI reads that same file.

**Depends on:** ABSD-104. Full acceptance criteria: issue #29.

### ABSD-108 · Add a headless UI test harness for the views

**Outcome:** A headless suite runs Avalonia views inside the existing `dotnet test` invocation, so a window, a binding, a control and a dispatcher can be asserted on a runner with no display server, and every later UI ticket has a gate a reviewer can run. A run that reports zero tests from the headless assembly fails the ticket — under `--no-build`, "No test is available" is otherwise indistinguishable from a pass.

**Depends on:** ABSD-105. Full acceptance criteria: issue #30.

### ABSD-110 · Show credential status and gate board actions

**Outcome:** A badge names which credential source resolved the PAT — the OS credential store, `pat_env`, or `pat_file` — and every board-reading and board-writing action is disabled, with all three sources listed, when none resolves. Offline work stays available throughout.

**Depends on:** ABSD-103, ABSD-107. Full acceptance criteria: issue #34.

### ABSD-111 · Reconcile the delivery documents and the board with the shipped app

**Outcome:** `STATUS.md`, `TRACEABILITY.md`, `AGENTS.md` and `GITHUB-PROJECT.md` describe the application that now exists — including the host, the UI, the seventh epic and the non-functional requirements — and a markdownlint gate keeps them honest.

**Depends on:** ABSD-104. Full acceptance criteria: issue #35.

### ABSD-112 · Onboard a Board profile without a config file

**Outcome:** A first run offers two equal routes into a Board profile: open an existing `board.config.json`, or describe the organisation, project, issue code prefix and backlog file in the app. The second route produces the same schema-validated `BoardConfig` as the first, and saving to disk is optional — so a first-time user is never blocked behind hand-writing a config file.

**Depends on:** ABSD-104. Full acceptance criteria: issue #27.

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

### ABSD-306 · Build the Audit view and its Close-children handoff

**Outcome:** The Audit section renders one finding card per drift item — subject, evidence and kind — from the computed audit, runs on demand without a prior Plan, never triggers a write, and hands a hierarchy-drift finding to the Close-children review. Clearing hierarchy drift is never offered inline; it requires the explicit Close-children Apply.

**Depends on:** ABSD-304, ABSD-109, ABSD-108. Full acceptance criteria: issue #40.

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

### ABSD-506 · Extend continuous integration to the desktop application

**Outcome:** Every push and pull request builds and tests the desktop application on macOS, Windows and Linux, runs the headless UI suite, and verifies that the application launches a window on each platform. This ticket owns the tri-platform launch claim cut from ABSD-104, which had no mechanism to prove it; exit code 0 is explicitly not accepted as evidence.

**Depends on:** ABSD-108. Full acceptance criteria: issue #41.

### ABSD-507 · Emit structured diagnostics for Plan generation, Apply and file writes

**Outcome:** The application writes structured local events for every profile load, Plan generation, Apply, Audit run, connector retry and file write, and exports a diagnostics bundle that contains no PAT and no description body. The logging port lives outside Core, which keeps its zero-`PackageReference` csproj.

**Depends on:** ABSD-103, ABSD-106, ABSD-303. Full acceptance criteria: issue #42.

### ABSD-508 · Build the operation history timeline and scope it to the active profile

**Outcome:** Operation history is rendered as a reverse-chronological timeline of Apply runs with their per-item outcomes and run status, filterable by command and date, and every read and write is scoped to the active Board profile so switching never shows another profile's runs. The schema stores item codes, operations, outcomes and timestamps only.

**Depends on:** ABSD-501, ABSD-502, ABSD-108, ABSD-109. Full acceptance criteria: issue #43.

## Epic ABSD-600: Distribution

### ABSD-601 · Produce an installable desktop package

**Outcome:** A signed, per-user installable package for macOS, Windows, and Linux, produced by a repeatable build script, with documented install and upgrade steps.

**Depends on:** ABSD-503, ABSD-505.

### ABSD-602 · Publish a self-contained local build

**Outcome:** One documented command produces a self-contained build that starts on a machine with no .NET SDK present, and a matrix job publishes the macOS, Windows and Linux outputs as artifacts — so the app can be handed to someone else long before signing exists. The pipeline produces clearly unsigned artifacts when no signing secret is present, so it is exercisable before certificates exist.

**Depends on:** ABSD-104, ABSD-505. Full acceptance criteria: issue #44.

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
