# Persistent PTY Backend Validation

This harness tests the low-level transport, WebSocket protocol, and controller state machine for persistent interactive PTY sessions.

---

## Running the Test

From Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\opencode-persistent-pty-transport\run-backend.ps1 -EvidenceRoot artifacts\pty-evidence
```

The runner links production sources from `src/FluentCode/Services/OpenCode/PersistentPty/` and executes headless integration tests against simulated WebSocket endpoints using .NET 10.

---

## Tested Capabilities & Invariants

- **Binary WebSocket Streaming**: Validates streaming of VT100 control sequences, fragmented WebSocket frames, split UTF-8 multi-byte sequences, and bounded output queuing.
- **Server-Authoritative Grid Geometry**: Tests that terminal window resizing follows server confirmation. Valid boundaries (e.g. 2×1 to 1000×1000) are accepted, while unsupported grid sizes transition to terminal `Unavailable` without infinite retry loops.
- **Controller Consent & Takeover**:
  - Connection starts in passive observer mode; keyboard input is rejected until explicit user takeover.
  - Verifies that successful native takeover commands transition the session to controller mode, while connection drops or role revocations disarm input immediately.
- **Checkpoint Replay & Memory Bounds**: Verifies state snapshots during reconnect, 8 MiB checkpoint thresholds, and clean controller disposal.

For an overview of the full test suite, see [docs/testing.md](../../docs/testing.md).
