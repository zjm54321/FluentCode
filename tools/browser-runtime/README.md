# Offline-deployed browser runtime

This folder owns packaging, not application startup. The app must use its local
`BrowserRuntime/node.exe`; it must never run npm, install a runtime, download a
browser, or fall back to Node on PATH. Proxy credentials remain in C#.

## Prepare (Windows PowerShell 5.1 or newer, Windows x64)

From the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/browser-runtime/prepare.ps1
```

Preparation downloads the official Node **24.22.1** Windows x64 archive, checks
the pinned archive and executable SHA-256 values against the official version's
`SHASUMS256.txt`, then invokes only that archive's `node.exe` and npm CLI.
`-NodeArchivePath C:\path\node-v24.22.1-win-x64.zip` reuses an existing archive
after the same checks. Node's official checksum listing is still fetched.

`npm ci` uses the tracked lock, production dependencies only, disabled lifecycle
scripts and bin links, strict engines, explicit npmjs registry, and separate
temporary user/global npmrc, prefix, cache and temp paths. User npmrc, user npm
configuration environment variables, NODE_OPTIONS/NODE_PATH and global installs
are not used; PATH is never changed. Preparation requires network access. The
resulting application runtime does not require a subsequent package install.

The checked-in `licenses/Node-LICENSE.txt` and `licenses/Lighthouse-LICENSE.txt`
retain the exact upstream license texts for these pins. Preparation also retains
the licenses directly from the verified distribution and locked npm payload.

Use `-UpdateLock` only to intentionally regenerate `package-lock.json`; review and
retain the resulting lock. Ordinary preparation never rewrites it.

## Output and deployment contract

`artifacts/browser-runtime/` is ignored already by the repository's `artifacts/`
rule. It is replaced only after preparation verifies the candidate. It contains:

- `node.exe`, `package.json`, `package-lock.json`, `runtime-versions.json`;
- **all** production `node_modules/` files, not a JavaScript-only subset;
- every file from this folder's `runtime/`, copied to the output root;
- `licenses/`, `THIRD-PARTY-NOTICES.md`, `third-party-packages.json`;
- official `SHASUMS256.txt` and verified `runtime-manifest.json` provenance.

The sidecar owner can add its entry point and supporting files under `runtime/`.
Run preparation again after changing runtime source. Packaging currently verifies
Node/Lighthouse and required assets; actual callback integration and snapshot
audits are validated by the runtime owner.

The csproj owner should copy `../../artifacts/browser-runtime/**/*` (relative to
`src/FluentCode/FluentCode.csproj`) to `BrowserRuntime/%(RecursiveDir)%(Filename)%(Extension)`
with both `CopyToOutputDirectory` and `CopyToPublishDirectory` set to
`PreserveNewest`, including hidden files. Missing `node.exe`,
`node_modules/lighthouse/package.json`, `runtime-manifest.json`, or the eventual
sidecar entry point must fail build/publish clearly and direct the developer to
the preparation command. Do not trigger preparation automatically from MSBuild
or application startup. Do not include the temporary preparation directories.

## Focused offline verification

```powershell
& artifacts/browser-runtime/node.exe artifacts/browser-runtime/verify-runtime.mjs
```

This runs the **packaged Windows executable**, checks exact versions, executable
hash and lock hash, checks each installed package against its production lock
entry and registry integrity metadata, imports Lighthouse/snapshot/startFlow,
and checks report, gatherer, localization, axe and license assets. `npm ci`
verifies tarball content against every lock integrity value during preparation.
The standalone verifier makes no network request and launches no browser.

Do not commit `node.exe`, `node_modules`, npm caches, downloaded archives or the
generated bundle. The tracked manifests, lock, scripts and notices are sufficient
to regenerate the artifact. npm itself is a preparation tool and is not deployed.
