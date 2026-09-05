"""Regression coverage for the N+1 request patterns that made resync-tasks/sprints/assign feel
slow on boards with many Issues: each Issue's child Tasks used to cost its own relations-expand
round trip (see _child_tasks in commands.py, now replaced by the _tasks_by_parent batched lookup
for multi-Issue runs). These tests pin down that the request count stays flat as the number of
Issues grows, using FakeClient.call_counts as a stand-in for network round trips.
"""
import tempfile
import unittest

from ado_board_sync import commands

from .fake_client import FakeClient
from .support import Args, build_cfg, write_backlog


def _issue_code(i):
    return f"PROJ-{100 + i}"


def _seed_issues(client, cfg, n):
    """Create n Issues (PROJ-101..PROJ-10n) on the board, each with no Tasks yet."""
    for i in range(1, n + 1):
        client.add_item(cfg.types["story"], f"{_issue_code(i)} · Issue {i}")


def _backlog_text(n):
    """A minimal backlog with n Issues under one Epic, each with two Task bullets."""
    lines = ["## Epic 1 — Scale Test", ""]
    for i in range(1, n + 1):
        lines.append(f"### {_issue_code(i)} · Issue {i}")
        lines.append(f"- Task A for {_issue_code(i)}")
        lines.append(f"- Task B for {_issue_code(i)}")
        lines.append("")
    return "\n".join(lines)


def _run_resync_tasks(n):
    with tempfile.TemporaryDirectory() as tmpdir:
        board_path = write_backlog(tmpdir, text=_backlog_text(n))
        cfg = build_cfg(csv_path="unused.csv", board_file=board_path)
        client = FakeClient(cfg)
        _seed_issues(client, cfg, n)
        commands.resync_tasks(cfg, client, Args(go=False))
        return client.call_counts


def _run_sprints(n):
    codes = [_issue_code(i) for i in range(1, n + 1)]
    cfg = build_cfg(csv_path="unused.csv", iterations=[{"name": "Sprint 1", "items": codes}])
    client = FakeClient(cfg)
    _seed_issues(client, cfg, n)
    commands.sprints(cfg, client, Args(go=False))
    return client.call_counts


def _run_assign(n):
    codes = [_issue_code(i) for i in range(1, n + 1)]
    cfg = build_cfg(csv_path="unused.csv", assignees={"alice@example.com": codes})
    client = FakeClient(cfg)
    _seed_issues(client, cfg, n)
    commands.assign(cfg, client, Args(go=False))
    return client.call_counts


def _run_dedup(n):
    """A board holding a duplicate pair of Issues per code -- the case dedup exists for."""
    cfg = build_cfg(csv_path="unused.csv")
    client = FakeClient(cfg)
    epic = client.add_item(cfg.types["epic"], "Epic 1 — Scale Test")
    for i in range(1, n + 1):
        for _copy in (1, 2):
            client.add_item(
                cfg.types["story"], f"{_issue_code(i)} · Issue {i}", parent=epic
            )
    commands.dedup(cfg, client, Args(go=False))
    return client.call_counts


def _run_audit(n):
    with tempfile.TemporaryDirectory() as tmpdir:
        board_path = write_backlog(tmpdir, text=_backlog_text(n))
        cfg = build_cfg(csv_path="unused.csv", board_file=board_path)
        client = FakeClient(cfg)
        epic = client.add_item(cfg.types["epic"], "Epic 1 — Scale Test")
        for i in range(1, n + 1):
            issue = client.add_item(
                cfg.types["story"],
                f"{_issue_code(i)} · Issue {i}",
                desc="<p>Body</p>",
                parent=epic,
            )
            for letter in ("A", "B"):
                client.add_item(
                    cfg.types["task"],
                    f"Task {letter} for {_issue_code(i)}",
                    parent=issue, state="To Do",
                )
        commands.audit(cfg, client)
        return client.call_counts


class RequestCountRegressionTest(unittest.TestCase):
    COMMANDS = {
        "resync-tasks": _run_resync_tasks,
        "sprints": _run_sprints,
        "assign": _run_assign,
    }

    def test_request_count_does_not_scale_with_issue_count(self):
        for name, run in self.COMMANDS.items():
            with self.subTest(command=name):
                small = run(5)
                large = run(50)

                # No per-Issue relations-expand left: that was the N+1 pattern this guards against.
                self.assertEqual(small["get_item"], 0)
                self.assertEqual(large["get_item"], 0)

                # The batched wiql/get_items calls are a fixed number of round trips, not one per Issue.
                self.assertEqual(small["wiql"], large["wiql"])
                self.assertEqual(small["get_items"], large["get_items"])

    def test_dedup_reads_parents_in_the_batched_pass_not_per_task(self):
        small = _run_dedup(5)
        large = _run_dedup(50)

        # Duplicate scanning used to resolve each Task's parent through its own
        # relations-expand round trip; System.Parent now rides along on the one
        # batched read beside everything else.
        self.assertEqual(small["get_item"], 0)
        self.assertEqual(large["get_item"], 0)
        self.assertEqual(small["wiql"], large["wiql"])
        self.assertEqual(small["get_items"], large["get_items"])

    def test_audit_serves_every_check_from_one_board_read(self):
        small = _run_audit(5)
        large = _run_audit(50)

        # audit used to read Epics/Issues and then read the hierarchy again (two WIQL queries
        # plus two batched gets). One read pair now serves identity, description parity, Task
        # parity, and state-vs-hierarchy checks alike.
        self.assertEqual(1, large["wiql"])
        self.assertEqual(1, large["get_items"])
        self.assertEqual(small["wiql"], large["wiql"])
        self.assertEqual(small["get_items"], large["get_items"])


if __name__ == "__main__":
    unittest.main()
