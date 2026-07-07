import json
import os
import tempfile
import unittest

from ado_board_sync import config


class ConfigTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name

    def tearDown(self):
        self.tmp.cleanup()

    def _write(self, data, name="board.config.json"):
        path = os.path.join(self.dir, name)
        with open(path, "w") as f:
            json.dump(data, f)
        return path

    def test_load_resolves_relative_paths_against_config_dir(self):
        path = self._write({
            "org": "o", "project": "p", "code_prefix": "PROJ",
            "board_file": "docs/backlog.md",
        })
        cfg = config.load(path)
        self.assertEqual(cfg.board_file, os.path.join(self.dir, "docs/backlog.md"))
        self.assertTrue(cfg.csv_file.startswith(self.dir))

    def test_defaults_applied(self):
        cfg = config.load(self._write({"org": "o", "project": "p", "code_prefix": "PROJ"}))
        self.assertEqual(cfg.api_version, "7.1")
        self.assertEqual(cfg.types["story"], "Issue")
        self.assertEqual(cfg.stop_headings, [])
        self.assertEqual(cfg.max_retries, 3)
        self.assertEqual(cfg.backoff, 1.5)
        self.assertEqual(cfg.timeout, 20)

    def test_missing_required_keys_exit(self):
        for data in ({"org": "o"}, {"org": "o", "project": "p"}):
            with self.assertRaises(SystemExit):
                config.load(self._write(data))

    def test_resolve_pat_from_env(self):
        cfg = config.load(self._write({"org": "o", "project": "p", "code_prefix": "PROJ"}))
        os.environ["AZURE_DEVOPS_PAT"] = "  secret-token  "
        try:
            self.assertEqual(cfg.resolve_pat(), "secret-token")
        finally:
            del os.environ["AZURE_DEVOPS_PAT"]

    def test_resolve_pat_from_file_when_env_absent(self):
        cfg = config.load(self._write({"org": "o", "project": "p", "code_prefix": "PROJ"}))
        os.environ.pop("AZURE_DEVOPS_PAT", None)
        with open(os.path.join(self.dir, ".ado_pat"), "w") as f:
            f.write("file-token\n")
        self.assertEqual(cfg.resolve_pat(), "file-token")

    def test_resolve_pat_none_when_unset(self):
        cfg = config.load(self._write({"org": "o", "project": "p", "code_prefix": "PROJ"}))
        os.environ.pop("AZURE_DEVOPS_PAT", None)
        self.assertIsNone(cfg.resolve_pat())

    def test_missing_config_file_exits(self):
        with self.assertRaises(SystemExit):
            config.load(os.path.join(self.dir, "nope.json"))


if __name__ == "__main__":
    unittest.main()
