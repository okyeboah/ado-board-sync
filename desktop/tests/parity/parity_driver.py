#!/usr/bin/env python3
"""Emit reference output from the CLI implementation, for the .NET parity tests.

The desktop app re-implements the CLI's parser, HTML conversion, and config
loader in C#. That port is only trustworthy if it is compared against the real
thing, so ``AdoBoardSync.Parity.Tests`` runs this script over shared fixtures
and fails on any difference.

Usage::

    parity_driver.py html|inline|plain|norm|unbalanced   < text
    parity_driver.py parse|tasks|config --config <path>

Text modes read UTF-8 from stdin and split it on "\\n" with no trailing-element
trimming, so the .NET side can build an identical list with String.Split('\\n').
Every mode prints one JSON object on stdout.
"""
import json
import os
import sys

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
    else:
        sys.exit(f"unknown mode: {mode}")

    json.dump(out, sys.stdout, ensure_ascii=False, sort_keys=True)


if __name__ == "__main__":
    main()
