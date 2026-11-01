# FluentCode shell contract

The WinUI shell is implemented by `src/FluentCode/MainWindow.xaml` and its
`MainWindow.*.cs` partials. Visual layout belongs to the shell implementation;
this document describes ownership and navigation rather than fixed dimensions.

## Application surface

- OpenCode is the sole conversation/work surface.
- Settings, account/connection and task status remain application services.
- The window title identifies FluentCode and the active session when available.
- Application/window/tray icons use `Assets/AppIcon/app.ico`.
- Settings and secrets are shared through `AppServices`, independently of page
  lifetime. Pages do not own application-level settings containers.

## Navigation and lifetime

- Settings navigation respects dirty-state Save/Discard/Stay decisions.
- Subscribe to a page's events while it is owned and detach on replacement or
  final shutdown. Late results must verify the active page/source/session.
- Account/connection actions use the application's OpenCode source. Task status
  reflects observed OpenCode execution, not inferred idle/transport events.
- Notification navigation uses trusted structured targets and never sends a
  prompt, retries a request or approves an interaction automatically.
- Background closing and final application shutdown have distinct ownership;
  native hooks and application-owned resources are released on final shutdown.

## Related contracts

- [Application integration](app-integration-contract.md)
- [Settings](settings-ui-contract.md)
- [OpenCode UI](opencode-ui-contract.md)
- [Taskbar integration](taskbar-integration-contract.md)
- [Theme refresh](theme-refresh.md)
