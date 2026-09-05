# Design System: ADO Board Sync Desktop

**Status:** Approved — the tokens in §2–§3 are the ones `Styles/Theme.axaml` defines; a token here without a definition there is a defect

**Date:** 2026-09-01 (rev 2)

## 1. Principles

1. **Same family, distinct identity.** Share ADO Insights' spacing scale, type scale, and corner-radius language so the two desktop tools feel like one product family. Use a distinct accent hue so a user can tell at a glance which app window they are in.
2. **Never rely on color alone.** Every Plan operation (Create/Update/Delete/Unchanged) and every validation state (Error/Warning) pairs a color with a glyph and a text label. This app shows diffs that gate real writes; a colorblind user must be able to read them correctly.
3. **The preview is the product.** The Markdown preview pane is not a "nice to have" side panel — it is how the user verifies a write before it happens. It gets first-class layout space: an equal half of the split editor, resizable.
4. **Plan and Apply are visually distinct actions.** Generating a Plan (safe, read-only) uses the quiet button style; Apply uses the accent style and always requires a second, explicit confirmation. The same rule now covers Save: writing the backlog file is a normal-emphasis action (it is local and atomic), clearly separated from the accent reserved for board writes.
5. **The buffer and the file are visually distinct states.** An unsaved edit carries a filled-dot glyph plus the word "unsaved" — on the item, and in the header — and while any buffer is dirty, the Save control is the only way forward: Plan and Apply are refused. The state, the rule, and the exit are all on screen at once.

## 2. Color tokens

Defined as `DynamicResource` keys in `Styles/Theme.axaml`, in both a Light and a Dark theme. Body text holds at least 4.5:1 contrast against its background in both themes.

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| AppAccentBrush | #2D6CDF | #6FA8FF | Primary actions, selection, links, focus outlines, the editor caret. |
| AppAccentSoftBrush | accent @ 12% | accent @ 18% | Selected nav/list item background, hover fills. |
| AppAccentEndBrush | #5B9CFF | #2D6CDF | Gradient end, hover accents. |
| ShellBackgroundBrush | #FFFFFF | #161B26 | Window/page background, form fields. |
| SidebarBackgroundBrush | #F4F7FD | #1E2531 | Nav rail, list backgrounds, footer. |
| CardBackgroundBrush | #F4F7FD | #1E2531 | Card fill, chips, quiet buttons. |
| CardBorderBrush | #E3EAF7 | #2B3446 | Card/field/pane borders. |
| EditorBackgroundBrush | — | — | The split editor's pane fill (both themes), so source and preview read as one surface. |
| TextPrimaryBrush | #1E2430 | #EEF1F6 | Primary text. |
| TextSecondaryBrush | #5B6472 | #B4BBC8 | Secondary text, labels. |
| TextMutedBrush | #8B93A1 | #7C8494 | Captions, placeholders. |
| PlanCreateBrush | #16C784 | #3CCB7E | Create rows/chips, paired with a `+` glyph. |
| PlanUpdateBrush | #F5A623 | #F2C12E | Update rows/chips (`~`); also the unsaved-edit dot, by intent: an unsaved buffer is an unapplied local update. |
| PlanDeleteBrush | #EA3943 | #FF7A85 | Delete rows/chips (`-`), solid fill, heavier border on destructive rows. |
| PlanUnchangedBrush | #8B93A1 | #7C8494 | Unchanged rows/chips (`=`). |
| StatusOkBrush / StatusErrorBrush | — | — | Badge fills for "markup clean" (ok) and problem states (error). |
| ValidationErrorBrush | #EA3943 | #FF7A85 | Inline malformed-markup markers; the tree's `!` badge; blocks Apply. |
| ValidationWarningBrush | #F5A623 | #F2C12E | Inline advisories; does not block Apply. |

## 3. Type and spacing

| Token | Value |
| --- | --- |
| FontSizeCaption / Body / Title / Display | 11 / 13 / 17 / 26 |
| SpacingXS / S / Tight / M / Chip / L / XL | 4 / 8 / 10 / 12 / 14 / 16 / 24 |
| RadiusSm / Md / Lg / Pill | 6 / 10 / 14 / 999 |
| ControlCornerRadius | 10 (RadiusMd) |
| EditorFontFamily | A monospace family for the source and HTML panes |

The backlog source editor uses the monospace family at FontSizeBody; the preview pane uses the app's default UI font at the same size, so the two panes stay visually comparable line-for-line where practical. Both wrap rather than clip: a backlog line is routinely wider than half the content column.

## 4. Components

Implemented components (styles live in `Styles/ControlStyles.axaml`; every resource key a view asks for is resolved by a launch test):

| Component | Purpose | Key states |
| --- | --- | --- |
| Nav rail | Switch sections (Backlog, Plan & Apply; Audit/Sprints/Assignees/History arrive with their slices). `ListBox#NavList`. | Selected (AppAccentSoftBrush fill + accent text), hover, disabled (no profile open). |
| Rail actions | Open profile…, Reload, Export CSV… — quiet, full-width buttons at the rail's foot. | Enabled/disabled by profile state (`CanReload`, `HasProfile`). |
| Backlog tree | Epic→Issue hierarchy with code badges and a `!` problem badge per item. `TreeView.backlog`. | Expanded by default (collapsed, the rail is unnavigable), hover/selected fills, problem badge glyph+tooltip. |
| Split editor | Source buffer (left, `TextBox.editor.editable`, monospace, wrapping) and preview/generated-HTML (right), `GridSplitter` between. | Editing (caret in accent), dirty (dot chip), preview/HTML radio toggle (Preview is the default). |
| Save control | Quiet-to-normal button in the source pane header; `Ctrl+S` via a window KeyBinding; enabled only while the profile has unsaved edits. | Enabled (dirty), disabled (clean), saving… (status bar). |
| Unsaved chips | Item-level dot chip ("unsaved") in the item header; profile-level chip ("Unsaved edits") in the content header with the rule in its tooltip. | Visible only while dirty; glyph + word, never colour alone. |
| Problems card | One row per markup problem under the editor, scoped as `check-html` scopes it ("description", "task …"). | Error border (ValidationErrorBrush); blocks Apply. |
| Onboarding screen | Two route cards: open an existing config, or describe the board; typed inline error per route; scaffold checkbox when the backlog file is missing. | Form valid/invalid (Open button gated), import error visible, scaffold option visible only when it applies. |
| Plan list | One row per affected item: code, title, operation chip, expandable field diff. | Create/Update/Delete/Unchanged (color + glyph + label per §2); destructive rows get a heavier delete-coloured border. |
| Apply confirmation dialog | Second, explicit confirmation before any write; restates the Plan's counts. | Default, in-progress (per-item outcome streaming), completed, failed. |
| Credential status | Names which source resolved (session/env/file) or lists the sources checked. | Resolved, missing (blocks board actions). |
| Footer | Status line (counts, markup summary, save/plan outcomes) plus the standing gate reminder: "Generating a Plan only reads. Nothing is written until you confirm an Apply." | Always visible. |

Planned components (specified now so their slices land consistently):

| Component | Purpose | Key states |
| --- | --- | --- |
| Inline validation gutter marker | Underline plus gutter glyph at the malformed line/block. | Error (blocks Apply), Warning (advisory only). |
| Audit finding card | One card per drift item: subject, evidence, kind (BacklogDrift/HierarchyDrift). | Read-only; "Open in Close-children" when applicable. |
| Sprint/assignee table | Editable table bound to `iterations`/`assignees` config. | Editing, conflict (code in two rows), saved. |
| Operation history timeline | Reverse-chronological ApplyRuns with per-item outcomes. | Succeeded, Partial, Failed. |

## 5. Interaction rules

1. A "Generate Plan" action always uses the quiet button style — it is read-only and safe to click repeatedly.
2. An "Apply" action always uses the accent style, and always opens the confirmation dialog — never a single-click write. Save uses neither: it is a local file write with its own control and gesture.
3. Plan chips use color, glyph, and label together (`+ Create`, `~ Update`, `- Delete`, `= Unchanged`), never color or glyph alone.
4. The editor never auto-saves to the backlog file; saving is an explicit action (button or Ctrl+S) and always re-validates before the next Plan generation.
5. Destructive Plan rows (Delete, and Close-children's closes) get a heavier visual weight — a PlanDeleteBrush border, not just text color — so they are not missed while scanning a long Plan.
6. A refused action says what to do next in the same breath as the refusal: the unsaved-edits refusal names Save; the external-change refusal names Reload and says the buffer survived; the stale-plan refusal names regeneration.
7. Radio groups that switch a pane's view bind `IsChecked` two-way with no Click handler — the group's own coordination raises Click while the view loads and flips the pane before the user touches it (a real defect this system shipped and pinned).

## 6. Accessibility

1. Keyboard: the editor and Plan-review flow are reachable without a mouse; `Ctrl+S` saves; standard Avalonia focus traversal covers the rest.
2. Focus-visible outlines on every interactive control, using AppAccentBrush.
3. Diff and validation states never rely on color alone (see §5.3).
4. Respect the OS reduced-motion setting; skip nonessential transitions (panel resize, chip hover) when it is set.
5. Screen-reader labels announce a Plan chip's operation and item code together, not the glyph alone.
6. Unsaved state is announced as text ("unsaved"), not only by the dot glyph; tooltips carry the rule (save before planning) so the chip teaches as well as flags.
