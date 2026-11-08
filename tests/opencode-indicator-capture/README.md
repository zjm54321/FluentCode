# Indicator v3 native capture (test-only)

Run from the indicator lane after `scripts/build-debug.ps1` publishes the candidate:

```bash
bash tests/opencode-indicator-capture/capture.sh \
  artifacts/<debug-evidence>/publish \
  artifacts/indicator-v3-capture-<UTC>
```

- `capture.sh` owns `/tmp/opencode/capture.lock`, records its owner, retries every 15 s for at most 20 min, and removes only its own lock on exit.
- `capture.ps1` checks an Active `quser` session and zero RichEditPrototype processes before its single launch. The disposable `FC_STARTUP_SMOKE_ROOT` isolates settings, instance identity, logs and profiles from installed FluentCode.
- All three gates are required: `FC_INDICATOR_CAPTURE_TEST=1`, `FC_SHELL_CAPTURE_TEST=1`, and the disposable startup-smoke root. Indicator capture skips OpenCode ownership entirely and rejects a page with a source/client. No server or desktop input is used.
- The unrelated composer is collapsed before its first template measure in this header-only fixture; ordinary startup and RichEdit implementation are unchanged.
- One launch renders the real window/tab controls with RenderTargetBitmap in Light/Dark at 1920/1024 window widths. Wide: 9 sample session tabs. Narrow: 8 (long-title working replaces the duplicate short working tab). The real 48 DIP Home item remains.
- `tabs-<width>-<theme>.png`: original native frame. Matching JSON: actual width, XAML/raster scale, session count, per-tab/status/title geometry and ItemStatus.
- `tabs-<width>-<theme>-headers-3x.png`: nearest-neighbour 3× copies of original header pixels, not reconstructed UI.
- `review-<width>-<theme>.png`: overview plus 3× atlas, for the bounded four-image review.
- The runner validates the ready marker's PID and closes only its own Process object/PID with WM_CLOSE, then exact-PID Kill only if necessary. It never searches for or stops installed FluentCode.

Keep launches at most three per assignment; inspect at most four downscaled new capture PNGs. The capture hook does not alter ordinary startup or production state outside the explicit disposable gates.
