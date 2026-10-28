# Focused browser runtime validation

Use the prepared Windows bundle; tests do not install Node, npm packages or Chrome.

```powershell
# Source-linked .NET 10 compile (warnings as errors), real sidecar callbacks,
# public factory, stable authenticated proxy and real Chrome CDP audits:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/browser-runtime/run.ps1

# HTTP/CONNECT/websocket, half-close, immutable leases, failed runtime,
# no direct fallback, worker cancellation and 31-second idle SSE:
& artifacts/browser-runtime/node.exe --test tests/browser-runtime/runtime.test.mjs

# Real Lighthouse snapshot via Node callback bridge, including child renderers:
& artifacts/browser-runtime/node.exe --test tests/browser-runtime/audit.test.mjs
```

Chrome defaults to `C:\Program Files\Google\Chrome\Application\chrome.exe`;
`BROWSER_RUNTIME_TEST_CHROME` may select an existing controlled executable.
Unavailable Chrome is explicitly reported as skipped, not replaced with a fake
audit. Profiles and compile output stay under ignored `artifacts/`.

The C# harness links the frozen execution/lease contracts and all runtime
implementation files, without WinUI. It mirrors only the two serialization-neutral
tunnel records defined beside the application-specific RPC extensions; tunnel
callbacks themselves are deliberately controllable test doubles. The public
`AuditAsync` tests use actual Chrome flattened CDP and genuine Lighthouse reports.
They cover cancellation after session creation, immediate audit reuse, a real CDP
attachment reply delayed past cancellation, subscription cleanup, and working
proxy traffic after cancellation. The driver owns UI-thread dispatch; native
WebView2/composition checks and combined build/publish remain the parent's scope.

The self-listener test rejects HTTP/subresource, canonicalized IPv4, CONNECT and
websocket destinations identifying the proxy's own exact host/port before any
tunnel callback. It also proves that other remote IPv4/IPv6/localhost destinations
remain routed through tunnel callbacks, including a different host at the same port.
