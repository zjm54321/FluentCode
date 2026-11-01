## Cloned Dependency Source

`.slim/clonedeps.json` records dependency-source configuration. When dependency clones are present under `.slim/clonedeps/repos/`, they are read-only inspection sources; do not edit them.

- `ranxianglei/billion-context` at `d64139f9cd2d64fdd4fb83fffced37cc2a0f57a7` (`v0.1.196`) supplies the OpenCode discovery RPC reference.

Preserve unrelated uncommitted changes and respect the files assigned to concurrent implementation lanes.

## FluentCode Workspace

- Application sources are under `src/FluentCode/`. From a fresh clone, explicitly run `tools/browser-runtime/prepare.ps1` first; its tracked inputs regenerate the ignored runtime bundle.
- Use `scripts/build-debug.ps1` for Debug build/publish/hash checks and `scripts/publish-release.ps1` for Release. Both accept an optional new `-EvidenceRoot`; paths are resolved from the scripts' repository location. `scripts/build.ps1` remains the build-only entry point.
- Work only in this checkout, not sibling or frozen checkouts. Vendored dependency source is under `src/ThirdParty/` and should change only when explicitly assigned.
