"""Shared test helpers (not collected as a test module)."""
import json
import os

from ado_board_sync.config import Config

FIXTURES = os.path.join(os.path.dirname(__file__), "fixtures")


def build_cfg(csv_path, **overrides):
    """Build a Config pointed at the fixture backlog and a temp CSV path."""
    data = {
        "org": "demo-org",
        "project": "DemoProject",
        "code_prefix": "PROJ",
        "board_file": os.path.join(FIXTURES, "backlog.md"),
        "csv_file": csv_path,
        "stop_headings": ["## Appendix"],
    }
    data.update(overrides)
    return Config(data, FIXTURES)


MINIMAL_BACKLOG = (
    "## Epic 1 — Platform Foundations\n\n"
    "### PROJ-101 · Build the core event store\n"
    "- Implement the append-only EventStore\n"
)


def write_board_config(directory):
    """Write a real board.config.json into ``directory``, for CLI-level tests (subprocess or
    cli.main()) that load config from disk rather than building a Config in memory."""
    config = {
        "org": "demo-org",
        "project": "DemoProject",
        "code_prefix": "PROJ",
        "board_file": "backlog.md",
        "csv_file": "work-items.csv",
    }
    path = os.path.join(directory, "board.config.json")
    with open(path, "w") as f:
        json.dump(config, f)
    return path


def write_backlog(directory, text=MINIMAL_BACKLOG):
    """Write a backlog.md into ``directory``, matching board_file above."""
    path = os.path.join(directory, "backlog.md")
    with open(path, "w") as f:
        f.write(text)
    return path


class Args:
    """Stand-in for parsed argparse namespace."""

    def __init__(self, go=False, **kw):
        self.go = go
        for k, v in kw.items():
            setattr(self, k, v)
