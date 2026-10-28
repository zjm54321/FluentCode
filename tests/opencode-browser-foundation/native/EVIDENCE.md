# Accepted native run

Evidence: `artifacts/browser-native/run-005/results.json`, `build.txt`, `package-pins.json`,
and `obj/project.assets.json` in the current history checkout.

Executed only the dedicated fixture with:

```powershell
./tests/opencode-browser-foundation/native/run.ps1 -EvidenceRoot "$PWD/artifacts/browser-native/run-005"
```

- Actual x64 WinUI host, real source-linked production WebView2 adapter, zero build warnings/errors.
- Resolved SDKs match the application lock: WebView2 **1.0.3179.45**, WindowsAppSDK **1.8.260921001**,
  WinUI **1.8.260803003**. Evergreen browser runtime **156.0.4314.50**.
- Real 407 callback: URI `http://127.0.0.1:40700/`, challenge `Basic realm="OpenCode Browser Proxy"`.
  Production predicate matched and populated the response; the controlled proxy authenticated the retry.
- Origin 401 callback: URI `http://localhost:40701/origin401`, identical challenge/realm. The production response
  remained credential-free, and the origin never received the private proxy credentials.
- Nine controlled proxy requests, eight authenticated; eight origin requests, **zero direct-origin requests**.
  First localhost navigation reached the origin through the already configured proxy and eventually completed successfully.
- Real nested OOPIF: parent/child attachment chain plus `Runtime.consoleAPICalled` from both renderer sessions.
  Named and wildcard receivers retained the same non-null SessionId for each renderer, including the nested one.
- No usernames, passwords, Authorization headers or proxy option objects were logged. Only synthetic local origins
  were reachable through the fixture; external requests were rejected locally. No OpenCode server/session or user
  settings were used. No global configuration or software installation was performed.

Native sequencing finding: navigation 2 completed with `ValidProxyAuthenticationRequired`; the auth callback/retry
then produced navigation 3, which completed successfully. The final fixture waits for actual proxy-to-origin delivery
and successful terminal state. Earlier exploratory harness candidates stopped at the provisional auth completion;
their failed reports remain separate and are not the accepted result.

Peer/façade checks and the full application build/suite were not rerun. Production files were not changed for this task.

## Accepted production-runtime Lighthouse snapshot

Evidence: `artifacts/browser-native/audit-001/audit-results.json`, raw `lighthouse.json` and `lighthouse.html`.
The dedicated fixture was launched with the prepared `artifacts/browser-runtime` directory; production runtime,
stdio protocol, tunnel transport and native driver were source-linked without modifications. Only the remote
RPC endpoint was the controlled reserved-domain HTTP fixture, not a live OpenCode source.

- One completed real Lighthouse snapshot through `IOpenCodeBrowserRuntime.AuditAsync` and native WebView2 CDP.
- Accessibility score **0.52**; actual failures include `button-name`, `html-has-lang`, `image-alt` and `label`.
- Raw JSON report **115,766 bytes**; raw HTML report **274,263 bytes**.
- Before/after viewport **1424 × 710**, DPR **1**, scroll **(0,100)**; same URL/history, local/session storage,
  cookie and document sentinel. Navigation-start count **2 before / 2 after** (initial proxy-auth retry precedes audit).
- Second audit intentionally cancelled after owned-session attachment, at `Page.enable`; its owned session was
  detached successfully. This cancellation is the recorded expected `TaskCanceledException`, not a failed snapshot.
- Same proxy remained alive: a fresh native fetch returned `sidecar-proxy-still-alive` through one post-cancel
  RPC probe. Four actual sidecar tunnel opens/four controlled origin requests total; authorization headers at origin **0**.
- State remained unchanged after cancellation too. Native host run took **23.68 seconds**, with zero build warnings/errors.

The original proxy/auth/OOPIF fixture and peer/façade suites were not rerun for this added audit mode. No full
application build, production edits, user settings/session changes, global installs or commits were made.
