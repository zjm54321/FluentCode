# Offline Browser Runtime Tooling

This directory contains preparation scripts and manifests for the isolated Node.js browser runtime bundled with FluentCode.

---

## 1. Overview & Architecture

FluentCode includes an embedded Node.js sidecar process to handle session browser tunnels and Lighthouse performance audits. 

- **Local Execution**: The application launches its bundled `BrowserRuntime\node.exe` directly.
- **No System Impact**: The application never invokes global `npm`, never downloads packages dynamically at runtime, and never touches system environment variables (`PATH`, `NODE_OPTIONS`, or `NODE_PATH`).
- **Network Boundaries**: Node.js and its dependencies are fetched exclusively during the explicit preparation step. Normal application builds and launches are fully offline.

---

## 2. Preparing the Runtime

From the repository root in Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\browser-runtime\prepare.ps1
```

### What Preparation Does
1. **Download & Checksum Verification**: Downloads the official Node **26.13.0** Windows x64 distribution archive and verifies its SHA-256 hash against the official `SHASUMS256.txt`.
2. **Deterministic Dependency Installation**: Uses the extracted Node executable and locked `package-lock.json` to execute `npm ci` with:
   - Production dependencies only (`--omit=dev`).
   - Lifecycle scripts disabled (`--ignore-scripts`).
   - Isolated temporary cache and scratch directories.
3. **Artifact Generation**: Outputs the verified bundle into `artifacts/browser-runtime/`:
   - `node.exe`, `package.json`, `package-lock.json`, and `runtime-manifest.json`
   - Production `node_modules/` (including Lighthouse and Chrome DevTools Protocol libraries)
   - Runtime scripts from `runtime/` (including `sidecar.mjs`)
   - Upstream licenses and third-party notices

---

## 3. Deployment Contract

During application build and publish (`scripts/build-debug.ps1` or `scripts/publish-release.ps1`), MSBuild copies `artifacts/browser-runtime/` into the output folder as `BrowserRuntime/`.

- If `artifacts/browser-runtime/` is missing, the build fails immediately and directs the developer to run `prepare.ps1`.
- The build scripts verify that the SHA-256 hashes of `node.exe`, `sidecar.mjs`, and `runtime-manifest.json` in the publish directory match the prepared artifacts.

---

## 4. Offline Verification

To verify the integrity of the generated bundle without launching the main application:

```powershell
& artifacts\browser-runtime\node.exe artifacts\browser-runtime\verify-runtime.mjs
```

This runs an offline sanity check using the packaged Windows executable, verifying installed package integrity, file hashes, and module imports (Lighthouse, axe-core, and protocol schemas) without network requests.
