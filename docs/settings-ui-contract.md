# FluentCode settings contract

`src/FluentCode/Pages/Settings/SettingsPage.cs` hosts the shared `SettingsView`.
The window owns opening/closing settings; this contract does not freeze visual
layout or require a separate settings window.

## Shared state

- Settings and credentials come from
  `FluentCode.Services.Configuration.AppServices.Settings` / `.Secrets`.
- Persistent state is defined by `src/FluentCode/Models/AppSettings.cs`,
  `BrowserSettings.cs` and `TerminalAppearanceSettings.cs`.
- The default profile is `%LOCALAPPDATA%\FluentCode`; no external profile import
  or removed-feature JSON preservation is performed.
- Plaintext credentials are never serialized into settings. DPAPI failures remain
  explicit; they do not reset credentials or silently downgrade authentication.

## Current categories

- Appearance/background, motion, fonts and terminal appearance retain their
  shared application policies.
- OpenCode settings support explicit connection configuration and V2 pairing.
  Pairing does not save or connect without the user's explicit save action.
- Session-browser settings select embedded WebView2 or external Chrome and the
  supported connection options. Saving affects newly created browser workspaces.
- Dirty browser settings participate in Save/Discard/Stay navigation protection.
- Changing a credential under the same ID refreshes the app-owned OpenCode source.

## Sources and checks

- `src/FluentCode/Pages/Settings/SettingsView.*.cs` contains category behavior.
- `src/FluentCode/Services/Configuration/SettingsService.cs` validates and saves
  settings; `DpapiSecretStore.cs` handles encrypted credentials.
- [OpenCode client](opencode-client-contract.md), [native terminal](native-terminal-integration.md)
  and [runtime theme refresh](theme-refresh.md) describe the corresponding policies.
- Current automated runners are listed in [test suite](test-suite.md).
