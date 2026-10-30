# AIFrontend remote-terminal fork

Upstream **WindowsTerminalControl 0.1.0**, commit **8a9d4e05484ee848098471404e312fbd4ebaee16**, MIT. See `UPSTREAM`, `LICENSE`, `NOTICE.md`. Core/WinUI sources are vendored; the native renderer is restored from **CI.Microsoft.Terminal.WinUI3.Unofficial 1.0.20-beta.1** (native file version **1.25.2603.03002**), not rebuilt.

Changes:

- Retarget **WinAppSDK 2.4.1-experimental → 1.8.260921001**, BuildTools **10.0.28000.2270 → 10.0.26100.9169**, minimum OS declaration 17763 to match the app. Child-HWND hosting is explicit. Unavailable `ContentExternalOutputLink` code is guarded by `HAS_CONTENT_EXTERNAL_OUTPUT_LINK` (undefined); composition requests fail, never silently fall back.
- Fix native ABI: remove the nonexistent `SelectionBackgroundAlpha` float (**84 → 80-byte** `TerminalTheme`); set **CharSet.Unicode** for `TerminalSendCharEvent` (previously corrupting CJK input).
- Add authoritative `FixedSize`, non-mutating `MeasureViewport` via existing native resize/measurement exports, and coordinated native/XAML focus. `TerminalAppearance` live-applies font, the Windows Terminal built-in palettes, cursor shape/blink; **Cascadia Mono 12** defaults to **Campbell** in dark and **One Half Light** in light theme.
- Add `SetHostVisible`, native HWND/selection/focus accessors for the application's airspace lease. Unsubscribe the retained XamlRoot on disposal, including after unload.
- Add non-destructive, bounded **UI Automation TextPattern** selection/document readback on MTA workers. It uses the Windows SDK's stable COM ABI; no WPF/framework dependency or native binary patch is required.
- Exclude unused `ConPtySession.cs` and its ConPTY package from the remote-only Core build. The original source remains for provenance; no local process/backend is shipped or created.
- The executable owns self-contained Windows App SDK deployment. The library uses `WindowsAppSDKSelfContained=false` to avoid duplicate `MsixContent` publish assets. Native/runtime assets remain normal transitive ProjectReference/NuGet runtime assets; no manual DLL registration.
- Import the opt-in `AifScratchRoot` per-project build paths from `src/Directory.Build.props`. Normal Debug/Release builds retain their usual output paths.

Integration/airspace/UTF-8/consent logic lives in `src/AIFrontend/Pages/OpenCode/OpenCodePersistentPty*.cs`, outside upstream code. Tests link those production files in `tests/opencode-pty-right-panel-visual/`. Do not replace the native DLL with XamlToolkit's identically named DLL: their ABIs differ.
