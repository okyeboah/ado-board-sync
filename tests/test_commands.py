import os
import tempfile
import unittest

from ado_board_sync import commands
from tests.fake_client import FORWARD, FakeClient
from tests.support import Args, build_cfg


class CommandsTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.cfg = build_cfg(os.path.join(self.tmp.name, "out.csv"))
        commands.gen_csv(self.cfg)            # build the CSV the commands consume
        self.client = FakeClient(self.cfg)

    def tearDown(self):
        self.tmp.cleanup()

    # --- helpers -----------------------------------------------------------
    def _titles_of_type(self, wtype):
        return sorted(
            it["fields"]["System.Title"]
            for it in self.client.items.values()
            if it["fields"]["System.WorkItemType"] == wtype
        )

    def _children(self, parent_id):
        _, item = self.client.get_item(parent_id)
        return [int(r["url"].split("/")[-1]) for r in item["relations"] if r["rel"] == FORWARD]

    # --- check-html --------------------------------------------------------
    def test_check_html_passes_on_the_fixture_backlog(self):
        self.assertEqual(commands.check_html(self.cfg), 0)

    # --- import ------------------------------------------------------------
    def test_import_dry_run_creates_nothing(self):
        commands.import_items(self.cfg, self.client, Args(go=False))
        self.assertEqual(len(self.client.items), 0)

    def test_import_creates_epics_issues_and_hierarchy(self):
        commands.import_items(self.cfg, self.client, Args(go=True))
        self.assertEqual(len(self._titles_of_type("Epic")), 2)
        self.assertEqual(len(self._titles_of_type("Issue")), 3)
        # Every Issue must be linked under an Epic.
        epic_ids = [
            wid for wid, it in self.client.items.items()
            if it["fields"]["System.WorkItemType"] == "Epic"
        ]
        linked = [c for eid in epic_ids for c in self._children(eid)]
        self.assertEqual(len(linked), 3)

    def test_import_is_idempotent(self):
        commands.import_items(self.cfg, self.client, Args(go=True))
        before = len(self.client.items)
        commands.import_items(self.cfg, self.client, Args(go=True))
        self.assertEqual(len(self.client.items), before)

    def test_import_dedups_duplicate_rows_in_one_run(self):
        # A duplicate Issue row in the CSV (or a re-run whose WIQL read lags
        # behind a fresh create) must not produce two work items for one code.
        rows = [
            {"Work Item Type": "Epic", "Title 1": "Epic 1 — Platform Foundations",
             "Title 2": "", "Description": "d"},
            {"Work Item Type": "Issue", "Title 1": "",
             "Title 2": "PROJ-101 · Build the core event store", "Description": "d"},
            {"Work Item Type": "Issue", "Title 1": "",
             "Title 2": "PROJ-101 · Build the core event store", "Description": "d"},
        ]
        from ado_board_sync import csvio
        csvio.write_rows(rows, self.cfg.csv_file)
        commands.import_items(self.cfg, self.client, Args(go=True))
        self.assertEqual(len(self._titles_of_type("Issue")), 1)

    def test_import_does_not_flag_child_task_as_duplicate(self):
        # A child Task's title routinely carries its parent Issue's code. That
        # is not a duplicate: only work items of the Issue type should be keyed
        # by code. Regression for a false "Run `dedup --go`" warning that fired
        # on a healthy board whenever an Issue had a code-bearing child Task.
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        issue = self.client.add_item(
            "Issue", "PROJ-101 · Build the core event store", parent=epic
        )
        self.client.add_item(
            "Task", "PROJ-101 · Define the append-only stream schema", parent=issue
        )
        import contextlib
        import io

        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            commands.import_items(self.cfg, self.client, Args(go=False))
        self.assertNotIn("WARNING", out.getvalue())
        self.assertNotIn("dedup --go", out.getvalue())

    def test_import_still_warns_on_genuine_duplicate_issue(self):
        # The warning must still fire when two distinct Issues carry one code.
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        self.client.add_item("Issue", "PROJ-101 · Build the core event store", parent=epic)
        self.client.add_item("Issue", "PROJ-101 · Build the core event store (dup)", parent=epic)
        import contextlib
        import io

        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            commands.import_items(self.cfg, self.client, Args(go=False))
        self.assertIn("WARNING", out.getvalue())
        self.assertIn("PROJ-101", out.getvalue())

    # --- resync ------------------------------------------------------------
    def test_resync_fixes_stale_title_and_description(self):
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations", desc="stale")
        self.client.add_item("Issue", "PROJ-101 · OLD TITLE", desc="old", parent=epic)
        commands.resync(self.cfg, self.client, Args(go=True))
        issue = next(
            it for it in self.client.items.values()
            if it["fields"]["System.WorkItemType"] == "Issue"
        )
        self.assertEqual(issue["fields"]["System.Title"], "PROJ-101 · Build the core event store")
        self.assertIn("event", issue["fields"]["System.Description"].lower())

    def test_resync_updates_description_without_a_pregenerated_csv(self):
        # The real-world trap: the backlog changed but nobody ran gen-csv.
        # resync reads the backlog directly, so it still fixes the board and
        # never touches the (absent) CSV.
        missing_csv = os.path.join(self.tmp.name, "never-written.csv")
        cfg = build_cfg(missing_csv)
        client = FakeClient(cfg)
        epic = client.add_item("Epic", "Epic 1 — Platform Foundations", desc="stale")
        client.add_item(
            "Issue", "PROJ-101 · Build the core event store", desc="stale", parent=epic
        )
        commands.resync(cfg, client, Args(go=True))
        self.assertFalse(os.path.exists(missing_csv))  # CSV never consulted
        issue = next(
            it for it in client.items.values()
            if it["fields"]["System.WorkItemType"] == "Issue"
        )
        self.assertIn("event", issue["fields"]["System.Description"].lower())

    # --- resync-tasks ------------------------------------------------------
    def test_resync_tasks_adds_missing_and_deletes_stale(self):
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        issue = self.client.add_item(
            "Issue", "PROJ-101 · Build the core event store", parent=epic
        )
        # One correct task (keep) + one stale task (delete); one task is missing.
        self.client.add_item("Task", "Implement the append-only EventStore", parent=issue)
        self.client.add_item("Task", "Stale task to remove", parent=issue)

        commands.resync_tasks(self.cfg, self.client, Args(go=True))

        child_titles = {
            self.client.items[c]["fields"]["System.Title"] for c in self._children(issue)
        }
        self.assertEqual(child_titles, {
            "Implement the append-only EventStore",
            "Add optimistic-concurrency checks",
        })

    # --- close-children ----------------------------------------------------
    def _seed_issue_with_tasks(self, issue_state, task_states):
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        issue = self.client.add_item(
            "Issue", "PROJ-101 · Build the core event store",
            parent=epic, state=issue_state,
        )
        tasks = [
            self.client.add_item("Task", f"Task {i}", parent=issue, state=s)
            for i, s in enumerate(task_states)
        ]
        return issue, tasks

    def test_close_children_closes_open_tasks_under_done_issue(self):
        _, tasks = self._seed_issue_with_tasks("Done", ["Doing", "To Do"])
        commands.close_children(self.cfg, self.client, Args(go=True))
        for tid in tasks:
            self.assertEqual(self.client.items[tid]["fields"]["System.State"], "Done")

    def test_close_children_cascades_from_a_done_epic_through_issue_to_task(self):
        epic = self.client.add_item("Epic", "Epic 9 — Shipped", state="Done")
        issue = self.client.add_item("Issue", "PROJ-901 \u00b7 Open issue", parent=epic, state="Doing")
        task = self.client.add_item("Task", "Open task", parent=issue, state="To Do")
        commands.close_children(self.cfg, self.client, Args(go=True))
        self.assertEqual(self.client.items[issue]["fields"]["System.State"], "Done")
        self.assertEqual(self.client.items[task]["fields"]["System.State"], "Done")

    def test_close_children_leaves_descendants_of_an_open_epic_alone(self):
        epic = self.client.add_item("Epic", "Epic 9 — In flight", state="Doing")
        issue = self.client.add_item("Issue", "PROJ-902 \u00b7 Open issue", parent=epic, state="Doing")
        task = self.client.add_item("Task", "Open task", parent=issue, state="To Do")
        commands.close_children(self.cfg, self.client, Args(go=True))
        self.assertEqual(self.client.items[issue]["fields"]["System.State"], "Doing")
        self.assertEqual(self.client.items[task]["fields"]["System.State"], "To Do")

    def test_close_children_dry_run_changes_nothing(self):
        _, tasks = self._seed_issue_with_tasks("Done", ["Doing", "To Do"])
        commands.close_children(self.cfg, self.client, Args(go=False))
        self.assertEqual(
            [self.client.items[t]["fields"]["System.State"] for t in tasks],
            ["Doing", "To Do"],
        )

    def test_close_children_leaves_open_issue_tasks_alone(self):
        _, tasks = self._seed_issue_with_tasks("Doing", ["Doing", "To Do"])
        commands.close_children(self.cfg, self.client, Args(go=True))
        self.assertEqual(
            [self.client.items[t]["fields"]["System.State"] for t in tasks],
            ["Doing", "To Do"],
        )

    def test_close_children_assigns_parent_to_unassigned_task_when_flagged(self):
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        issue = self.client.add_item(
            "Issue", "PROJ-101 · x", parent=epic, state="Done",
            assigned_to={"uniqueName": "alice@example.com", "displayName": "Alice"},
        )
        task = self.client.add_item("Task", "Task 0", parent=issue, state="Doing")
        commands.close_children(self.cfg, self.client, Args(go=True, assign_from_parent=True))
        f = self.client.items[task]["fields"]
        self.assertEqual(f["System.State"], "Done")
        self.assertEqual(f["System.AssignedTo"], "alice@example.com")

    def test_close_children_does_not_overwrite_an_assigned_task(self):
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        issue = self.client.add_item(
            "Issue", "PROJ-101 · x", parent=epic, state="Done",
            assigned_to={"uniqueName": "alice@example.com"},
        )
        task = self.client.add_item(
            "Task", "Task 0", parent=issue, state="Doing",
            assigned_to={"uniqueName": "bob@example.com"},
        )
        commands.close_children(self.cfg, self.client, Args(go=True, assign_from_parent=True))
        f = self.client.items[task]["fields"]
        self.assertEqual(f["System.State"], "Done")           # still closed
        self.assertEqual(f["System.AssignedTo"], {"uniqueName": "bob@example.com"})  # untouched

    def test_close_children_without_flag_leaves_assignee_untouched(self):
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        issue = self.client.add_item(
            "Issue", "PROJ-101 · x", parent=epic, state="Done",
            assigned_to={"uniqueName": "alice@example.com"},
        )
        task = self.client.add_item("Task", "Task 0", parent=issue, state="Doing")
        commands.close_children(self.cfg, self.client, Args(go=True))
        f = self.client.items[task]["fields"]
        self.assertEqual(f["System.State"], "Done")
        self.assertNotIn("System.AssignedTo", f)

    def test_close_children_uses_configured_done_state(self):
        cfg = build_cfg(os.path.join(self.tmp.name, "out.csv"), states={"done": "Closed"})
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        issue = self.client.add_item("Issue", "PROJ-101 · x", parent=epic, state="Closed")
        task = self.client.add_item("Task", "Task 0", parent=issue, state="Active")
        commands.close_children(cfg, self.client, Args(go=True))
        self.assertEqual(self.client.items[task]["fields"]["System.State"], "Closed")

    # --- dedup -------------------------------------------------------------
    def test_dedup_removes_duplicate_issue_keeping_lowest_id(self):
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        first = self.client.add_item("Issue", "PROJ-101 · Build the core event store", parent=epic)
        dup = self.client.add_item("Issue", "PROJ-101 · duplicate", parent=epic)
        commands.dedup(self.cfg, self.client, Args(go=True))
        self.assertIn(first, self.client.items)
        self.assertNotIn(dup, self.client.items)

    def test_dedup_cascades_to_child_tasks_of_removed_duplicate(self):
        # The real-incident shape: the duplicate Issue carries its own child
        # Tasks. Deleting only the Issue would orphan them, so dedup must remove
        # the whole subtree.
        epic = self.client.add_item("Epic", "Epic 1 — Platform Foundations")
        first = self.client.add_item("Issue", "PROJ-101 · Build the core event store", parent=epic)
        dup = self.client.add_item("Issue", "PROJ-101 · Build the core event store", parent=epic)
        orphan = self.client.add_item("Task", "Redundant child task", parent=dup)
        kept_task = self.client.add_item("Task", "Canonical child task", parent=first)
        commands.dedup(self.cfg, self.client, Args(go=True))
        self.assertIn(first, self.client.items)
        self.assertNotIn(dup, self.client.items)
        self.assertNotIn(orphan, self.client.items)   # child of the removed dup
        self.assertIn(kept_task, self.client.items)    # child of the kept Issue

    # --- sprints -----------------------------------------------------------
    def _sprint_cfg(self):
        return build_cfg(
            os.path.join(self.tmp.name, "out.csv"),
            iterations=[
                {"name": "Sprint 1", "start": "2026-01-01", "finish": "2026-01-14",
                 "items": ["PROJ-101", "PROJ-102"]},
                {"name": "Sprint 2", "items": ["PROJ-201"]},
            ],
        )

    def _issue_by_code(self, code):
        return next(
            it for it in self.client.items.values()
            if it["fields"].get("System.Title", "").startswith(code)
        )

    def _issue_id_by_code(self, code):
        return next(
            wid for wid, it in self.client.items.items()
            if it["fields"].get("System.Title", "").startswith(code)
        )

    def test_sprints_dry_run_creates_and_assigns_nothing(self):
        cfg = self._sprint_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.sprints(cfg, self.client, Args(go=False))
        self.assertEqual(self.client.iterations, {})
        for it in self.client.items.values():
            self.assertNotIn("System.IterationPath", it["fields"])

    def test_sprints_creates_nodes_and_assigns_issues_and_tasks(self):
        cfg = self._sprint_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.resync_tasks(cfg, self.client, Args(go=True))
        commands.sprints(cfg, self.client, Args(go=True))

        self.assertEqual(set(self.client.iterations), {"Sprint 1", "Sprint 2"})
        self.assertEqual(len(self.client.team_iterations), 2)
        # Issue -> correct iteration path.
        self.assertEqual(
            self._issue_by_code("PROJ-101")["fields"]["System.IterationPath"],
            "DemoProject\\Sprint 1",
        )
        self.assertEqual(
            self._issue_by_code("PROJ-201")["fields"]["System.IterationPath"],
            "DemoProject\\Sprint 2",
        )
        # Child Tasks cascade to their parent Issue's sprint.
        for cid in self._children(self._issue_id_by_code("PROJ-101")):
            self.assertEqual(
                self.client.items[cid]["fields"]["System.IterationPath"],
                "DemoProject\\Sprint 1",
            )

    def test_sprints_no_tasks_leaves_child_tasks_unassigned(self):
        cfg = self._sprint_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.resync_tasks(cfg, self.client, Args(go=True))
        commands.sprints(cfg, self.client, Args(go=True, no_tasks=True))
        issue_id = self._issue_id_by_code("PROJ-101")
        self.assertEqual(
            self.client.items[issue_id]["fields"]["System.IterationPath"],
            "DemoProject\\Sprint 1",
        )
        for cid in self._children(issue_id):
            self.assertNotIn("System.IterationPath", self.client.items[cid]["fields"])

    def test_sprints_assign_only_skips_node_creation(self):
        cfg = self._sprint_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.sprints(cfg, self.client, Args(go=True, assign_only=True))
        self.assertEqual(self.client.iterations, {})
        self.assertEqual(
            self._issue_by_code("PROJ-102")["fields"]["System.IterationPath"],
            "DemoProject\\Sprint 1",
        )

    def test_sprints_without_reset_on_missing_keeps_failed_status(self):
        cfg = self._sprint_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.resync_tasks(cfg, self.client, Args(go=True))

        original_patch = self.client.patch
        def mock_patch(wid, ops):
            for op in ops:
                if op["path"] == "/fields/System.IterationPath" and "Sprint 1" in op["value"]:
                    return 400, "Sprint not found"
            return original_patch(wid, ops)
        self.client.patch = mock_patch

        res = commands.sprints(cfg, self.client, Args(go=True, reset_on_missing=False))
        self.assertEqual(res, 1)

        issue_id = self._issue_id_by_code("PROJ-101")
        self.assertNotIn("System.IterationPath", self.client.items[issue_id]["fields"])

    def test_sprints_with_reset_on_missing_falls_back_to_root(self):
        cfg = self._sprint_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.resync_tasks(cfg, self.client, Args(go=True))

        original_patch = self.client.patch
        def mock_patch(wid, ops):
            for op in ops:
                if op["path"] == "/fields/System.IterationPath" and "Sprint 1" in op["value"]:
                    return 400, "Sprint not found"
            return original_patch(wid, ops)
        self.client.patch = mock_patch

        res = commands.sprints(cfg, self.client, Args(go=True, reset_on_missing=True))
        self.assertEqual(res, 1)

        issue_id = self._issue_id_by_code("PROJ-101")
        self.assertEqual(
            self.client.items[issue_id]["fields"]["System.IterationPath"],
            "DemoProject",
        )
        for cid in self._children(issue_id):
            self.assertEqual(
                self.client.items[cid]["fields"]["System.IterationPath"],
                "DemoProject",
            )

    def test_sprints_returns_error_when_none_configured(self):
        self.assertEqual(commands.sprints(self.cfg, self.client, Args(go=True)), 1)

    # --- assign ------------------------------------------------------------
    def _assign_cfg(self):
        return build_cfg(
            os.path.join(self.tmp.name, "out.csv"),
            assignees={
                "alice@example.com": ["PROJ-101", "PROJ-102"],
                "bob@example.com": ["PROJ-201"],
            },
        )

    def test_assign_dry_run_changes_nothing(self):
        cfg = self._assign_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.assign(cfg, self.client, Args(go=False))
        for it in self.client.items.values():
            self.assertNotIn("System.AssignedTo", it["fields"])

    def test_assign_sets_issue_and_cascades_tasks(self):
        cfg = self._assign_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.resync_tasks(cfg, self.client, Args(go=True))
        commands.assign(cfg, self.client, Args(go=True))
        issue_id = self._issue_id_by_code("PROJ-101")
        self.assertEqual(
            self.client.items[issue_id]["fields"]["System.AssignedTo"], "alice@example.com"
        )
        children = self._children(issue_id)
        self.assertTrue(children)
        for cid in children:
            self.assertEqual(
                self.client.items[cid]["fields"]["System.AssignedTo"], "alice@example.com"
            )
        self.assertEqual(
            self.client.items[self._issue_id_by_code("PROJ-201")]["fields"]["System.AssignedTo"],
            "bob@example.com",
        )

    def test_assign_no_tasks_leaves_child_tasks_unassigned(self):
        cfg = self._assign_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.resync_tasks(cfg, self.client, Args(go=True))
        commands.assign(cfg, self.client, Args(go=True, no_tasks=True))
        issue_id = self._issue_id_by_code("PROJ-101")
        self.assertEqual(
            self.client.items[issue_id]["fields"]["System.AssignedTo"], "alice@example.com"
        )
        for cid in self._children(issue_id):
            self.assertNotIn("System.AssignedTo", self.client.items[cid]["fields"])

    def test_assign_only_unassigned_does_not_overwrite(self):
        cfg = self._assign_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        issue_id = self._issue_id_by_code("PROJ-101")
        # Pre-assign to someone else; --only-unassigned must leave it alone.
        self.client.items[issue_id]["fields"]["System.AssignedTo"] = {"uniqueName": "carol@example.com"}
        commands.assign(cfg, self.client, Args(go=True, only_unassigned=True))
        self.assertEqual(
            self.client.items[issue_id]["fields"]["System.AssignedTo"],
            {"uniqueName": "carol@example.com"},
        )

    def test_assign_is_idempotent_skips_already_correct(self):
        cfg = self._assign_cfg()
        commands.import_items(cfg, self.client, Args(go=True))
        commands.assign(cfg, self.client, Args(go=True))
        issue_id = self._issue_id_by_code("PROJ-101")
        # Already resolves to alice; a second run must not re-plan it.
        import contextlib
        import io
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            commands.assign(cfg, self.client, Args(go=True))
        self.assertIn("0 issue(s)", out.getvalue())
        self.assertIn("already correct", out.getvalue())

    def test_assign_returns_error_when_none_configured(self):
        commands.import_items(self.cfg, self.client, Args(go=True))
        self.assertEqual(commands.assign(self.cfg, self.client, Args(go=True)), 1)

    # --- audit -------------------------------------------------------------
    def test_audit_passes_after_full_sync(self):
        commands.import_items(self.cfg, self.client, Args(go=True))
        commands.resync_tasks(self.cfg, self.client, Args(go=True))
        self.assertEqual(commands.audit(self.cfg, self.client), 0)

    def test_audit_fails_on_description_drift_against_the_backlog(self):
        commands.import_items(self.cfg, self.client, Args(go=True))
        commands.resync_tasks(self.cfg, self.client, Args(go=True))
        self.assertEqual(commands.audit(self.cfg, self.client), 0)
        # Corrupt an Issue's description on the board. Audit compares against the
        # backlog (not the CSV), so it must catch the drift and fail.
        issue_id = next(
            wid for wid, it in self.client.items.items()
            if it["fields"]["System.WorkItemType"] == "Issue"
        )
        self.client.items[issue_id]["fields"]["System.Description"] = "wiped"
        self.assertEqual(commands.audit(self.cfg, self.client), 1)

    def test_audit_fails_when_a_done_parent_has_open_descendants(self):
        commands.import_items(self.cfg, self.client, Args(go=True))
        commands.resync_tasks(self.cfg, self.client, Args(go=True))
        self.assertEqual(commands.audit(self.cfg, self.client), 0)
        # Close an Issue but leave its Tasks open. Azure DevOps does not cascade
        # state, so the board is now internally inconsistent even though every
        # title and description still matches the backlog.
        issue_id = next(
            wid for wid, it in self.client.items.items()
            if it["fields"]["System.WorkItemType"] == "Issue" and it["relations"]
        )
        self.client.items[issue_id]["fields"]["System.State"] = "Done"
        self.assertEqual(commands.audit(self.cfg, self.client), 1)

    def test_audit_passes_when_every_child_is_done_but_the_parent_is_not(self):
        commands.import_items(self.cfg, self.client, Args(go=True))
        commands.resync_tasks(self.cfg, self.client, Args(go=True))
        for it in self.client.items.values():
            if it["fields"]["System.WorkItemType"] == "Task":
                it["fields"]["System.State"] = "Done"
        # Closing the parent is a judgement call, so this is reported, not failed.
        self.assertEqual(commands.audit(self.cfg, self.client), 0)

    def test_audit_fails_when_issue_missing(self):
        commands.import_items(self.cfg, self.client, Args(go=True))
        commands.resync_tasks(self.cfg, self.client, Args(go=True))
        issue_id = next(
            wid for wid, it in self.client.items.items()
            if it["fields"]["System.WorkItemType"] == "Issue"
        )
        self.client.delete(issue_id)
        self.assertEqual(commands.audit(self.cfg, self.client), 1)

    def test_audit_fails_on_duplicate_issue_on_board(self):
        commands.import_items(self.cfg, self.client, Args(go=True))
        commands.resync_tasks(self.cfg, self.client, Args(go=True))
        self.assertEqual(commands.audit(self.cfg, self.client), 0)
        # Add a byte-for-byte identical second work item for the same code. Every
        # field-level check collapses the two into one map entry and still sees a
        # match, so ONLY the explicit duplicate gate can fail this.
        real = next(
            it for it in self.client.items.values()
            if it["fields"]["System.WorkItemType"] == "Issue"
            and it["fields"]["System.Title"].startswith("PROJ-101")
        )
        epic = next(
            wid for wid, it in self.client.items.items()
            if it["fields"]["System.WorkItemType"] == "Epic"
        )
        self.client.add_item(
            "Issue", real["fields"]["System.Title"],
            desc=real["fields"].get("System.Description", ""), parent=epic,
        )
        self.assertEqual(commands.audit(self.cfg, self.client), 1)


if __name__ == "__main__":
    unittest.main()
