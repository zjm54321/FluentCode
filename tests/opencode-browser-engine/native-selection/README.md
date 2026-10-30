# Browser selection / annotation native regression

Run from the history checkout, with existing Windows .NET/WinUI/WebView2 packages (offline restore only):

```powershell
./tests/opencode-browser-engine/run-selection-native.ps1 -EvidenceRoot "$PWD/artifacts/browser-selection-live/native-final"
```

The dedicated WinUI fixture source-links the production overlay, content host, selection controller,
selected-item list, picker script, viewport snapshot helper, frame registry and geometry. WebView2 CDP,
native pointer inspection, region input, native controls and PNG decoding are real. The surrounding
workspace, ref registrar and composer transport are fixture adapters; no live OpenCode source, user
settings, account, remote server, runtime sidecar or production app process is used. Preview and external
Chrome surfaces are inert fixture adapters here, not evidence about those engines' presentation.

Checks include:

- Real native reproduction of `DOM.querySelector {nodeId:0,selector:"#target"}` (and iframe `#child`),
  with the full `ArgumentException`, HRESULT `0x80070057`, and safe parameters in `expected-legacy-error.json`.
- Repaired production controller queries each frame's remote document object rather than using the
  uninitialized DOM frontend node ID; root and same-process iframe inspect/region selection work.
- Oversized, partly offscreen body selection gets a real bounded visible crop, not an invalid screenshot clip.
- Actual FlyoutPresenter fills the content band with a 12-DIP edge gap, one toolbar row, and no second title row.
  Smoke covers the full band. A native WinUI close button is invoked through its automation peer and reopened.
- Real region drag at scroll Y=410; opening/closing the native note panel preserves the same CSS viewport,
  target rectangle, and scroll. Another 73-pixel scroll keeps the document-anchored outline on the target.
- Real note entry and the production attachment path preserve the original ref, image, note, captured viewport,
  and document-region metadata. The remote ref registrar/composer are intentionally fixture adapters.
- Colored open icon retains the existing native Segoe Fluent E774 globe glyph and 18-DIP dimensions,
  rather than switching to a Chrome brand mark or a different globe outline.

`native-region-panel-open.png` / `native-region-panel-closed.png` use **PrintWindow on the fixture's own HWND only**.
There is no desktop/screen-copy path. If accelerated content cannot be captured, the fixture explicitly records
that limitation and writes a separate WinUI-only RenderTargetBitmap image (not claimed to contain WebView pixels).
`browser-region-after-scroll.png` is a real CDP screenshot of the controlled page, and `posted-annotations.json`
contains only synthetic fixture labels/metadata (image lengths, not base64 blobs).

For the actual Chromium CDP argument contract (also relevant to the separate Chrome engine):

```sh
node tests/opencode-browser-engine/selection-live.mjs artifacts/browser-selection-live/chrome-final
```

This launches only its own ephemeral Chrome/profile and loopback HTML server. `results.json` records the expected
legacy `-32000: Could not find node with given id` failures for root/iframe, successful remote-document queries,
viewport-preserving ordinary crops, and scroll-aligned outlines. It does not claim managed/attached Chrome-window
UI coverage, cross-process iframe coverage, or an application-wide integration run.

The pure runner remains `run.ps1`; its added viewport/attachment checks supplement, rather than replace,
the native and Chromium runs. No main application build, live backend restart, settings change or global install.
