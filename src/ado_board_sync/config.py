"""Per-project configuration loading and credential resolution.

Settings live in a ``board.config.json`` checked into each project. The PAT is
never stored there: it is read from an environment variable or a gitignored
token file at run time, and is never logged or echoed.
"""
import json
import os
import re
import sys

REQUIRED = ("org", "project", "code_prefix")

DEFAULTS = {
    "api_version": "7.1",
    "board_file": "docs/backlog.md",
    "csv_file": "build/work-items.csv",
    "types": {"epic": "Epic", "story": "Issue", "task": "Task"},
    # Workflow state names. `done` is the terminal state used by close-children
    # to cascade an Issue's completion onto its child Tasks. Basic uses "Done";
    # Agile/CMMI use "Closed"/"Resolved".
    "states": {"done": "Done"},
    # Matches an Epic heading on the *stripped* line; group(1) is the title.
    "epic_heading_regex": r"^##\s+(Epic\b.*)$",
    # Headings that mark the end of the backlog body (appendices, registers).
    "stop_headings": [],
    "pat_env": "AZURE_DEVOPS_PAT",
    "pat_file": ".ado_pat",
    "task_title_max": 250,
    "team": None,
    "iterations": [],
    "max_retries": 3,
    "backoff": 1.5,
    "timeout": 20,
}


class Config:
    def __init__(self, data, base_dir):
        self.base_dir = base_dir
        missing = [k for k in REQUIRED if not data.get(k)]
        if missing:
            sys.exit(f"board.config.json must set: {', '.join(missing)}.")
        self.org = data["org"]
        self.project = data["project"]
        self.api_version = data.get("api_version", DEFAULTS["api_version"])
        self.code_prefix = data["code_prefix"]
        self.board_file = self._abs(data.get("board_file", DEFAULTS["board_file"]))
        self.csv_file = self._abs(data.get("csv_file", DEFAULTS["csv_file"]))

        types = dict(DEFAULTS["types"])
        types.update(data.get("types", {}))
        self.types = types

        states = dict(DEFAULTS["states"])
        states.update(data.get("states", {}))
        self.states = states

        self.epic_heading_regex = re.compile(
            data.get("epic_heading_regex", DEFAULTS["epic_heading_regex"])
        )
        self.stop_headings = data.get("stop_headings", DEFAULTS["stop_headings"])
        self.pat_env = data.get("pat_env", DEFAULTS["pat_env"])
        self.pat_file = data.get("pat_file", DEFAULTS["pat_file"])
        self.task_title_max = data.get("task_title_max", DEFAULTS["task_title_max"])
        self.max_retries = int(data.get("max_retries", DEFAULTS["max_retries"]))
        self.backoff = float(data.get("backoff", DEFAULTS["backoff"]))
        self.timeout = int(data.get("timeout", DEFAULTS["timeout"]))

        # Sprints/iterations: an ordered list of {name, start?, finish?, items:[codes]}.
        # `team` overrides the auto-detected team the sprints are added to.
        self.team = data.get("team", DEFAULTS["team"])
        self.iterations = data.get("iterations", DEFAULTS["iterations"])

        self.issue_code_re = re.compile(r"(%s-\d+)" % re.escape(self.code_prefix))
        self.base_url = f"https://dev.azure.com/{self.org}/{self.project}/_apis"
        self.org_url = f"https://dev.azure.com/{self.org}/_apis"

    def _abs(self, p):
        if os.path.isabs(p):
            return p
        return os.path.normpath(os.path.join(self.base_dir, p))

    def resolve_pat(self):
        """Return the PAT from env or the token file, or None. Never logged."""
        env = os.environ.get(self.pat_env)
        if env and env.strip():
            return env.strip()
        token_path = self._abs(self.pat_file)
        if os.path.exists(token_path):
            with open(token_path) as f:
                token = f.read().strip()
            if token:
                return token
        return None


def load(path=None):
    path = path or os.path.join(os.getcwd(), "board.config.json")
    if not os.path.exists(path):
        sys.exit(
            f"Config not found: {path}\n"
            "Create one from board.config.example.json (-c to point elsewhere)."
        )
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    return Config(data, os.path.dirname(os.path.abspath(path)))
