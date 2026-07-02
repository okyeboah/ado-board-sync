"""CSV intermediate: board model <-> Azure DevOps import CSV.

The columns match Azure DevOps' "Import work items" CSV layout, so the file can
be hand-inspected or imported through the web UI as well as via this tool::

    Work Item Type, Title 1, Title 2, Description
"""
import csv

from . import htmlfmt, parser

FIELDNAMES = ["Work Item Type", "Title 1", "Title 2", "Description"]


def rows_from_board(items, cfg):
    """Convert the parsed board model to CSV rows (Epics carry Title 1)."""
    rows = []
    for it in items:
        desc = htmlfmt.markdown_to_html(it["desc_lines"])
        if it["level"] == "epic":
            rows.append({
                "Work Item Type": cfg.types["epic"],
                "Title 1": it["title"],
                "Title 2": "",
                "Description": desc,
            })
        else:
            rows.append({
                "Work Item Type": cfg.types["story"],
                "Title 1": "",
                "Title 2": it["title"],
                "Description": desc,
            })
    return rows


def write_rows(rows, path):
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=FIELDNAMES)
        writer.writeheader()
        for row in rows:
            writer.writerow(row)


def read_rows(path):
    with open(path, encoding="utf-8") as f:
        return list(csv.DictReader(f))


def maps(cfg):
    """Read the CSV into lookup maps for inspection / the ADO web importer.

    Returns ``(epics, issues)`` where ``epics`` maps ``title_lower -> desc`` and
    ``issues`` maps ``CODE -> {"title": ..., "desc": ...}``.

    Note: ``resync`` and ``audit`` use :func:`backlog_maps` instead, so they read
    descriptions from the backlog directly and never depend on a freshly
    regenerated CSV.
    """
    epics, issues = {}, {}
    estype, sttype = cfg.types["epic"], cfg.types["story"]
    for row in read_rows(cfg.csv_file):
        wt = row["Work Item Type"]
        if wt == estype:
            epics[row["Title 1"].strip().lower()] = row["Description"]
        elif wt == sttype:
            m = cfg.issue_code_re.search(row["Title 2"].strip())
            if m:
                issues[m.group(1).upper()] = {
                    "title": row["Title 2"].strip(),
                    "desc": row["Description"],
                }
    return epics, issues


def backlog_maps(cfg):
    """Build the same ``(epics, issues)`` lookup maps as :func:`maps`, but from
    the backlog Markdown directly rather than the intermediate CSV.

    This is the source of truth for ``resync`` (title/description sync) and
    ``audit``: reading the backlog means an edit to it is reflected without a
    prior ``gen-csv``, and ``audit`` is a genuine board-vs-backlog check (a stale
    CSV can no longer produce a false PASS). Descriptions are rendered to HTML
    with the same converter ``gen-csv`` uses, so comparisons stay apples-to-apples.
    """
    epics, issues = {}, {}
    for it in parser.parse_board(cfg):
        desc = htmlfmt.markdown_to_html(it["desc_lines"])
        if it["level"] == "epic":
            epics[it["title"].strip().lower()] = desc
        else:
            issues[it["code"].upper()] = {
                "title": it["title"].strip(),
                "desc": desc,
            }
    return epics, issues
