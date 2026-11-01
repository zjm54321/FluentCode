# Browser Runtime Validation Suite

This test directory validates the isolated Node.js browser runtime, reverse network tunnel, and Chrome DevTools Protocol (CDP) Lighthouse audit bridge.

---

## Running the Tests

Use the prepared browser runtime bundle in `artifacts/browser-runtime/` (ensure `tools/browser-runtime/prepare.ps1` has been run first). Tests do not install Node, npm packages, or Chrome globally.

From Windows PowerShell:

```powershell
# 1. C# Host Integration Harness
# Compiles with .NET 10 (warnings as errors) and exercises C# sidecar callbacks,
# tunnel lifecycle, and CDP audit dispatch:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\browser-runtime\run.ps1

# 2. Node.js Tunnel & Proxy Protocol Tests
# Validates HTTP/CONNECT/WebSocket proxying, TCP half-close, backpressure,
# and loopback self-listener prevention:
& artifacts\browser-runtime\node.exe --test tests\browser-runtime\runtime.test.mjs

# 3. Node.js Lighthouse Audit Tests
# Validates real Chrome CDP sessions, flattened multi-target attachments,
# snapshot reports, and worker cancellation:
& artifacts\browser-runtime\node.exe --test tests\browser-runtime\audit.test.mjs
```

---

## Key Invariants Tested

- **Self-Proxy Prevention**: The local HTTP proxy rejects requests whose destination matches its own loopback host and port before opening a tunnel, preventing infinite loops.
- **Reverse Network Tunnel**: Remote web traffic (`localhost` and arbitrary hosts) is forwarded back through C# `tunnel.open` / `tunnel.read` / `tunnel.write` callbacks to the OpenCode server environment.
- **CDP Session Flattening**: Child targets (iframes, service workers) and the main page session are tracked and detached cleanly upon audit completion or worker cancellation.
- **Clean Worker Termination**: Audit jobs execute in disposable Node worker threads with strict execution time budgets (50 seconds) and bounded resource cleanup.
