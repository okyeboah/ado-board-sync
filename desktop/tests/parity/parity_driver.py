#!/usr/bin/env python3
"""Emit reference output from the CLI implementation, for the .NET parity tests.

The desktop app re-implements the CLI's parser, HTML conversion, and config
loader in C#. That port is only trustworthy if it is compared against the real
thing, so ``AdoBoardSync.Parity.Tests`` runs this script over shared fixtures
and fails on any difference.

Usage::

    parity_driver.py html|inline|plain|norm|unbalanced   < text
    parity_driver.py parse|tasks|config --config <path>
    parity_driver.py apply --config <path> --command <name> [flags]  < board.json

Text modes read UTF-8 from stdin and split it on "\\n" with no trailing-element
trimming, so the .NET side can build an identical list with String.Split('\\n').
Every mode prints one JSON object on stdout.

``apply`` is the plan-computation gate CONVENTIONS.md rule 5 asks for. It seeds
the CLI's own FakeClient with a board read from stdin, runs a mutating command
with ``--go``, and prints the board that command left behind. The .NET side seeds
its fake gateway identically, applies the Plan its own builder produced, and
compares the two boards. Comparing end states rather than printed output is what
makes the comparison meaningful: the two implementations word their dry-runs
differently and always will, but the board they leave behind must be identical or
one of them is wrong.
"""
import csv
import io
import json
import os
import sys
import types

REPO_ROOT = os.path.dirname(  # <repo>
    os.path.dirname(          # <repo>/desktop
        os.path.dirname(      # <repo>/desktop/tests
            os.path.dirname(os.path.abspath(__file__)))))
sys.path.insert(0, os.path.join(REPO_ROOT, "src"))

from ado_board_sync import config as cfgmod  # noqa: E402
from ado_board_sync import htmlfmt, parser  # noqa: E402


def _stdin_lines():
    return sys.stdin.buffer.read().decode("utf-8").split("\n")


def _stdin_text():
    return sys.stdin.buffer.read().decode("utf-8")


def _config(argv):
    if len(argv) < 2 or argv[0] != "--config":
        sys.exit("expected: --config <path>")
    return cfgmod.load(argv[1])


def _flags(argv):
    """The ``--key value`` and bare ``--flag`` pairs after the mode."""
    out = {}
    index = 0
    while index < len(argv):
        key = argv[index]
        if not key.startswith("--"):
            sys.exit(f"expected a --flag, got {key!r}")
        if index + 1 < len(argv) and not argv[index + 1].startswith("--"):
            out[key[2:]] = argv[index + 1]
            index += 2
        else:
            out[key[2:]] = True
            index += 1
    return out


def _seed(client, board):
    """Rebuild a board inside the FakeClient with the ids the caller chose.

    add_item allocates its own ids, which would make the two sides' boards
    incomparable, so this writes the item dictionaries directly — the same shape
    add_item builds, including the System.Parent field Azure DevOps keeps in step
    with the hierarchy relation.
    """
    reverse = "System.LinkTypes.Hierarchy-Reverse"
    forward = "System.LinkTypes.Hierarchy-Forward"

    for item in board:
        fields = {
            "System.Title": item.get("title", ""),
            "System.Description": item.get("description", ""),
            "System.WorkItemType": item["type"],
            "System.State": item.get("state", ""),
            "System.AssignedTo": item.get("assignedTo", ""),
            "System.IterationPath": item.get("iterationPath", ""),
        }
        client.items[item["id"]] = {"fields": fields, "relations": []}

    for item in board:
        parent = item.get("parentId")
        if parent is None:
            continue
        wid = item["id"]
        client.items[wid]["fields"]["System.Parent"] = parent
        client.items[wid]["relations"].append(
            {"rel": reverse, "url": f"{client.cfg.org_url}/wit/workItems/{parent}"})
        client.items[parent]["relations"].append(
            {"rel": forward, "url": f"{client.cfg.org_url}/wit/workItems/{wid}"})


def _dump(client):
    """The board the command left behind, in a shape the .NET side can build too."""
    return [
        {
            "id": wid,
            "type": item["fields"].get("System.WorkItemType", ""),
            "title": item["fields"].get("System.Title", ""),
            "description": item["fields"].get("System.Description", ""),
            "parentId": item["fields"].get("System.Parent"),
            "state": item["fields"].get("System.State", "") or "",
            "assignedTo": item["fields"].get("System.AssignedTo", "") or "",
            "iterationPath": item["fields"].get("System.IterationPath", "") or "",
        }
        for wid, item in sorted(client.items.items())
    ]


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    mode, rest = sys.argv[1], sys.argv[2:]

    if mode == "html":
        out = {"value": htmlfmt.markdown_to_html(_stdin_lines())}
    elif mode == "inline":
        out = {"value": htmlfmt.inline(_stdin_text())}
    elif mode == "plain":
        out = {"value": htmlfmt.plain(_stdin_text())}
    elif mode == "norm":
        out = {"value": htmlfmt.norm(_stdin_text())}
    elif mode == "unbalanced":
        out = {"problems": htmlfmt.unbalanced(_stdin_text())}
    elif mode == "parse":
        out = {"items": parser.parse_board(_config(rest))}
    elif mode == "tasks":
        out = {"tasks": parser.tasks_by_code(_config(rest))}
    elif mode == "csv":
        from ado_board_sync import csvio
        cfg = _config(rest)
        rows = csvio.rows_from_board(parser.parse_board(cfg), cfg)
        buf = io.StringIO()
        writer = csv.DictWriter(buf, fieldnames=csvio.FIELDNAMES)
        writer.writeheader()
        for row in rows:
            writer.writerow(row)
        out = {"value": buf.getvalue()}
    elif mode == "config":
        cfg = _config(rest)
        out = {
            "org": cfg.org,
            "project": cfg.project,
            "code_prefix": cfg.code_prefix,
            "api_version": cfg.api_version,
            "board_file": cfg.board_file,
            "csv_file": cfg.csv_file,
            "types": cfg.types,
            "states": cfg.states,
            "stop_headings": cfg.stop_headings,
            "pat_env": cfg.pat_env,
            "pat_file": cfg.pat_file,
            "task_title_max": cfg.task_title_max,
            "max_retries": cfg.max_retries,
            "backoff": cfg.backoff,
            "timeout": cfg.timeout,
            "team": cfg.team,
            "iterations": cfg.iterations,
            "assignees": cfg.assignees,
            "issue_code_pattern": cfg.issue_code_re.pattern,
            "base_url": cfg.base_url,
            "org_url": cfg.org_url,
        }
    elif mode == "apply":
        sys.path.insert(0, os.path.join(REPO_ROOT, "tests"))
        from fake_client import FakeClient

        from ado_board_sync import commands

        flags = _flags(rest)
        cfg = cfgmod.load(flags["config"])
        client = FakeClient(cfg)
        _seed(client, json.loads(sys.stdin.buffer.read().decode("utf-8"))["board"])

        # The commands read their switches off an argparse Namespace. Building one
        # by hand keeps this driver out of the CLI's argument parsing, which is not
        # what the .NET side re-implements.
        args = types.SimpleNamespace(
            go=True,
            assign_only=flags.get("assign-only", False),
            no_tasks=flags.get("no-tasks", False),
            reset_on_missing=False,
            only_unassigned=flags.get("only-unassigned", False),
            assign_from_parent=flags.get("assign-from-parent", False),
            code=flags.get("code"),
            sprint=flags.get("sprint"),
        )

        # Every command prints a report; the parity comparison is the board it
        # leaves behind, not the words, so the report goes nowhere.
        stdout, sys.stdout = sys.stdout, io.StringIO()
        try:
            exit_code = {
                "dedup": commands.dedup,
                "assign": commands.assign,
                "sprints": commands.sprints,
                "close-children": commands.close_children,
                "resync": commands.resync,
                "resync-tasks": commands.resync_tasks,
                "audit": commands.audit,
                "sync-one": commands.sync_one,
            }[flags["command"]](cfg, client, args)
        finally:
            sys.stdout = stdout

        out = {"exitCode": exit_code, "board": _dump(client)}
    else:
        sys.exit(f"unknown mode: {mode}")

    json.dump(out, sys.stdout, ensure_ascii=False, sort_keys=True)


if __name__ == "__main__":
    main()
