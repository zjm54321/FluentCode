# Focused persistent PTY backend checks

`run-backend.ps1 -EvidenceRoot <directory>` uses the installed .NET 10 compiler/reference pack, warnings as errors, without restore or a main application build. It links the real HTTP transport, native WebSocket connection, renderer-aware controller and revocable page source. `BackendSupport.cs` substitutes only unrelated desktop-bridge storage; no PTY network/controller behavior is substituted.

Source-bound origin/controller/renderer contracts live in `src/AIFrontend/Services/OpenCode/PersistentPty/`, explicitly included by this focused runner and recursively included by the main SDK's default compile glob. Root `Services/OpenCode/*.cs` retains only source-free wire DTOs/HTTP/socket/connection contracts, so flat transport harnesses do not acquire page lifecycle dependencies.

The runner also compiles a source-free wire-only library with `OpenCodePageSource`, PTY lifecycle/controller contracts and `BackendSupport.cs` omitted. Its response file and log are `wire-only.rsp` and `wire-only-compile.log` in the evidence directory.

Use a fresh evidence directory per candidate. Compiler logs explicitly record exit code/PASS even when csc succeeds silently. `candidate-inputs.json` records exact compiler source paths, saved input snapshots/SHA-256, compiler/ref-pack identity and both response hashes. `candidate-results.json` records compiler/test exits, unchanged-source verification and assembly hashes. Old failed compile logs must be retained as failed evidence, not cited as later successful builds.

`BackendTests.cs` covers authenticated prefix-preserving server-scoped HTTP, nullable capabilities, exact IDs/envelopes/base64 checkpoints/ticket POST, redirects and redaction, real fragmented WebSocket messages and split UTF-8, replay renderer-consumption barriers, canonical resize/checkpoint ordering, passive observers, explicit control, automatic promotion without consent, controller-loss cancellation of queued input/resize, fresh reconnect without write replay, bounded-queue overflow and source revocation. It also proves unsupported canonical grids at snapshot/attached/live-resize boundaries become terminal `Unavailable` (`unsupported-grid`) without coercion or a reconnect loop, while exact supported boundary sizes 2×1 and 1000×1000 remain usable.

Assigned-controller consent cases cover both `attached(role=controller)` and observer→automatic promotion: assignment and renderer readiness never imply consent; input is denied until an explicit control request. For a matching already-owned identity and unchanged canonical size, successful native control send arms input without another role event/application ACK. Pre-dispatch cancellation, role loss, queued-control cancellation and later re-promotion remain disarmed; an explicit fresh enable can arm the still-assigned controller again.

Fake-renderer resource regressions verify exact authoritative checkpoint bytes on `ResetAsync` (an 8 MiB + 1 byte snapshot) and `ResizeAsync` (an ordered socket checkpoint rejected by the fake renderer). Throwing `PersistentPtyException("unsupported-renderer-state")` ends the run as disarmed `Unavailable`, with one bootstrap, no extra attachment/control/input, proper detach and idempotent controller disposal. The UI adapter owns ≤1 MiB output chunking/ACK order and >8 MiB checkpoint rejection before WebView creation; these tests change no wire limits or native UI code.

For an actual installed OpenCode/native PTY pair, first compile the backend harness, then run:

```text
node real-fixture.mjs <opencode binary> <opencode-pty binary> <Windows dotnet.exe> <Windows assembly path> <evidence directory>
```

The launcher creates a fresh `/tmp/opencode/aif-phase2-pty-real-*` HOME/config/cache/data/state/runtime/DB and empty cwd, starts its own server on port 0, and invokes the production C# HTTP/WebSocket/controller harness against it. The test creates one `/bin/sh`/`cat` terminal, opens two passive observers, explicitly transfers control twice, observes output in both, verifies auto-promotion is disarmed, proves detach leaves the process alive, validates retained raw-byte replay, then removes only its terminal. The launcher terminates only its new process group. No user service, terminal, configuration, credentials or database is used. Missing native binaries must be reported as a skipped real-PTY test, not a simulated success.

UI renderer tests remain untouched in the separate `tests/opencode-persistent-pty/` directory. These backend checks do not open a WinUI window or perform combined application acceptance.
