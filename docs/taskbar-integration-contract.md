# Taskbar integration contract

## Frozen consumer API

`AIFrontend.App` exposes the following synchronous methods, all returning
`AIFrontend.Services.Taskbar.TaskbarOperationResult`:

```csharp
InitializeTaskbar(IntPtr hwnd)
UpdateTaskbarTask(string taskId, TaskbarTaskState state)
UpdateTaskbarObservation(string taskId, bool isStale)
AcknowledgeTaskbarTask(string taskId)
ShutdownTaskbar()
```

- The App instance, HWND, initialization, updates, acknowledgements and shutdown must
  belong to the same Windows STA/UI thread. Consumers must marshal background events
  to that thread themselves. Wrong-thread/platform calls return a result without
  modifying the aggregate. Invalid empty/null IDs or undefined states are programmer
  errors (argument exceptions), not task execution failures.
- `taskId` is an opaque, case-sensitive, nonempty **execution ID**, with no content,
  filename, token, or prompt. Local/OpenCode adapters must namespace IDs if necessary.
  A session ID alone is not enough when executions overlap. Repeated events for one
  execution use the same ID; each subsequent execution gets a new ID.
- States: `Working`, `Pending` (explicit unresolved user action), `OutcomeUnknown`
  (explicit execution uncertainty), `Completed`, `Failed`, `Cancelled`.
- Only authoritative execution observations may publish outcomes. HTTP acceptance,
  stream silence, navigation, window activation, transport failure and stopping a
  local observer do not imply success, failure or confirmed cancellation.
- Disconnection calls `UpdateTaskbarObservation(id, true)` for affected known IDs.
  It only marks observation freshness: it does **not** clear pending, fail working,
  or fabricate `OutcomeUnknown`. An accepted execution update clears its stale flag;
  adapters may also explicitly report freshness using `false` (including for a latched
  terminal record, whose execution updates are ignored).
- Projection priority: `Pending` or `OutcomeUnknown` → NeedsAttention; otherwise any
  Working; otherwise unacknowledged Failed; otherwise unacknowledged Completed; else
  Idle. Cancelled never means success. No invented percentages are projected.
- Terminal observations are latched against late updates, including cancellation.
  Acknowledge a **specific** terminal execution only after explicit user confirmation.
  Working/Pending/OutcomeUnknown cannot be acknowledged away. There is no clear-all
  or activation reset. Acknowledging removes that ID; adapters must stop forwarding
  late events for an acknowledged execution (the store has no unbounded tombstones).
- Acknowledgement of Cancelled releases its retained record without showing success.
  State is in-memory for this App lifetime, not persisted.
- Results describe native projection, **not** task mutation or visible Shell pixels.
  Updates before initialization retain the aggregate and return NotInitialized.
  Projection failure never changes execution state. No retry loop or logging of IDs.

## App owner wiring (not connected by this lane)

1. After obtaining the real top-level window HWND on the UI thread, call
   `InitializeTaskbar(hwnd)` (e.g. using `WinRT.Interop.WindowNative.GetWindowHandle`).
2. Wire Local/OpenCode authoritative execution events to the API above. No speculative
   page/event names are required by this contract.
3. In the owning window's `Closed` handler call `ShutdownTaskbar()` on the UI thread.
   This only tears down native resources; it never acknowledges tasks. Do not wire
   activation to any task reset. If close handlers can outlive replacement windows,
   guard them so closing an old window does not shut down the new HWND's projection.
4. For a replacement HWND call `InitializeTaskbar(newHwnd)` on the same UI thread.
   The hook detaches and disposes the old private service before creating the new one.
   A detach failure retains the old hook/root and reports NativeError; retry teardown
   on its UI thread rather than abandoning an attached hook.

## Native ownership and recovery

`TaskbarWindowHook` exclusively reuses the existing `TaskbarStatusService.Apply`,
`Reapply`, and `Dispose`; no second COM drawing implementation was added. It validates
HWND/process/native-thread ownership and STA before attaching `SetWindowSubclass`.
A process-rooted fixed delegate plus per-attachment GCHandle roots the callback and
instance until successful removal or WM_NCDESTROY. `TaskbarButtonCreated` is registered
with `RegisterWindowMessageW`; receipt calls Reapply, recreating COM and resubmitting
the last complete snapshot after Explorer restarts. The first apply can fail before
the Shell button exists; the retained intent is reapplied when that message arrives.

Closed-driven disposal and WM_NCDESTROY fallback are idempotent. Removal failure does
not free a still-attached callback's instance; WM_NCDESTROY releases it regardless.
Callbacks contain managed exceptions and always attempt `DefSubclassProc`. There is
no background COM, finalizer teardown, polling, inferred outcomes or activation reset.
The hook/service do not destroy the HWND or shared system icons. Disposing does not
clear taskbar pixels; normal Closed destroys the HWND. A still-visible old HWND can
retain its last visual state until Windows removes it.

## Assigned headless validation

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/taskbar/run.ps1`.
The script uses the installed .NET 10 SDK/ref pack and direct Roslyn compilation:
no project edits, restore, install, GUI, real API, or full application build.

- Independently compile all production Taskbar sources and App.Taskbar.cs as an x64
  library, warnings as errors. The App partial stands alone: this is not WinUI startup
  or other-partial integration validation.
- Compile/run exactly three pure aggregate scenarios: concurrent-task priority;
  stale observations/unknown/cancellation; terminal latching/per-task acknowledgement.
  The test executable does not link or invoke native code.

Not runtime-validated here: actual overlays/progress, HWND lifecycle/subclass removal,
COM behavior, real STA/window dispatch, Explorer restart, WinUI wiring or real Local/
OpenCode event delivery. Parent owns the main application build and Windows GUI/Shell
verification after connecting the App lane.
