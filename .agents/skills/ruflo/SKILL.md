---
name: ruflo
description: What ruflo (claude-flow v3) was in ado-board-sync, why it was retired on 2026-09-03, and what to do if someone re-introduces it. Load when a task mentions ruflo, claude-flow, swarm orchestration, SPARC modes, hive-mind, ruvector.db, or when an unexpected .mcp.json / .claude-flow / .swarm directory appears in this repo.
---

# ruflo in ado-board-sync — retired 2026-09-03

`ruflo` (published as `ruflo`, internally claude-flow v3) was a swarm-orchestration
and coordination layer installed into 7 repositories on 2026-08-02. It was retired
from every brain-primary repo on 2026-08-24, and ado-board-sync was the last repo
holding it. This skill is the project-scoped record of that, kept per the agent-brain
Phase 4 plan ("ado-board-sync `.agents` — keep `ruflo` as its PROJECT skill in-repo").

## Why it was retired here

Every ruflo store was empty or invalid at retirement, so nothing was migrated —
the "merge into the brain" option turned out to be empty, which is what settled
the decision. `~/.agent/protocols/harness-precedence.md` (§"ruflo-primary")
holds the measurements, and the stores themselves are preserved in the backup
named at the end of this file.

## What replaced it

The user-scope portable brain at `~/.agent/`, fed one-way from the private
`agent-brain` repository: `agent-brain -> ~/.agent/skills -> ~/.agents/skills
(mirror) -> repo stubs`. Durable lessons go through
`python3 ~/.agent/tools/learn.py`, never into a harness-private store. See
`~/.agent/protocols/harness-precedence.md` for the full ownership table and the
uninstall trail.

## If ruflo reappears

A stray `ruflo init` or `npx ruflo@latest ...` re-creates `.mcp.json`, a full set of
`.claude/settings.json` hooks, `.claude/helpers/`, `.claude-flow/`, `.swarm/` and
`ruvector.db`. That breaks the standing rule **one hook system per repo, ever** —
two hook systems means a lesson written into one store cannot be recalled from the
other, which is the exact failure the precedence protocol exists to prevent.

If you find those artefacts here:

1. Do not run `ruflo` or `npx ruflo` to inspect them — invoking the CLI re-creates config.
2. Confirm they are ignored: every one of them is listed in this repo's `.gitignore`,
   so removal is a local-only change and cannot touch tracked work.
3. Remove them, then re-check `.claude/settings.json` — the brain hooks
   (`$HOME/.agent/harness/hooks/claude_code_post_tool.py` for PostToolUse,
   `auto_dream.py` for Stop, `ztk rewrite` for PreToolUse) must be the only ones present.

`ztk` is **not** ruflo. It is an agentic-stack framework skill and its
`ztk rewrite --skip-permissions` PreToolUse hook stays.

## Historical reference

The full ruflo documentation that used to occupy this repo's `CLAUDE.md` — swarm
topologies, SPARC modes, the MCP tool surface, agent routing tables, the 3-tier
model routing and the background-worker schedule — is preserved at
`~/.ruflo-retirement-backup-20260903T102950Z/repo/CLAUDE.md`, alongside the
`.claude-flow`, `.swarm` and `ruvector.db` state as it stood at retirement and a
`RESTORE.md` giving the reversal procedure.
