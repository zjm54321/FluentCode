# External Chrome driver checks

```powershell
./tests/opencode-browser-chrome/run.ps1 -EvidenceRoot <absolute-workspace-evidence-directory>
```

Use `-DownloadProvenanceOnly` with a new evidence directory to compile the current Chrome source and run only the immutable download URL/after-navigation regression, retaining prior evidence for unchanged driver behavior. `-DownloadChildProvenanceOnly` is the exact repaired cross-origin iframe regression: it checks creation-time provenance, retained bytes, and immutable source URL after navigation.

`-DownloadRoutingOnly` runs the additional same-process/nested-OOPIF routing fixtures, busy-UI receive-order case, real subtree-removal cleanup, root sibling-tab ownership, and unowned user-fixture tab rejection. A test-only read-only transport observer records `download-routing.jsonl`; it does not change interception or download behavior. These routing checks also run as part of the default Chrome harness. No real user profile or existing user tab is touched.

Requires installed Windows Chrome, .NET 10, and the app's restored WinUI/Windows SDK reference packages. No downloads, installs, user-profile edits, app builds or full suites occur. Each run launches real installed Chrome against fresh disposable workspace profiles and a controlled authenticated loopback HTTP/CONNECT fixture. TLS trust is bypassed through CDP only in fixture pages; no certificate is installed.

The runner first compiles all production Chrome source against real WinUI references (the unused attachment type is a host stub). Runtime tests replace only WinUI activation types and use a single-thread synchronization-context pump; Chrome/CDP/network/storage/downloads remain real. `production-compile.txt`, `compile.txt`, `results.txt`, `events.jsonl`, `viewport.png`, and retained download bytes are written into the evidence directory. Fixture profiles remain there for diagnosis; every owned process/context is closed on exit.

Composition:

```csharp
new OpenCodeBrowserChromeDriverFactory(new OpenCodeBrowserChromeOptions(
    ExecutablePath: null, Attach: false, Endpoint: null))
```

Default options launch installed Chrome with the workspace's dedicated profile and authenticated proxy. Attach accepts only loopback browser-level HTTP(S)/WS(S) endpoints, rejects discovery and handshake redirects, and creates a private context rather than adopting existing tabs. Attached Chrome must already use `--enable-automation --disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp`; CDP cannot install these process-wide network policies per private context. Attach checks them before creating targets and never sends `Browser.close` or kills that process.
