# Offline session UI production bridge host

Run on Windows with the existing .NET 10 SDK/reference packs and existing WinAppSDK NuGet references:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/opencode-session-ui-bridge/run.ps1
```

No restore, installation, live endpoint, credentials, model, GUI, or test framework. All HTTP requests terminate in an injected `HttpMessageHandler` for `offline.invalid`; unexpected paths fail. This does not execute neighboring test suites or build the application tree. Compiler warnings are errors, except existing-reference assembly unification warnings 1701/1702, matching the observations thin host.

## Production linkage

- Entire real Notifications/OpenCode service files, including Monitor Execution, projection, gate, page source, frozen UI store and DTOs.
- Entire real `App.OpenCode.SessionState.cs`, `App.OpenCode.Observations.cs`, and `App.OpenCode.Diagnostics.cs`.
- Verbatim extraction from `App.OpenCode.cs`: current-source getter, Track/TrackCore, Release, RequireAppThread/PostToApp/RaiseAppEvent, and Configure's dispatcher binding body. Configuration assignment is a controlled host input; no credential/configuration/network construction executes.
- Real taskbar aggregator/status files; only native rendering is replaced.
- `Monitor.TestHost.cs` only supplies StreamState inputs and invokes real ExecutionAsync/helper. No producer/adapter mapping is reproduced in hand-written host code.
- Exact-time boundary probe extracts the entire real UI-fact block, replacing **only** its single `UtcNow` binding with an explicit `now` argument. Raw projection still comes from real Evaluate. This supplemental probe does not replace the full production ExecutionAsync compiled and exercised by other cases.

Generated extracts, compiler response file, SHA256 source manifest, and transcript are retained in `artifacts/run-*/` so the tested source and instrumentation are inspectable.

## Three case groups

1. Active/history Working plus pending with different evidence times; real timer independent expiry; Idle+Pending and root Idle with unresolved descendants; protected Working; strict helper/extracted boundary cases; missing evidence; legacy observations do not infer new UI fields; legacy taskbar/outcome/evidence behavior.
2. Old overall observation with fresh Info; stale/future/missing/failed/mismatched Info; invalid viewed values; completion freshness, same-observation atomic watermark, original six-way evidence, rejected cross-turn/negative/future dependencies; exact covered ACK through real App/Monitor/client, null/error handling, newer completion and gate protection preservation.
3. Busy first seed, synchronous gate sampling, no delayed false write, blocked dispatcher with same-endpoint owner/revision replacement, queued old monitor/ACK/release invalidation, stale getters, immutable snapshots, reset/closing, at-most-one publication per callback, events outside App lock and callback-owned gate lease.

## Validation status (2026-10-09)

The initial Windows invocation reached the warnings-as-errors compiler but failed before executing cases:

- CS0136: extracted boundary wrapper parameter `historyAt` collided with a local in the verbatim producer block.
- CS0649: unused optional host field `Fixture.BeforeActive` was never assigned.

Both host-only issues were corrected (`historyTimestamp` wrapper parameter; unused field removed). The failed attempt's original source hashes/log remain at `artifacts/run-54381b246038462ca08ad16f1afbdd3d/`.

The parent's subsequent run passed groups 1 and 2 but exposed an incomplete test lifecycle in group 3: replacement skipped the real configuration-request reset, leaving revision 1 in `_sessionObservationTimes`; the timer therefore skipped sampling the revision 2 owner. The replacement scenario now calls `old.App.Reset()` (the real `ResetOpenCodeObservations`) after old callbacks/ACK are queued and the snapshot is captured, before Release and new binding. No old queue is drained early, no protection value is forced, and all prior assertions remain. Configuration construction is still stubbed, but its production reset path now executes in the correct order.

An authorized Windows rerun passed warnings-as-errors compilation and **236 assertions: 81 + 118 + 37**. Successful transcript, compiler inputs/generated extracts and source SHA256 manifest are retained at `artifacts/run-e93f7172ef9540199bb684cd36e96eed/`. No production files were modified and no production defect was established by these failures.

Actual boundaries: no real WinUI dispatcher/thread-affinity testing, credential refresh/configuration construction, native notification/taskbar rendering, or exhaustive scheduler races. Exact active-age equality is exercised by the explicit-clock extracted probe, not by trying to land the real wall clock on a single tick. Event assertions record failures because production event delivery intentionally swallows subscriber exceptions.
