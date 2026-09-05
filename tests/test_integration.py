"""Integration tests: multi-command flows driven through cli.main(), against an in-memory
FakeClient instead of the real command functions tested in isolation. test_commands.py already
covers each command's own logic in depth; this file targets the *composition* in cli.py --
`sync`'s gen-csv -> check-html -> import -> resync -> resync-tasks -> audit chain, its
abort-before-any-write behavior on malformed markup, and multi-run convergence -- none of which
is exercised anywhere else, since cli.main() always builds a real network Client on its own.
"""
import os
import tempfile
import unittest
from unittest.mock import patch

from ado_board_sync import cli
from tests.fake_client import FakeClient
from tests.support import write_backlog, write_board_config

BACKLOG = (
    "## Epic 1 — Platform Foundations\n"
    "Context for the epic.\n\n"
    "### PROJ-101 · Build the core event store\n"
    "Some description.\n"
    "- Implement the append-only EventStore\n"
    "- Add optimistic-concurrency checks\n\n"
    "### PROJ-102 · Wire up local orchestration\n"
    "- Wire the orchestrator\n"
)


class SyncOrchestrationTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = self.tmp.name
        self.backlog_path = write_backlog(self.dir, text=BACKLOG)
        self.config_path = write_board_config(self.dir)

        env_patcher = patch.dict(os.environ, {"AZURE_DEVOPS_PAT": "fake-pat-for-tests"})
        env_patcher.start()
        self.addCleanup(env_patcher.stop)

        self.fake_client = None

        def _build_fake(cfg, _pat):
            self.fake_client = FakeClient(cfg)
            return self.fake_client

        client_patcher = patch("ado_board_sync.cli.client_mod.Client", side_effect=_build_fake)
        client_patcher.start()
        self.addCleanup(client_patcher.stop)

    def _run(self, *cmd_args):
        return cli.main(["-c", self.config_path, *cmd_args])

    def _titles(self, wtype=None):
        return sorted(
            it["fields"]["System.Title"] for it in self.fake_client.items.values()
            if wtype is None or it["fields"]["System.WorkItemType"] == wtype
        )

    # --- happy path ----------------------------------------------------------
    def test_sync_creates_and_converges_an_empty_board(self):
        exit_code = self._run("sync", "--go")

        self.assertEqual(exit_code, 0)
        self.assertIn("Epic 1 — Platform Foundations", self._titles("Epic"))
        issue_titles = self._titles("Issue")
        self.assertTrue(any(t.startswith("PROJ-101") for t in issue_titles))
        self.assertTrue(any(t.startswith("PROJ-102") for t in issue_titles))
        self.assertEqual(len(self._titles("Task")), 3)

    def test_sync_is_idempotent_on_a_second_run(self):
        self._run("sync", "--go")
        first_count = len(self.fake_client.items)

        exit_code = self._run("sync", "--go")

        self.assertEqual(exit_code, 0)
        self.assertEqual(len(self.fake_client.items), first_count)

    def test_sync_picks_up_a_backlog_edit_on_the_next_run(self):
        self._run("sync", "--go")

        with open(self.backlog_path, "a") as f:
            f.write("- A newly added task bullet\n")

        exit_code = self._run("sync", "--go")

        self.assertEqual(exit_code, 0)
        self.assertIn("A newly added task bullet", self._titles("Task"))

    # --- abort-before-write ----------------------------------------------------
    def test_sync_aborts_before_any_write_when_check_html_fails(self):
        # check_html's own detection logic is covered directly in test_htmlfmt.py /
        # test_commands.py; what's untested is that cli.py's `sync` composition actually wires
        # a failing check_html to an abort *before* import ever runs, instead of writing partial
        # state to the board.
        with patch("ado_board_sync.commands.check_html", return_value=1):
            exit_code = self._run("sync", "--go")

        self.assertEqual(exit_code, 1)
        self.assertEqual(len(self.fake_client.items), 0)

    # --- dry run ---------------------------------------------------------------
    def test_sync_dry_run_writes_nothing(self):
        exit_code = self._run("sync")

        # Dry-run sync still runs audit at the end, which fails because nothing was created --
        # the meaningful assertion is that no items exist, not the exit code.
        self.assertEqual(exit_code, 1)
        self.assertEqual(len(self.fake_client.items), 0)


if __name__ == "__main__":
    unittest.main()
