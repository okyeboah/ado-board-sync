import os
import tempfile
import unittest

from ado_board_sync import commands, csvio, parser
from tests.support import build_cfg


class CsvIoTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.csv_path = os.path.join(self.tmp.name, "out.csv")
        self.cfg = build_cfg(self.csv_path)

    def tearDown(self):
        self.tmp.cleanup()

    def test_rows_from_board_assigns_columns_by_level(self):
        rows = csvio.rows_from_board(parser.parse_board(self.cfg), self.cfg)
        epic_rows = [r for r in rows if r["Work Item Type"] == "Epic"]
        issue_rows = [r for r in rows if r["Work Item Type"] == "Issue"]
        self.assertEqual(len(epic_rows), 2)
        self.assertEqual(len(issue_rows), 3)
        self.assertTrue(all(r["Title 1"] and not r["Title 2"] for r in epic_rows))
        self.assertTrue(all(r["Title 2"] and not r["Title 1"] for r in issue_rows))

    def test_write_then_read_roundtrip(self):
        commands.gen_csv(self.cfg)
        rows = csvio.read_rows(self.csv_path)
        self.assertEqual(rows[0]["Work Item Type"], "Epic")
        self.assertEqual(rows[0]["Title 1"], "Epic 1 — Platform Foundations")

    def test_maps_builds_epic_and_issue_lookups(self):
        commands.gen_csv(self.cfg)
        epics, issues = csvio.maps(self.cfg)
        self.assertIn("epic 1 — platform foundations", epics)
        self.assertIn("PROJ-101", issues)
        self.assertEqual(
            issues["PROJ-101"]["title"], "PROJ-101 · Build the core event store"
        )

    def test_backlog_maps_reads_backlog_without_a_csv(self):
        # No gen_csv() call: backlog_maps must not touch the CSV file at all.
        self.assertFalse(os.path.exists(self.csv_path))
        epics, issues = csvio.backlog_maps(self.cfg)
        self.assertFalse(os.path.exists(self.csv_path))
        self.assertIn("epic 1 — platform foundations", epics)
        self.assertIn("PROJ-101", issues)
        self.assertEqual(
            issues["PROJ-101"]["title"], "PROJ-101 · Build the core event store"
        )
        # Descriptions are rendered to HTML, matching what gen-csv would write.
        self.assertIn("<", issues["PROJ-101"]["desc"])

    def test_custom_types_drive_csv_type_column(self):
        cfg = build_cfg(self.csv_path, types={"epic": "Epic", "story": "User Story", "task": "Task"})
        rows = csvio.rows_from_board(parser.parse_board(cfg), cfg)
        self.assertTrue(any(r["Work Item Type"] == "User Story" for r in rows))


if __name__ == "__main__":
    unittest.main()
