## Cloned Dependency Source

`.slim/clonedeps.json` records dependency-source configuration. When dependency clones are present under `.slim/clonedeps/repos/`, they are read-only inspection sources; do not edit them.

- `ranxianglei/billion-context` at `d64139f9cd2d64fdd4fb83fffced37cc2a0f57a7` (`v0.1.196`) supplies the OpenCode discovery RPC reference.

Preserve unrelated uncommitted changes and respect the files assigned to concurrent implementation lanes.

## FluentCode Workspace

- Build `FluentCode.sln`; application sources are under `src/FluentCode/`.
- Work only in this checkout, not sibling or frozen checkouts. Vendored dependency source is under `src/ThirdParty/` and should change only when explicitly assigned.
