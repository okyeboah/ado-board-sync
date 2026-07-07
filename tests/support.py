"""Shared test helpers (not collected as a test module)."""
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


class Args:
    """Stand-in for parsed argparse namespace."""

    def __init__(self, go=False, **kw):
        self.go = go
        for k, v in kw.items():
            setattr(self, k, v)
