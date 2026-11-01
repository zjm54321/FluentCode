# Native terminal integration — 2026-10-30

> Current automated entry points are listed in the [critical suite](test-suite.md).

## Result

Persistent PTYs, foreground shell tools, background shell tools and agent-tree terminal entries use one `OpenCodePersistentPtyView` floating card and the same Windows Terminal native child-HWND renderer. No terminal WebView/xterm assets or old center-replacement terminal view remain. Browser, Mermaid and transcript WebViews are unrelated and unchanged by this replacement.

The existing PTY controller/transport/authentication/consent contracts remain in place. VT bytes, checkpoint acknowledgement, source/epoch fencing, reconnect, detach and takeover still cross the existing renderer adapter. Geometry is server-authoritative: layout/font changes cannot send remote resize; explicit **适应窗口** proposes dimensions and acknowledged resize recreates/replays the native parser at the approved grid. Selection copy uses non-destructive UIA TextPattern readback.

Shell output is read-only because the verified `/api/shell/{id}/output` source has no input endpoint. `OpenCodeShellTerminalAdapter` appends only new output, normalizes log LF to terminal CRLF and resets only when authoritative/retained output replaces a divergent snapshot. Existing byte-cursor pagination, 404 recorded-output fallback, status/exit/time metadata, page/source cancellation and background/foreground identity are retained. Closing destroys the HWND and cancels reads; it never kills a shell or PTY. Shell viewers fit their local read-only viewport without issuing backend resize calls.

Transcript shell tool rows and shell-message rows remain XAML with a three-line/320-character preview and **在终端中打开**. The conversation is not replaced and no native HWND is placed inside scrolling transcript content.

## Appearance

- Persisted `AppSettings.TerminalAppearance`: `FontFamily`, `FontSize`, `ColorScheme`, `CursorShape`, `CursorBlink`.
- Defaults: Cascadia Mono, 12 pt, theme-following scheme, blinking bar cursor. Font sizes are validated at 6–72.
- Palettes: Campbell, Campbell Powershell, One Half Dark/Light, Solarized Dark/Light, Tango Dark/Light, Vintage. Theme-following chooses Campbell in dark and One Half Light in light.
- Font, size, palette and bar/block/underline cursor settings apply live without replacing parser contents or the HWND. PTY grid authority is preserved.
- The same editor appears in Settings → 终端 and overlay More → 外观. Changes save immediately through `SettingsService`; settings UI contains labels/controls only.

## Native hosting / airspace

- The HWND is inset 12 DIPs from the terminal card edges. XAML owns rounded corners, border, shadow, toolbar and InfoBar rows outside the native rectangle.
- Permission/question/browser overlay visibility synchronously hides the HWND. Explicit dialog/menu leases, root popup intersection checks and pre-pointer/pre-key routing cover overlapping dialogs/flyouts.
- Layout, window move/size and DPI notifications realign or temporarily hide the native host until bounds settle. Focus tracking reports remain authorized while hidden; human input does not.
- Close/unload disposes the native control, message monitor, selection timer, XamlRoot hooks and airspace subscriptions.

## Files

- `FluentCode.sln`, `src/FluentCode/FluentCode.csproj`, `src/FluentCode/packages.lock.json`, `src/Directory.Build.props`: project references, reproducible dependency graph, third-party notices and isolated scratch outputs.
- `src/ThirdParty/WindowsTerminalControl/`: MIT source fork, provenance, native ABI fixes, child-HWND hosting, fixed grid/measurement, UIA readback and live `TerminalAppearance` palettes.
- `src/FluentCode/Pages/OpenCode/OpenCodePersistentPtyRenderer.cs`, `src/FluentCode/Pages/OpenCode/OpenCodePersistentPtyRendererAdapter.cs`, `src/FluentCode/Pages/OpenCode/OpenCodePersistentPtySession.cs`, `src/FluentCode/Pages/OpenCode/OpenCodePersistentPtyVtStream.cs`, `src/FluentCode/Pages/OpenCode/OpenCodePersistentPtyAirspace.cs`: native byte-consumption and airspace implementation.
- `src/FluentCode/Pages/OpenCode/OpenCodePersistentPtyPresentation.cs`, `src/FluentCode/Pages/OpenCode/OpenCodePersistentPtyView.cs`, `src/FluentCode/Pages/OpenCodePage.PersistentPty.cs`, `src/FluentCode/Pages/OpenCodePage.PersistentPtyOverlay.cs`: shared native card, lifecycle and toolbar.
- `src/FluentCode/Pages/OpenCode/OpenCodeShellTerminalState.cs`, `src/FluentCode/Pages/OpenCode/OpenCodeShellTerminalAdapter.cs`, `src/FluentCode/Pages/OpenCodePage.TerminalView.cs`, `src/FluentCode/Pages/OpenCodePage.xaml`: shared shell route and removal of `TerminalViewHost`.
- `src/FluentCode/Pages/OpenCode/OpenCodeToolPartView.cs`, `src/FluentCode/Pages/OpenCode/OpenCodeToolDetailResults.cs`, `src/FluentCode/Pages/OpenCode/OpenCodeToolDetailSurface.cs`, `src/FluentCode/Pages/OpenCode/OpenCodeToolRowPresentation.cs`, `src/FluentCode/Pages/OpenCode/OpenCodeConversationView.cs`: bounded transcript previews and unified foreground/background native-open actions. Concurrent non-terminal edits in these shared files were retained.
- `src/FluentCode/Models/TerminalAppearanceSettings.cs`, `src/FluentCode/Models/AppSettings.cs`, `src/FluentCode/Services/Configuration/SettingsService.cs`, `src/FluentCode/Pages/Settings/TerminalAppearanceEditor.cs`, `src/FluentCode/Pages/Settings/SettingsView.Terminal.cs`, `src/FluentCode/Pages/Settings/SettingsView.cs`, `src/FluentCode/Pages/OpenCodePage.TerminalAppearance.cs`: persistence, shared editor and live application. Concurrent tray settings were retained.
- Retained runners: `tests/opencode-persistent-pty/run.ps1` and `tests/opencode-persistent-pty-transport/run-backend.ps1`.

## Dependency wiring / size

`FluentCode.csproj` references the vendored WinUI project, which references the remote-only Core project. The WinUI source builds against WinAppSDK **1.8.260921001**, BuildTools **10.0.26100.9169**, CsWinRT **2.3.1**, WinUIEx **2.5.1** and **CI.Microsoft.Terminal.WinUI3.Unofficial 1.0.20-beta.1**. The library does not export a duplicate self-contained AppSDK payload; the executable owns deployment.

Upstream: **0x5bfa/WindowsTerminalControl 0.1.0**, commit **8a9d4e05484ee848098471404e312fbd4ebaee16**, MIT. The C# host is built from the vendored source; the underlying native terminal DLL is restored from the pinned NuGet package, not rebuilt. Native file version: **1.25.2603.03002**. Composition hosting is deliberately unavailable on AppSDK 1.8; child-HWND hosting is explicit, with no WebView or local-process fallback.

Vendored source/notices: **19 files, 93,434 bytes** (~91.2 KiB), excluding generated build artifacts. No binaries are committed. Scratch output confirms transitive copying of:

| Asset | Bytes |
| --- | ---: |
| Microsoft.Terminal.Control.dll | 1,676,832 |
| Microsoft.Terminal.WinUI3.dll | 230,400 |
| WindowsTerminalControl.Core.dll | 7,680 |
| WindowsTerminalControl.WinUI.dll | 653,312 |

## Remaining limits

- The native renderer comes from the pinned unofficial NuGet package; retain the upstream fork/ABI patches and notices when upgrading it.
- Child HWNDs cannot participate in XAML clipping/z-order. Insetting and temporary hiding are required behavior, not composition-mode hosting.
- Shell tools remain read-only until a verified backend input contract exists. No speculative input or PTY-control endpoint was added.
