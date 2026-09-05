"""End-to-end tests: invoke the real `ado-board-sync` CLI as a subprocess, the way a user
actually runs it -- exercising process startup, argument parsing, config/PAT loading, and file
I/O as a black box. This is distinct from the integration tier (test_integration.py), which
drives cli.main() in-process against a FakeClient.

Scoped to what's honestly testable without a live Azure DevOps org: the network-free commands
(gen-csv, check-html) run for real, plus the config/PAT error paths every network command shares
before it ever opens a connection. There is no local stand-in for the Azure DevOps REST API here
-- the client always targets https://dev.azure.com, with no override point -- so commands that
write to a board are covered by the FakeClient-based integration/unit tiers instead.
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest

from tests.support import write_backlog, write_board_config

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO_ROOT, "src")


class CliE2ETest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = self.tmp.name

    def _run(self, *args, env_extra=None):
        env = dict(os.environ)
        env["PYTHONPATH"] = SRC
        if env_extra:
            env.update(env_extra)
        return subprocess.run(
            [sys.executable, "-m", "ado_board_sync", *args],
            cwd=self.dir, env=env, capture_output=True, text=True, timeout=30,
        )

    # --- offline commands: no PAT, no network -----------------------------------
    def test_gen_csv_writes_the_import_csv(self):
        write_board_config(self.dir)
        write_backlog(self.dir)

        result = self._run("gen-csv")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Wrote", result.stdout)
        csv_path = os.path.join(self.dir, "work-items.csv")
        self.assertTrue(os.path.exists(csv_path))
        with open(csv_path) as f:
            content = f.read()
        self.assertIn("PROJ-101", content)

    def test_check_html_passes_on_well_formed_backlog(self):
        write_board_config(self.dir)
        write_backlog(self.dir)

        result = self._run("check-html")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("malformed: 0", result.stdout)

    # --- config / credential error paths, shared by every network command ------
    def test_missing_config_file_exits_nonzero_with_a_clear_message(self):
        result = self._run("audit")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Config not found", result.stdout + result.stderr)

    def test_config_missing_a_required_key_exits_nonzero(self):
        # No project/code_prefix -- Config validates and exits before board_file is ever read,
        # so no backlog needs to exist on disk for this case.
        with open(os.path.join(self.dir, "board.config.json"), "w") as f:
            json.dump({"org": "demo-org"}, f)

        result = self._run("gen-csv")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("board.config.json must set", result.stdout + result.stderr)

    def test_network_command_without_a_pat_exits_nonzero_before_touching_the_network(self):
        # audit resolves the PAT and exits before it ever reads the backlog, so none is needed.
        write_board_config(self.dir)
        # Force-clear AZURE_DEVOPS_PAT regardless of what the host shell has set, and rely on
        # the tempdir having no .ado_pat file either -- resolve_pat() must fail closed.
        result = self._run("audit", env_extra={"AZURE_DEVOPS_PAT": ""})

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("No PAT found", result.stdout + result.stderr)

    # --- process / argument-parsing sanity ---------------------------------------
    def test_unknown_command_exits_nonzero(self):
        result = self._run("not-a-real-command")

        self.assertNotEqual(result.returncode, 0)

    def test_help_exits_zero(self):
        result = self._run("--help")

        self.assertEqual(result.returncode, 0)
        self.assertIn("ado-board-sync", result.stdout)


if __name__ == "__main__":
    unittest.main()
