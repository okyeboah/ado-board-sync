"""Tests for the advance command: git evidence to state transition.

The subprocess boundary is stubbed at the module's probe functions (_repo_ready,
_branches, _ahead_count) so these tests exercise only command logic -- which
branch names count as evidence, how evidence combines with board states, and
what may be written.
"""
import os
import tempfile
import threading
import unittest
from unittest.mock import patch

from ado_board_sync import gitstate

from .fake_client import FakeClient
from .support import Args, build_cfg, write_backlog

REPO = "/local/repo"

BRANCHES = [
    "main",
    "origin/main",
    "feature/PROJ-101-split-atoms",
    "feature/PROJ-102-split-atoms",
    "bugfix/PROJ-103-fix",
]
AHEAD = {  # branch -> commits beyond base; a count of 0 proves nothing started
    "feature/PROJ-101-split-atoms": 3,
    "feature/PROJ-102-split-atoms": 5,
    "bugfix/PROJ-103-fix": 0,
}

BACKLOG = (
    "## Epic 1 — Foundations\n\n"
    "### PROJ-101 · Split atoms\n"
    "### PROJ-102 · Fuse cells\n"
    "### PROJ-103 · Ship it\n"
)


def _probe_ready(_repo, _fetch):
    return True


class AdvanceTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        board_path = write_backlog(self.tmp.name, text=BACKLOG)
        self.cfg = build_cfg(csv_path=os.path.join(self.tmp.name, "out.csv"), states={
            "done": "Done", "todo": "To Do", "doing": "Doing",
        }, board_file=board_path)
        self.client = FakeClient(self.cfg)
        epic = self.client.add_item(self.cfg.types["epic"], "Epic 1 — Foundations")
        # On the board: two evidenced Issues in the start state, one whose
        # branch has no commits beyond base, one already past the start state.
        self.todo_issue = self.client.add_item(
            self.cfg.types["story"], "PROJ-101 · Split atoms", parent=epic, state="To Do")
        self.other_todo_issue = self.client.add_item(
            self.cfg.types["story"], "PROJ-102 · Fuse cells", parent=epic, state="To Do")
        self.no_evidence = self.client.add_item(
            self.cfg.types["story"], "PROJ-103 · Ship it", parent=epic, state="To Do")
        self.doing_issue = self.client.add_item(
            self.cfg.types["story"], "PROJ-104 · Already going", parent=epic, state="Doing")

    def tearDown(self):
        self.tmp.cleanup()

    def _args(self, go=False):
        return Args(go=go, repo=[REPO], base="origin/main", no_fetch=True)

    def _run(self, go=False):
        with patch.object(gitstate, "_repo_ready", _probe_ready), \
             patch.object(gitstate, "_branches", return_value=BRANCHES), \
             patch.object(gitstate, "_ahead_count",
                          side_effect=lambda r, b, br: AHEAD.get(br, 0)):
            return gitstate.advance(self.cfg, self.client, self._args(go))

    def test_dry_run_reports_the_plan_and_writes_nothing(self):
        self.assertEqual(0, self._run(go=False))
        self.assertEqual(
            "To Do", self.client.items[self.todo_issue]["fields"]["System.State"])
        self.assertEqual(
            "To Do", self.client.items[self.other_todo_issue]["fields"]["System.State"])

    def test_go_advances_only_todo_issues_with_commit_evidence(self):
        self.assertEqual(0, self._run(go=True))
        self.assertEqual("Doing", self.client.items[self.todo_issue]["fields"]["System.State"])
        self.assertEqual(
            "Doing", self.client.items[self.other_todo_issue]["fields"]["System.State"])
        # No commits or already past start -> untouched either way.
        self.assertEqual("To Do", self.client.items[self.no_evidence]["fields"]["System.State"])
        self.assertEqual("Doing", self.client.items[self.doing_issue]["fields"]["System.State"])

    def test_missing_state_configuration_is_reported_not_guessed(self):
        cfg = build_cfg(csv_path="unused.csv")  # states carries 'done' only
        client = FakeClient(cfg)
        self.assertEqual(
            1,
            gitstate.advance(
                cfg, client,
                Args(go=True, repo=[REPO], base="origin/main", no_fetch=True),
            ),
        )
        self.assertEqual({}, client.items)

    def test_apply_runs_jobs_on_distinct_worker_threads(self):
        # Three jobs that only finish once all three have started: this passes
        # only if _apply genuinely executes them concurrently, and order of
        # results must survive regardless.
        barrier = threading.Barrier(3)
        threads = []

        def job(i):
            threads.append(threading.current_thread())
            barrier.wait(timeout=5)
            return i

        self.assertEqual(
            [0, 1, 2],
            gitstate._apply([lambda i=i: job(i) for i in range(3)]),
        )
        self.assertGreaterEqual(len(set(t.ident for t in threads)), 3)


if __name__ == "__main__":
    unittest.main()
