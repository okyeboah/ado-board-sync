# MECE Audit and Repair Record

**Status:** Repaired

**Audited:** 2026-08-20

The planning package was audited for MECE structure: every requirement in exactly
one place (mutually exclusive) and nothing required left unstated (collectively
exhaustive). Ten findings were confirmed against the files and repaired.

## Findings and repairs

| # | Severity | Finding | Repair |
| --- | --- | --- | --- |
| 1 | Overlap | FSD §3.3 enumerated the commands a Plan covers and excluded sprints and assignees, yet §3.7 and §3.8 each said they "generate a Plan" — two inconsistent definitions of the core concept. | §3.3 now states that every mutating command produces a Plan, and carries one table listing all nine Plan-producing commands and which are in the `sync` chain. |
| 2 | Overlap | Epic ABSD-400 "lifecycle operations" and Epic ABSD-500 "Operations and delivery" both claimed "operations". | ABSD-400 renamed to "Sprint, ownership & closure planning". |
| 3 | Critical gap | No traceability matrix existed, so no requirement could be shown as implemented or tested. | Added `docs/TRACEABILITY.md` mapping all 17 criteria to specification, ticket, and test, with an explicit gate for each enabling ticket. |
| 4 | Critical gap | Sprints (ABSD-401) and assignees (ABSD-402) were in scope with no acceptance criterion, making the definition-of-done rule unsatisfiable for both. | Added PRD-AC-11 and PRD-AC-12. |
| 5 | Critical gap | CSV export (ABSD-204) was a ticket with no PRD scope line and no FSD behaviour. | Added the PRD scope line, FSD §3.9, the contract row, and PRD-AC-16. |
| 6 | Critical gap | No CI existed anywhere in the repository, so the parity suite — the safety mechanism the whole design rests on — never ran except on a developer machine. | Added `.github/workflows/build-and-test.yml` and ticket ABSD-505. |
| 7 | Minor gap | FSD §3 had no section for operation history or multi-profile behaviour, though §5's contract and ABSD-501/502 both required them. | Added §3.10 and §3.11, plus three contract rows. |
| 8 | Minor gap | Architecture §2 had no module for the profile registry or for detecting an external file change. | Added the Board Profile Registry and Backlog Watcher modules. |
| 9 | Minor gap | `sync-one` appeared in the FSD but not the PRD; `resync-tasks [CODE]`, which exists in the CLI, appeared in neither. | Both are now in the PRD scope and in the FSD §3.3 command table. |
| 10 | Minor gap | Nothing described how a user obtains the application; release slices ended before distribution. | Added release slice R5, Epic ABSD-600, ticket ABSD-601, and PRD-AC-17. |

## Structure after repair

```text
ADO Board Sync Desktop
├── ABSD-100 Product foundation
│   ├── ABSD-101 Solution and delivery conventions
│   ├── ABSD-102 Config loader and schema validation
│   └── ABSD-103 Credential resolution and OS credential store
├── ABSD-200 Backlog engine
│   ├── ABSD-201 Backlog parser
│   ├── ABSD-202 Markdown-to-HTML conversion
│   ├── ABSD-203 Split-pane editor with live preview
│   └── ABSD-204 CSV export
├── ABSD-300 Plan, apply & audit
│   ├── ABSD-301 Azure DevOps connector
│   ├── ABSD-302 Plan Builder
│   ├── ABSD-303 Apply Executor with stale-plan guard
│   └── ABSD-304 Audit view
├── ABSD-400 Sprint, ownership & closure planning
│   ├── ABSD-401 Sprint planning view and Plan
│   ├── ABSD-402 Assignee planning view and Plan
│   └── ABSD-403 Close-children review and apply
├── ABSD-500 Operations and delivery
│   ├── ABSD-501 Operation history store
│   ├── ABSD-502 Multi-profile registry and switching
│   ├── ABSD-503 End-to-end parity and acceptance suite
│   ├── ABSD-504 External backlog and config change detection
│   └── ABSD-505 Continuous integration
└── ABSD-600 Distribution
    └── ABSD-601 Installable desktop package
```

Every delivery ticket now maps to either a PRD acceptance criterion or a named
enabling gate in `docs/TRACEABILITY.md`, and every acceptance criterion maps to a
ticket. No ticket appears under two epics.

## Standing rule

Re-run this audit whenever a release slice closes, or whenever a ticket is added
without a criterion. A ticket with neither an acceptance criterion nor a named
enabling gate is the failure mode this record exists to prevent.
