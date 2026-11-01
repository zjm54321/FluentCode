# FluentCode application integration

The application owns the window, settings/secrets, OpenCode source, notification
service and taskbar integration. Page and visual-layout contracts are documented
in [shell](shell-ui-contract.md), [settings](settings-ui-contract.md) and
[OpenCode UI](opencode-ui-contract.md).

## Settings and credentials

- `src/FluentCode/Services/Configuration/AppServices.cs` owns the shared
  `SettingsService` and `DpapiSecretStore`; pages do not create competing stores.
- The application uses `%LOCALAPPDATA%\FluentCode` only. It does not discover,
  copy, merge or import another application's data. Native smoke tests use an
  explicit disposable `FC_STARTUP_SMOKE_ROOT` instead.
- OpenCode credentials remain Windows DPAPI CurrentUser ciphertext. Configuration
  stores credential IDs, not plaintext passwords. A failed credential read must
  not silently become an anonymous connection.
- Configuration callbacks read the current settings when dispatched. A same-ID
  credential update calls `RefreshOpenCodeCredentials()` to reread the secret.

## OpenCode ownership

- `App.OpenCode*.cs` owns the monitor client and publishes
  `CurrentOpenCodePageSource`. Each page-created client has independent ownership.
- Changing source or shutting down synchronously revokes the old source before
  awaiting outstanding operations. Consumers fence results by source/session
  generation and dispose their own clients.
- `OpenCodeInteractionGate` coordinates page and notification mutations. An
  uncertain mutation remains protected; changing credentials cannot clear it.
- Navigation consumes a structured trusted target, never a URL or a decoded
  request-ID guess. It does not automatically submit prompts or approve actions.

## Single instance and notifications

- `App.Activation.cs` registers `FluentCode.Main` before creating a window.
- `App.ActivationPipe.cs` uses `FluentCode.Activation.v1.<primaryPID>` with
  current-user-only local pipes and bounded frames. IPC input is not authorization.
- One `CompletionNotificationService` owns notification registration and uses the
  FluentCode display name/icon. No alternate application identity is consulted.
- Pending interactions, runtime nonce mappings and completion targets are not
  persisted. Unknown or expired activations require application-side revalidation.
- Detailed semantics: [notifications](notifications-contract.md) and
  [OpenCode notification integration](notification-integration-contract.md).

## Shutdown and taskbar

- The UI dispatcher owns HWND/taskbar operations. Only authoritative OpenCode
  execution observations publish outcomes; disconnect and HTTP acceptance are
  not completion signals.
- Closing stops admission, revokes sources, cancels/drains owned work, releases
  clients and disposes the notification service without synchronous STA waits.
- Background mode keeps the application alive when configured; final shutdown
  releases native hooks. See [taskbar integration](taskbar-integration-contract.md).
