## Theme A — Custom epic heading

This backlog uses a non-default epic_heading_regex, so the default
`^##\s+(Epic\b.*)$` matches nothing here.

### PROJ-701 · An issue under a custom epic
- a task under a custom epic

## Epic 1 — Not an epic under the custom regex

The line above starts with "## Epic", which the default regex matches and the
custom "Theme" regex does not. It is description text here.

### PROJ-702 · Second issue
- another task

Note: see ## Theme B below for the rest. This line is the trap. With an
unanchored epic regex, Python's re.match still refuses it because it anchors at
position 0, while .NET's Match would find "## Theme B..." at index 10 and call
the line an Epic. It must stay description text on both sides.

## Theme B — Second custom epic

### PROJ-703 · Third issue
