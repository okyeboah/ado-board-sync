"""Parse a Markdown backlog into an ordered Epic / Issue / Task model.

Backlog shape (configurable via the config regexes / prefix)::

    ## Epic 1 — Title            -> Epic
    *italic context lines*       -> part of the Epic description
    ### PROJ-101 · Title         -> Issue (code = PROJ-101, prefix "PROJ")
    - top-level bullet           -> a child Task of that Issue
      * nested bullet            -> part of the Issue description only

Everything between a heading and the next heading is that item's description.
Parsing starts at the first Epic heading and stops at any ``stop_headings``
entry (appendices / registers).
"""


def parse_board(cfg):
    """Return an ordered list of item dicts in document order.

    Each item is either::

        {"level": "epic",  "title": str, "desc_lines": [str, ...]}
        {"level": "issue", "code": str, "title": str,
         "desc_lines": [str, ...], "bullets": [str, ...]}
    """
    epic_re = cfg.epic_heading_regex
    issue_code_re = cfg.issue_code_re
    stops = cfg.stop_headings

    items = []
    target = None      # the item dict currently accumulating description lines
    desc = []
    started = False

    def finalize():
        nonlocal desc
        if target is not None:
            target["desc_lines"] = desc
            if target["level"] == "issue":
                target["bullets"] = [
                    ln.strip()[2:].strip()
                    for ln in desc
                    if ln.strip().startswith("- ")
                ]
        desc = []

    with open(cfg.board_file, encoding="utf-8") as f:
        for raw in f:
            s = raw.strip()

            if any(s.startswith(h) for h in stops):
                break

            em = epic_re.match(s)
            if em:
                finalize()
                target = {"level": "epic", "title": em.group(1).strip(), "desc_lines": []}
                items.append(target)
                started = True
                continue

            if not started:
                continue

            if s.startswith("### "):
                cm = issue_code_re.search(s)
                if cm:
                    finalize()
                    target = {
                        "level": "issue",
                        "code": cm.group(1).upper(),
                        "title": s.lstrip("#").strip(),
                        "desc_lines": [],
                        "bullets": [],
                    }
                    items.append(target)
                    continue

            # Description line for the current item (skip leading blanks).
            if target is not None:
                if not desc and not s:
                    continue
                desc.append(raw.rstrip("\n"))

    finalize()
    return items


def tasks_by_code(cfg):
    """Map each Issue code to its list of top-level bullet strings (Tasks)."""
    return {
        it["code"]: it["bullets"]
        for it in parse_board(cfg)
        if it["level"] == "issue"
    }
