# AT2: Librarian runtime inventory (2026-10-23)

Read-only `opencode api GET /api/agent` against the supplied OpenCode v2.1.3
backend returned these 15 agent IDs:

- primary: orchestrator, compaction, title, summary, raw
- visible subagent: explorer, oracle, designer, fixer, council, secretary
- hidden subagent: councillor, councillor-alpha, councillor-beta, councillor-gamma

`librarian` is absent, rather than removed by the tree projection. Read-only
`GET /api/config` returned the document `/home/ming/.config/opencode/opencode.json`
whose `info.agents` keys are build, explore, general, plan, title; it has no
librarian entry either. No runtime configuration was changed.

The existing projection excludes primary agents and hidden idle agents. The
default working-only filter excludes agents without running child sessions;
“全部” includes configured visible idle subagents. A regression assertion proves
that a non-hidden configured librarian follows exactly these same rules. No
synthetic row or special-case name filter was added. Making Librarian available
requires the user's runtime/plugin configuration to expose it via `/api/agent`.

Sanitized inventories (IDs/modes/hidden/color and config key names only) are saved
outside the repository at `/tmp/opencode/aif-t-tree-runtime-agents-20261023.json`
and `/tmp/opencode/aif-t-tree-runtime-config-20261023.json`.
