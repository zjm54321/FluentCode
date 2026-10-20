# v2.0.30 source verification

Inspected exact raw sources at tag `v2.0.30` before implementation (2026-10-16).
Source identity: `anomalyco/opencode`, commit
`f16ef9ad8878279ea2be3cfab7baeed97aef61aa`.

## Promise setup, context, and disposal

- [`packages/core/src/plugin/module.ts:78-90`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/core/src/plugin/module.ts#L78-L90)
  accepts default `{ id, setup }` as well as `{ id, effect }`.
  [`packages/core/src/plugin/module.ts:125-150`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/core/src/plugin/module.ts#L125-L150)
  adapts setup-style plugins with `PluginPromise.fromPromise` and passes host
  context plus configured `options`.
- [`packages/core/src/plugin/promise.ts:3`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/core/src/plugin/promise.ts#L3)
  re-exports the actual adapter from `@opencode/plugin/promise/adapter`.
- [`packages/plugin/src/promise/plugin.ts:26-61`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/plugin/src/promise/plugin.ts#L26-L61)
  defines the promise Context: app, location, options, agent, aisdk, command,
  credential, event, experimental, integration, mcp, model, generate, permission,
  plugin, provider, reference, **rpc**, session, shell, skill, storage, tool, vcs,
  websearch, and worktree. `setup(context)` returns a cleanup function or void,
  optionally asynchronously. We consume only `rpc.register`.
- [`packages/core/src/plugin/host.ts:115-119`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/core/src/plugin/host.ts#L115-L119)
  wires the Effect host's `rpc.register`;
  [`packages/plugin/src/promise/adapter.ts:465`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/plugin/src/promise/adapter.ts#L465)
  supplies the converted promise API to setup. The Effect API context is also
  explicitly declared at
  [`packages/plugin/src/effect/plugin.ts:26-59`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/plugin/src/effect/plugin.ts#L26-L59).
- [`packages/plugin/src/promise/rpc.ts:12-30`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/plugin/src/promise/rpc.ts#L12-L30):
  `register(definition, handlers)` returns `Promise<RpcRegistration<D>>`;
  handlers return Promises, with a context containing `signal` and `error`.
  [`packages/plugin/src/promise/registration.ts:1-3`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/plugin/src/promise/registration.ts#L1-L3)
  defines `dispose: () => Promise<void>`.
- [`packages/plugin/src/promise/adapter.ts:121-159`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/plugin/src/promise/adapter.ts#L121-L159)
  forwards definitions unchanged, wraps handlers in Effect, and returns the
  promise disposer. In contrast,
  [`packages/plugin/src/effect/rpc.ts:18-28`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/plugin/src/effect/rpc.ts#L18-L28)
  exposes Effect-valued registration and disposal.
- [`packages/plugin/src/promise/adapter.ts:618-621`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/plugin/src/promise/adapter.ts#L618-L621)
  uses `Effect.acquireRelease` to await setup acquisition and invoke its returned
  cleanup on teardown. We await registration in setup, then return an idempotent
  promise disposer. There is no detached registration callback that can install
  after our cleanup; a pending registration completes before cleanup is invoked
  by the host's acquire/release mechanism. Its captured scope also owns the
  registration:
  [`packages/core/src/rpc.ts:69-89`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/core/src/rpc.ts#L69-L89).

## Structural schemas: no Effect bundle needed

- [`packages/schema/src/rpc.ts:13-51`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/schema/src/rpc.ts#L13-L51)
  explicitly admits portable method schemas: **Standard Schema v1 or JSON
  Schema**, not just Effect Schema objects. The structural definition is
  `{ id, methods, events }`.
  [`packages/schema/src/rpc.ts:54-59`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/schema/src/rpc.ts#L54-L59)
  returns the definition unchanged after a reserved-error-name check; it adds no
  brand. This plugin has no declared errors.
- Runtime proof:
  [`packages/core/src/rpc.ts:203-229`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/core/src/rpc.ts#L203-L229)
  branches on Effect Schema, then Standard Schema (`~standard.validate`), then
  converts a plain draft-2020-12 JSON Schema to an Effect codec. Standard Schema
  issues fail the call; output schemas are validated too. Therefore structural
  schemas work without importing or bundling any SDK/Effect implementation.
- This implementation uses dependency-free structural **Standard Schema v1**
  objects for input and output, inside a plain structural RPC definition.
  Validators require exact keys rather than depending on any host codec's
  unknown-property stripping policy. No Effect/SDK runtime or npm dependency is
  bundled. Local minimal `node:process` typings cover our only Node API.
- [`packages/core/src/rpc.ts:108-141`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/core/src/rpc.ts#L108-L141)
  validates method inputs before invoking handlers (`rpc.invalid_input`) and
  validates outputs afterward (`rpc.invalid_output`). Our handlers also check
  input when called directly in tests.
- [`packages/server/src/handlers/rpc.ts:8-23`](https://github.com/anomalyco/opencode/blob/v2.0.30/packages/server/src/handlers/rpc.ts#L8-L23)
  waits for activation, unwraps `payload.input`, and wraps success as `{ output }`.
  Validation errors go through the host's RPC error handling.

## Implementation choices and validation boundary

- Loopback matching validates the whole raw string, including a final-newline
  check (JavaScript `$` also matches before a final newline). No URL parser or
  normalization can admit alternate host/port spellings. Only a trailing slash
  is removed. IPv6 and localhost are preserved, not rewritten to IPv4.
- Empty version lists are valid and negotiate no common version; the list is
  bounded to eight positive integers. Phase 2 features are absent.
- Build tools come from the pinned Nix flake; the package derivation has no
  dependency fetchers. Source pin initially matches the available system
  nixpkgs revision but is independently locked here. No npm lockfile is needed
  because there are no npm dependencies or install step; `flake.lock` pins the
  entire toolchain.
- The parent owns isolated real-host validation. This lane does not start
  OpenCode or alter deployment/global configuration. Mock lifecycle tests model
  the host's await-setup-before-cleanup contract; the parent verifies it against
   the actual host.

## Local verification (2026-10-16)

From the plugin directory:

- `nix build "path:$PWD" -o dist` — passed; sandboxed derivation typechecks
  and bundles without npm fetch/install. Output contains only `package.json`
  and `index.js`.
- `nix develop "path:$PWD" -c tsc --noEmit` — passed.
- `nix develop "path:$PWD" -c node --test --test-reporter=tap` — 94 passed,
  zero failed/skipped, including source validators, strict-origin matrix,
  live environment sampling, no-fetch/no-env-write checks, registration and
  cleanup, late-registration teardown, fixtures, and built-artifact import.
- Inspected all generated import/require sites: the only external import is
  `import process from "node:process";`. No Effect/SDK/npm imports remain.
- Built output / `dist` target:
  `/nix/store/vdnk1vhpjr0chr2mrghaxmdrx5fq5lb6-opencode-windows-frontend-plugin-0.1.0`.
- No server was started. Real-host/auth/location validation remains with the
  parent. No design deviations; Standard Schema is the host-supported
  structural validator choice, and `flake.lock` is the sole dependency lock.
