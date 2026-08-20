# Design System: ADO Board Sync Desktop

**Status:** Draft

## 1. Principles

1. **Same family, distinct identity.** Share ADO Insights' spacing scale, type scale, and corner-radius language so the two desktop tools feel like one product family. Use a distinct accent hue so a user can tell at a glance which app window they are in.
2. **Never rely on color alone.** Every Plan operation (Create/Update/Delete/Unchanged) and every validation state (Error/Warning) pairs a color with a glyph and a text label. This app shows diffs that gate real writes; a colorblind user must be able to read them correctly.
3. **The preview is the product.** The Markdown preview pane is not a "nice to have" side panel — it is how the user verifies a write before it happens. It gets first-class layout space.
4. **Plan and Apply are visually distinct actions.** Generating a Plan (safe, read-only) and Apply (a real write) never share a button style. Apply always requires a second, explicit confirmation.

## 2. Color tokens

Values below are starting points for an implementation-time contrast pass, not final-verified hex. Body text must hold at least 4.5:1 contrast against its background in both themes.

### Light theme

| Token | Value | Use |
| --- | --- | --- |
| AppAccentBrush | #2D6CDF | Primary actions, selection, links. |
| AppAccentSoftBrush | #2D6CDF at 12% opacity | Selected nav/list item background. |
| AppAccentEndBrush | #5B9CFF | Gradient end, hover accents. |
| ShellBackgroundBrush | #FFFFFF | Window/page background. |
| SidebarBackgroundBrush | #F4F7FD | Nav rail, list backgrounds. |
| CardBackgroundBrush | #F4F7FD | Card fill. |
| CardBorderBrush | #E3EAF7 | Card/field borders. |
| TextPrimaryBrush | #1E2430 | Primary text. |
| TextSecondaryBrush | #5B6472 | Secondary text, labels. |
| TextMutedBrush | #8B93A1 | Captions, placeholders. |
| PlanCreateBrush | #16C784 | Create rows/chips, paired with a `+` glyph. |
| PlanUpdateBrush | #F5A623 | Update rows/chips, paired with a `~` glyph. |
| PlanDeleteBrush | #EA3943 | Delete rows/chips, paired with a `-` glyph. |
| PlanUnchangedBrush | #8B93A1 | Unchanged rows/chips, paired with a `=` glyph. |
| ValidationErrorBrush | #EA3943 | Inline malformed-markup marker; blocks Apply. |
| ValidationWarningBrush | #F5A623 | Inline advisory (e.g., unmapped state); does not block Apply. |

### Dark theme

| Token | Value | Use |
| --- | --- | --- |
| AppAccentBrush | #6FA8FF | Primary actions, selection, links. |
| AppAccentSoftBrush | #6FA8FF at 18% opacity | Selected nav/list item background. |
| AppAccentEndBrush | #2D6CDF | Gradient end, hover accents. |
| ShellBackgroundBrush | #161B26 | Window/page background. |
| SidebarBackgroundBrush | #1E2531 | Nav rail, list backgrounds. |
| CardBackgroundBrush | #1E2531 | Card fill. |
| CardBorderBrush | #2B3446 | Card/field borders. |
| TextPrimaryBrush | #EEF1F6 | Primary text. |
| TextSecondaryBrush | #B4BBC8 | Secondary text, labels. |
| TextMutedBrush | #7C8494 | Captions, placeholders. |
| PlanCreateBrush | #3CCB7E | Create rows/chips. |
| PlanUpdateBrush | #F2C12E | Update rows/chips. |
| PlanDeleteBrush | #FF7A85 | Delete rows/chips. |
| PlanUnchangedBrush | #7C8494 | Unchanged rows/chips. |
| ValidationErrorBrush | #FF7A85 | Inline malformed-markup marker. |
| ValidationWarningBrush | #F2C12E | Inline advisory. |

## 3. Type and spacing

Reuse ADO Insights' scale for family consistency:

| Token | Value |
| --- | --- |
| FontSizeCaption | 11 |
| FontSizeBody | 13 |
| FontSizeTitle | 17 |
| FontSizeDisplay | 26 |
| SpacingXS / S / M / L / XL | 4 / 8 / 12 / 16 / 24 |
| CardCornerRadius | 14 |
| ControlCornerRadius | 10 |

The backlog source editor uses a monospace font at FontSizeBody; the preview pane uses the app's default UI font at the same size, so the two panes stay visually comparable line-for-line where practical.

## 4. Components

| Component | Purpose | Key states |
| --- | --- | --- |
| Nav rail | Switch between Board profiles and sections (Editor, Plan & Apply, Audit, Sprints, Assignees, History). | Selected (AppAccentSoftBrush fill), hover, disabled (no profile open). |
| Profile switcher | Pick the open Board profile. | Credential-resolved, credential-missing (warning badge). |
| Split editor | Source Markdown (left) and live preview (right), resizable, optional sync-scroll. | Editing, validating, error (inline marker), saved. |
| Inline validation marker | Underline plus gutter glyph at the malformed line/block. | Error (blocks Apply), Warning (advisory only). |
| Plan list | One row per affected item: code, title, operation chip, expandable field diff. | Create/Update/Delete/Unchanged (color + glyph + label per §2). |
| Apply confirmation dialog | Second, explicit confirmation before any write; restates the Plan's counts. | Default, in-progress (per-item outcome streaming), completed, failed. |
| Audit finding card | One card per drift item: subject, evidence, kind (BacklogDrift/HierarchyDrift). | Read-only; no action other than "Open in Close-children" when applicable. |
| Sprint/assignee table | Editable table bound to `iterations`/`assignees` config. | Editing, conflict (code in two rows), saved. |
| Operation history timeline | Reverse-chronological ApplyRuns with per-item outcomes. | Succeeded, Partial, Failed. |
| Credential status badge | Shows which credential source resolved (store/env/file) or that none did. | Resolved, missing (blocks board actions). |

## 5. Interaction rules

1. A "Generate Plan" action always uses a neutral/secondary button style — it is read-only and safe to click repeatedly.
2. An "Apply" action always uses a distinct, higher-emphasis style than "Generate Plan," and always opens the confirmation dialog — never a single-click write.
3. Plan chips use color, glyph, and label together (`+ Create`, `~ Update`, `- Delete`, `= Unchanged`), never color or glyph alone.
4. The editor never auto-saves to the backlog file on every keystroke; it saves on an explicit action or a debounced idle save the user can disable, and always re-validates before the next Plan generation.
5. Destructive Plan rows (Delete, and Close-children's closes) get a heavier visual weight — a PlanDeleteBrush border, not just text color — so they are not missed while scanning a long Plan.

## 6. Accessibility

1. Keyboard: the full editor and Plan-review flow are reachable without a mouse; standard shortcuts exist for Save, Generate Plan, and Apply-confirm.
2. Focus-visible outlines on every interactive control, using AppAccentBrush.
3. Diff and validation states never rely on color alone (see §5.3).
4. Respect the OS reduced-motion setting; skip nonessential transitions (panel resize, chip hover) when it is set.
5. Screen-reader labels announce a Plan chip's operation and item code together, not the glyph alone.
