import os
import tempfile
import unittest

from ado_board_sync import parser
from tests.support import build_cfg


class ParserTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.cfg = build_cfg(os.path.join(self.tmp.name, "out.csv"))
        self.items = parser.parse_board(self.cfg)

    def tearDown(self):
        self.tmp.cleanup()

    def _by_level(self, level):
        return [it for it in self.items if it["level"] == level]

    def test_epics_parsed_in_order(self):
        epics = self._by_level("epic")
        self.assertEqual(
            [e["title"] for e in epics],
            ["Epic 1 — Platform Foundations", "Epic 2 — Delivery"],
        )

    def test_issues_and_codes(self):
        codes = [it["code"] for it in self._by_level("issue")]
        self.assertEqual(codes, ["PROJ-101", "PROJ-102", "PROJ-201"])

    def test_stop_heading_excludes_later_issues(self):
        codes = [it["code"] for it in self._by_level("issue")]
        self.assertNotIn("PROJ-999", codes)

    def test_intro_before_first_epic_ignored(self):
        # No item should carry the intro paragraph in its description.
        for it in self.items:
            joined = "\n".join(it["desc_lines"])
            self.assertNotIn("must be ignored", joined)

    def test_top_level_bullets_become_tasks(self):
        tasks = parser.tasks_by_code(self.cfg)
        self.assertEqual(tasks["PROJ-101"], [
            "Implement the append-only `EventStore`",
            "Add **optimistic-concurrency** checks",
        ])
        self.assertEqual(tasks["PROJ-201"], ["Expose `/health`"])
        self.assertEqual(tasks["PROJ-102"], [])

    def test_nested_bullets_are_not_tasks(self):
        tasks = parser.tasks_by_code(self.cfg)
        self.assertTrue(all("nested note" not in t for t in tasks["PROJ-101"]))

    def test_issue_description_keeps_context_and_nested_note(self):
        issue = next(it for it in self.items if it.get("code") == "PROJ-101")
        joined = "\n".join(issue["desc_lines"])
        self.assertIn("Reference: ADR-002", joined)
        self.assertIn("nested note", joined)


if __name__ == "__main__":
    unittest.main()
