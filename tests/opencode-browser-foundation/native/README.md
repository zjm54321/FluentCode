# Real WinUI/WebView2 proxy fixture

This is a separate, unpackaged, self-contained-Windows-App-SDK UI host. It source-links the **actual production
WebView2 driver** and opens a temporary native WinUI window. The default proxy/auth mode uses two ephemeral
loopback TCP listeners, with no UI/WebView2/transport stand-ins or runtime sidecar. Neither mode uses an OpenCode
server, live application settings or user session.

From Windows PowerShell:

```powershell
./tests/opencode-browser-foundation/native/run.ps1 -EvidenceRoot "$PWD/artifacts/browser-native/run-001"
```

The script validates package pins against the main `packages.lock.json`, including **WebView2 SDK 1.0.3179.45**,
then restores from the existing NuGet cache only (empty source feed), builds only this fixture, and starts its
real x64 WinUI host. All build/profile/evidence files go below EvidenceRoot. No software installation or global
configuration change is attempted. A Windows interactive desktop and existing Evergreen WebView2 runtime are
required. Reuse a fresh evidence/profile directory for each run. The process tree is bounded to 120 seconds.

Required evidence in `results.json`:

- Actual native `BasicAuthenticationRequested` URI/challenge for HTTP 407; production handler supplies the random
  private credentials, and the controlled proxy verifies the resulting authentication.
- First `localhost` navigation reaches the controlled origin through that authenticated proxy, never directly.
- A distinct origin's HTTP 401 uses the **same realm** but receives no proxy credentials in either native event
  response or origin request. The fixture cancels this challenge so no credential dialog/user input is involved.
- Named/wildcard CDP receivers are optionally exercised with cross-site child/nested iframes. An unsupported
  native Target/session route or an absent OOPIF is recorded as an explicit boundary, not a synthetic pass.

Only authentication URI/challenge strings are recorded; never usernames, passwords, Authorization headers or
proxy option objects. Requests outside the synthetic origin are rejected locally; the proxy cannot connect to
the Internet. The temporary profile is retained inside the evidence directory for bounded cleanup by its owner
after the fixture/browser exits. `restore.txt`, `build.txt`, and `host-error.txt` identify an actual missing host
prerequisite if native startup fails; an unsuccessful host launch does **not** establish any native auth result.

Authentication observations use a native deferral and a queued read after event dispatch, because subscriber
ordering is not the assertion boundary. A first native NavigationCompleted can report
`ValidProxyAuthenticationRequired` before the authentication callback/retry; the fixture waits for actual proxied
origin delivery **and** the eventual successful terminal page state, rather than treating that provisional event
as final. Safe diagnostics record booleans/counters and navigation/session IDs, never credential values.

## Real production-runtime Lighthouse mode

```powershell
./tests/opencode-browser-foundation/native/run.ps1 `
  -EvidenceRoot "$PWD/artifacts/browser-native/audit-001" `
  -AuditRuntimeDirectory "$PWD/artifacts/browser-runtime"
```

Use a new evidence directory when rerunning. This mode skips the already-proven proxy/auth/OOPIF fixture and
source-links the sealed production runtime factory, protocol, runtime and tunnel transport. It starts the prepared
Node sidecar at the supplied directory, obtains its actual proxy settings and full CDP event catalogue, and creates
the real native WebView2 driver with those settings. It does not copy, install or change the prepared runtime.

Only the remote RPC endpoint is controlled: `AuditTunnelRpc` serves a fixed HTTP page for the reserved
`native-audit.invalid:80` target via real sidecar stdio tunnel callbacks. It has no DNS/socket-connect route and
rejects other targets, so the browser cannot load the page through direct networking or a live user server.

The test invokes production `IOpenCodeBrowserRuntime.AuditAsync` with a transparent recording CDP connection
forwarding to the actual native driver. It requires real `lighthouse.json` and `lighthouse.html`, a non-perfect
accessibility score and the known `button-name` failure. It compares URL/history, viewport dimensions/DPR/scroll,
localStorage/sessionStorage/cookie and a document sentinel before/after, plus actual navigation-start counts.

After that **one completed snapshot**, it starts one more audit solely to cancel it once an owned CDP session
exists. A subsequent native `fetch` must use the same live sidecar proxy and reach the controlled RPC origin.
`audit-results.json` preserves report sizes/scores, before/after state, cancellation/proxy proof, and exact CDP
method/session/error traces. The only failed CDP command in the accepted run is the deliberately cancelled
second audit's `Page.enable`, not an error in the completed snapshot.
