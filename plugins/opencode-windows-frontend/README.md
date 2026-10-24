# OpenCode Windows frontend plugin

Server plugin for OpenCode **v2.0.30+** (RPC checked against v2.1.3), id `opencode.windows-frontend`.
No tools, permissions, listeners, Billion imports, network calls, or environment
writes. Billion discovery samples the process-wide environment declaration;
`ready` means a valid declaration exists, not that an engine answered.

## Protocol v1

Call `POST /api/rpc/opencode.windows-frontend/:method` using OpenCode's existing
authentication, base path, and verified `location[directory]` query parameter.
Bodies are `{ "input": ... }`; successful replies are `{ "output": ... }`.

- `hello`: `{ "protocolVersions": [1] }` →
  `{ "protocolVersion": 1, "capabilities": ["billion.origin.v1", "model.favorites.v1"] }`.
  At most eight positive integer versions; no common version returns
  `{ "protocolVersion": null, "supportedVersions": [1], "capabilities": [] }`.
- `billion.origin`: exactly `{}` → ready with a strictly validated loopback HTTP
  `origin`, or unavailable with `reason: "not-ready" | "disabled"` (all fixed v1).
  `BILLION_CONTEXT_PLUGIN === "0"` takes precedence; otherwise sample
  `BILLION_CONTEXT_PROXY` on every call. No `hello` prerequisite or cache.
- `model.favorites.get`: exactly `{}` → `{ "favorites": [{ "providerID": "provider", "modelID": "vendor/model" }] }`.
- `model.favorites.set`: `{ "favorites": [...] }` → the same shape. At most 200
  entries; exact entry keys, nonempty IDs without whitespace/control characters,
  provider ID ≤256 UTF-16 units and model ID ≤512. Reads the server host's
  `${XDG_STATE_HOME || ~/.local/state}/opencode/model.json`, not a client file.
  Missing state returns an empty list. Set freshly reads the state, replaces only
  `favorite`, and preserves all other root members verbatim (including formatting
  and large numbers). Invalid JSON/non-object roots fail without overwrite.
  Writes use a unique same-directory temp + rename, like the TUI; no distributed
  lock/CAS is provided against a simultaneous TUI write. Get rejects malformed or
  oversized stored favorites instead of silently truncating them.
- No events. Inputs and outputs reject extra fields. Shared HTTP-envelope
  examples for the C# client are in `tests/fixtures/*.json`.

## Build and test (Nix only)

From this directory, use an explicit **path flake**, because Git flakes omit
untracked files:

```sh
nix build "path:$PWD" -o dist
nix develop "path:$PWD" -c tsc --noEmit
nix develop "path:$PWD" -c node --test
```

The pinned dev shell provides Node.js, esbuild, and TypeScript. There are no npm
dependencies or npm installation steps. The derivation bundles offline into
`dist/package.json` + one ESM `dist/index.js`; only `node:` builtins may remain
external. Source tests use Node's native TypeScript stripping; the built-import
test requires the preceding build. The filtered build source excludes tests,
docs, and generated output, so refreshing `dist` does not change its input.

After isolated-host validation and deployment approval, declare the directory
in `opencode.json` (do not point at the JavaScript file):

```json
{
  "plugins": [
    "file:///mnt/c/Users/Zhang/source/repos/AIFrontend-history-20261020-lanes/fav/plugins/opencode-windows-frontend/dist"
  ]
}
```

Keep existing plugin entries. Updating this GC-rooted symlink and restarting
OpenCode is a deployment action. Remove the entry to roll back. This package
does not edit configuration or start a server. See `NOTES.md` for source-pinned
loader, schema, and teardown evidence.
