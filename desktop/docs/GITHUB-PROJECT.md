# GitHub Project Import Blueprint

**Status:** Imported — Project #2 (`PVT_kwHOEHRTN84Bg7tk`), 50 Issues. This document is now the
reconciliation checklist: the rules below are re-runnable against the live board, and the import
sequence records how it was built.

## Source of truth

`desktop/docs/BACKLOG.md` is the source of truth for initial GitHub Issues. Create one Issue for every `ABSD-NNN` heading. Do not create an Issue for a dependency reference alone.

## Repository and Project

| Setting | Value |
| --- | --- |
| Repository | `ado-board-sync` (this repository; issues live alongside the existing CLI issues). |
| Project title | `ADO Board Sync Desktop Delivery` |
| Project description | Desktop companion app delivery plan: backlog editor, plan/apply, audit, sprints, assignees. |
| Item source | GitHub Issues in this repository, labeled `app:desktop` to separate them from CLI issues. |

## Project fields

| Field | Type | Allowed values | Initial value |
| --- | --- | --- | --- |
| GitHub Status | System field | Todo, In Progress, Done | Todo |
| Delivery state | Single select | Backlog, Ready, In Progress, In Review, Blocked, Done | Backlog |
| Epic | Single select | Foundation, Backlog Engine, Plan & Apply, Lifecycle Ops, Operations, Distribution, Agent Assist | From ticket prefix. |
| Priority | Single select | Must, Should, Could | Unset until product owner prioritizes. |
| Dependency state | Single select | None, Waiting, Satisfied | Waiting when a listed dependency is not Done. |
| Requirement | Text | PRD-AC identifier | From the PRD's acceptance criteria table, if referenced. |

## Labels

| Label | Meaning |
| --- | --- |
| `app:desktop` | Any ADO Board Sync Desktop issue (distinguishes it from existing CLI issues in this repo). |
| `area:foundation` | ABSD-100 Epic. |
| `area:backlog-engine` | ABSD-200 Epic. |
| `area:plan-apply` | ABSD-300 Epic. |
| `area:lifecycle-ops` | ABSD-400 Epic. |
| `area:operations` | ABSD-500 Epic. |
| `area:distribution` | ABSD-600 Epic. |
| `area:agents` | ABSD-700 Epic. |
| `type:epic` | An ABSD-x00 Epic heading. |
| `type:delivery` | An implementable ABSD ticket. |
| `status:partial` | A ticket whose Outcome is partly delivered; the remainder is named in `STATUS.md`. |
| `status:decision-needed` | A ticket blocked by an unresolved item in the PRD's Deferred decisions or the FSD's Open decisions. |

## Import sequence

1. Push `desktop/docs/` to the repository before creating Issues.
2. Create one Epic Issue per Epic heading — seven: ABSD-100 through ABSD-700.
3. Create the delivery Issues from ABSD-101 through ABSD-706.
4. Apply `app:desktop`, one `area:*` label, and one `type:*` label to every Issue.
5. Add every Issue to `ADO Board Sync Desktop Delivery` with the field values above.
6. Add the dependency references from `desktop/docs/BACKLOG.md` to each delivery Issue body.
7. Link PRD acceptance criteria (PRD-AC-01 through PRD-AC-10) in the matching Issue body where a ticket implements one.
8. Set each Issue's GitHub Status to `Todo` and Delivery state to `Backlog`. Do not mark a ticket `Ready` until its prerequisite Issues are Done and any required decision is resolved.
9. Read back Issue title, labels, Project fields, and dependencies; compare them to this document and `desktop/docs/BACKLOG.md`.

> **Changing a Project single-select field replaces its whole option set.**
> `updateProjectV2Field` reissues every option ID, which clears that field on every
> existing item. Adding the Agent Assist option to Epic wiped Epic on all 44 items
> and they had to be restored from each Issue's `area:*` label. Read the field back
> after any such change, and restore before doing anything else.

## Import integrity rules

1. Each ABSD code exists exactly once as a GitHub Issue.
2. Each delivery Issue has exactly one area label, `type:delivery`, and `app:desktop`.
3. Each Epic Issue has exactly one area label, `type:epic`, and `app:desktop`.
4. Each Issue is in the ADO Board Sync Desktop Delivery Project exactly once.
5. Every dependency named in the backlog is visible in the Issue body.
6. No source issue is marked Done during import.

## Vocabulary mapping (added 2026-09-01)

STATUS.md's delivery vocabulary and the Project's Delivery state are two views
of one truth. When they disagree, **STATUS.md is authoritative**; fix the board
in the same change that fixes the row.

| STATUS.md state | Project Delivery state |
| --- | --- |
| Not started | Backlog |
| Partial | In Progress |
| Done | Done |

`Ready` and `In Review` are board-scheduling states with no STATUS.md
equivalent: use them between dependency-satisfaction and review-start, and
resolve them back to `In Progress` before the STATUS.md row is touched.
