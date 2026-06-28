"""ado-board-sync — drive an Azure DevOps board from a Markdown backlog.

A project supplies a ``board.config.json`` describing its organisation/project,
backlog file, issue code prefix, and work-item type names. The commands then
parse the backlog and reconcile the board (Epics, Issues/Stories, Tasks) to it.
"""

__version__ = "0.1.0"
