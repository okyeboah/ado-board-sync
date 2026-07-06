"""Command-line entry point."""
import argparse
import sys

from . import client as client_mod
from . import commands, config


def _build_parser():
    p = argparse.ArgumentParser(
        prog="ado-board-sync",
        description="Drive an Azure DevOps board from a Markdown backlog.",
    )
    p.add_argument(
        "-c", "--config",
        help="path to board.config.json (default: ./board.config.json)",
    )
    sub = p.add_subparsers(dest="cmd", required=True)

    def add(name, help_, go=False):
        sp = sub.add_parser(name, help=help_)
        if go:
            sp.add_argument(
                "--go", action="store_true",
                help="apply changes (default: dry-run preview)",
            )
        return sp

    add("gen-csv", "Generate the import CSV from the backlog Markdown")
    add("import", "Create Epics/Issues that are missing from the board", go=True)
    add("resync", "Resync Epic/Issue titles + descriptions from the CSV", go=True)
    add("resync-tasks", "Reconcile each Issue's child Tasks to backlog bullets", go=True)
    cc = add("close-children", "Set child Tasks to Done for every Issue already Done", go=True)
    cc.add_argument(
        "--assign-from-parent", action="store_true",
        help="also copy the Issue's assignee onto each closed Task that is currently unassigned",
    )
    add("dedup", "Delete duplicate work items", go=True)
    add("audit", "Read-only: verify the board matches the backlog (exit 1 on drift)")
    add("sync", "gen-csv -> import -> resync -> resync-tasks -> audit", go=True)

    sp = add("sprints", "Create sprint iterations and assign Issues (+ Tasks) to them", go=True)
    sp.add_argument(
        "--assign-only", action="store_true",
        help="skip iteration-node creation; only set iteration paths (nodes must exist)",
    )
    sp.add_argument(
        "--no-tasks", action="store_true",
        help="assign Issues only; do not cascade the iteration path to child Tasks",
    )
    sp.add_argument(
        "--reset-on-missing", action="store_true",
        help="reset Issue iteration path to project root if sprint assignment fails",
    )
    return p


def main(argv=None):
    args = _build_parser().parse_args(argv)
    cfg = config.load(args.config)

    if args.cmd == "gen-csv":
        return commands.gen_csv(cfg, args)

    pat = cfg.resolve_pat()
    if not pat:
        sys.exit(
            f"No PAT found. Set ${cfg.pat_env} or create a '{cfg.pat_file}' file "
            "next to board.config.json (it should be gitignored)."
        )
    client = client_mod.Client(cfg, pat)

    if args.cmd == "import":
        return commands.import_items(cfg, client, args)
    if args.cmd == "resync":
        return commands.resync(cfg, client, args)
    if args.cmd == "resync-tasks":
        return commands.resync_tasks(cfg, client, args)
    if args.cmd == "close-children":
        return commands.close_children(cfg, client, args)
    if args.cmd == "sprints":
        return commands.sprints(cfg, client, args)
    if args.cmd == "dedup":
        return commands.dedup(cfg, client, args)
    if args.cmd == "audit":
        return commands.audit(cfg, client, args)
    if args.cmd == "sync":
        commands.gen_csv(cfg, args)
        commands.import_items(cfg, client, args)
        commands.resync(cfg, client, args)
        commands.resync_tasks(cfg, client, args)
        return commands.audit(cfg, client, args)

    return 1


if __name__ == "__main__":
    sys.exit(main())
