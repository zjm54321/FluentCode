# FluentCode step 1 — full rename and OpenCode-only client

The solution, project, source directory, root/C#/XAML namespaces, assembly,
resource references, runners and current documentation now use FluentCode.
`git mv` was used for the solution, source directory, project and icon generator.
The repository root remains `AIFrontend-history-20261020` by request.
No commits were made. The existing AIFrontend process was not stopped or used
for validation. Title-bar/tab layout and styling remain step 2 work.

## Identifier and data inventory

| Item | Decision and implementation |
| --- | --- |
| Solution/project/assembly | `FluentCode.sln`; `src/FluentCode/FluentCode.csproj:6-12`: root namespace, assembly/title/product/description and executable are FluentCode. C#/XAML/resource/InternalsVisibleTo references and all active scripts follow the new paths. |
| Window title | `src/FluentCode/MainWindow.xaml:7`; `src/FluentCode/MainWindow.xaml.cs:626-627` `SetSessionWindowTitle`: FluentCode, or FluentCode — current session. No title-bar/tab restyling. |
| Local profile | `src/FluentCode/Services/Configuration/AppDataPaths.cs:18,140-167`: `%LOCALAPPDATA%\AIFrontend` → `%LOCALAPPDATA%\FluentCode`, full recursive copy to a unique sibling staging directory followed by same-volume atomic directory move. `.fluentcode-migration-v1` commits migration; the old directory remains untouched as backup. |
| Settings and removed-feature settings | `src/FluentCode/Services/Configuration/SettingsService.cs:49`; `src/FluentCode/Models/AppSettings.cs:138-139`: same `settings.json` and schema; extension JSON retains legacy provider/model values on subsequent saves even though their UI/code is removed. |
| Saved connection and credentials | `src/FluentCode/Services/Configuration/DpapiSecretStore.cs:31-33,57`: copy `secrets/*.bin` verbatim, preserve credential IDs, `CurrentUser` DPAPI scope and null entropy. No PasswordVault/resource-name or Credential Manager migration is needed: the app does not use them. Same Windows user can decrypt the copied bytes. |
| Sessions and UI state | `src/FluentCode/Services/Configuration/AppDataPaths.cs:19-21,58-65`: entire tree copied, including legacy `sessions/` and `opencode-session-ui-state.json`; session/browser-discovery/editor preference stores continue using AppDataPaths. Removed local chat data is retained on disk, not deleted. |
| Recent models | `src/FluentCode/Services/OpenCode/OpenCodeRecentModels.cs:25-28`: new root/isolated smoke root; `opencode-model-recents.json` is copied before any page/store initialization by `src/FluentCode/App.xaml.cs:49-50`. The DTO-only source-linked test layer does not depend on the app configuration layer. |
| OpenCode browser/WebView2 | `src/FluentCode/Services/OpenCode/Browser/OpenCodeBrowserWorkspace.cs:39`; `src/FluentCode/Pages/OpenCode/OpenCodeBrowserPreviewWebContent.cs:61`; `src/FluentCode/Pages/OpenCode/OpenCodeMermaidView.cs:117`: Browser/Profiles, Browser/LocalPreview and Mermaid/UserData move with the whole profile. Legacy WebView2 and LocalTranscript folders are also copied as backups, although their removed features are no longer opened. |
| Logs/crashes | `src/FluentCode/App.xaml.cs:25-29`: new crashes go to `%LOCALAPPDATA%\FluentCode\logs\crash.log` with a FluentCode header (or disposable test profile). Prior executable-relative `artifacts/logs` evidence is left intact. |
| Roaming / registry / startup | No roaming data root, app-owned settings registry keys or Run/autorun entries exist in the inspected source. Read-only OS theme, WSL and editor discovery registry access is unchanged. No user's startup/pinned shortcuts are rewritten. |
| Single instance | `src/FluentCode/App.Activation.cs:20`: **new** AppInstance key `FluentCode.Main`, not the legacy key. `src/FluentCode/App.ActivationPipe.cs:24`: **new** pipe `FluentCode.Activation.v1.<pid>`. A FluentCode launch cannot silently forward to AIFrontend. Smoke uses an isolated key per disposable profile. |
| Old app still running | `src/FluentCode/Services/Configuration/AppDataPaths.cs:156-164,187-192`: first migration explicitly refuses while any legacy process is found, shows an actionable message, never kills/activates it and never opens a blank replacement profile. After a completed migration, the apps can run independently against their own profiles; subsequent legacy changes are not reimported. |
| Notifications/AUMID/COM | `src/FluentCode/Services/Notifications/CompletionNotificationService.cs:121`; `src/FluentCode/App.Activation.cs:60`: WinAppSDK `Register("FluentCode", icon)` owns unpackaged executable-derived AUMID/COM registration for the new executable. There is no hardcoded app AUMID or activator CLSID to copy. Old executable registration/toasts are deliberately not unregistered, preserving the running legacy app. Runtime notification nonce/tag IDs are not persistent user data. |
| Tray | `src/FluentCode/Services/Background/TrayIcon.cs:18,59,85`: new process-scoped window class `FluentCode.Tray.<pid>`, title and tooltip FluentCode; shared A-glass ICO. HWND-scoped tray IDs and system `TaskbarCreated` message remain unchanged. No persistent tray data to migrate. |
| Environment/test controls | `src/FluentCode/App.StartupSmoke.cs:10-16`, `src/FluentCode/Motion/FluentMotionPolicy.cs:33`, `tests/opencode-composer/run.ps1:49`: FC_STARTUP_SMOKE_ROOT, FC_THEME_REFRESH_TEST, FC_TRANSCRIPT_INPUT_TEST, FC_MOTION_SLOWMO and FC_SPLASH_REPO. `FC_MIGRATION_PROFILE_ROOT` is an explicit native-smoke disposable-profile hook only. |
| Native validation script filenames | Requested `artifacts/scripts/aif-restoration-native.ps1` and `artifacts/scripts/aif-native-release.ps1` names retained as validation entry points; their solution/project/executable paths are FluentCode. |

### Failure and retry behavior

The migration lock serializes profile copying. Locked/inaccessible files are
errors, not silently omitted. Reparse-point files/directories require manual
inspection. A nonempty target without a completion marker is never merged or
overwritten. Copy failures clean only this attempt's staging directory where
possible; the old profile is untouched and retry uses a new staging directory.
Enough disk space for a complete second profile is required. Exit the legacy
app normally before the first real migration, and do not restart it during
copying. Later changes in either separate profile are intentionally independent.
Old pinned/startup shortcuts still point at the old executable and should be
updated manually when switching to FluentCode.

## Removed production files (36)

Paths below use the renamed source root; these files no longer exist.

```text
src/FluentCode/MainWindow.Local.cs
src/FluentCode/Pages/NormalPage.xaml
src/FluentCode/Pages/NormalPage.xaml.cs
src/FluentCode/Pages/Settings/SettingsView.Cookies.cs
src/FluentCode/Pages/Settings/SettingsView.Providers.cs
src/FluentCode/Pages/Settings/SettingsView.Models.cs
src/FluentCode/Services/Configuration/LocalSessionStore.cs
src/FluentCode/Pages/Local/DisplayMessage.cs
src/FluentCode/Pages/Local/LocalChatView.cs
src/FluentCode/Pages/Local/LocalTaskCompletedEventArgs.cs
src/FluentCode/Pages/Local/LocalTaskStatusEventArgs.cs
src/FluentCode/Pages/Local/PlainTranscriptView.cs
src/FluentCode/Pages/Local/TranscriptDocument.cs
src/FluentCode/Pages/Local/TranscriptWebViewHost.cs
src/FluentCode/Pages/Web/LocalHostPanel.cs
src/FluentCode/Pages/Web/ProviderBrandIcons.cs
src/FluentCode/Pages/Web/WebCookieCleaner.cs
src/FluentCode/Pages/Web/WebProvider.cs
src/FluentCode/Pages/Web/WebViewHost.cs
src/FluentCode/Services/Contracts/IChatClient.cs
src/FluentCode/Services/Contracts/IModelCatalogClient.cs
src/FluentCode/Services/Protocols/ModelCatalogClient.cs
src/FluentCode/Services/Protocols/ProtocolChatClient.cs
src/FluentCode/Services/Protocols/ProtocolClientException.cs
src/FluentCode/Services/Protocols/ProtocolRequestBody.cs
src/FluentCode/Services/Protocols/ProtocolStreamDecoder.cs
src/FluentCode/Services/Protocols/ProtocolTransport.cs
src/FluentCode/Services/Protocols/SseReader.cs
src/FluentCode/Models/ChatMessage.cs
src/FluentCode/Models/ChatRequest.cs
src/FluentCode/Models/ChatStreamEvent.cs
src/FluentCode/Models/LocalSession.cs
src/FluentCode/Models/ModelDescriptor.cs
src/FluentCode/Models/ModelRef.cs
src/FluentCode/Models/ProviderEndpoint.cs
src/FluentCode/Models/ToolCall.cs
```

The 普通 navigation item, cookie cleaner layer, Local task/notification bridge,
provider/model/cookie settings and local notification source were removed from
the surviving shell/settings/services. OpenCode is the default/only work page;
Settings, Account/connection and Tasks remain. No Board production references
remain. Existing retained tests were already OpenCode/common tests; none was
exclusive to removed code. `notification-form` now rejects the removed local
source. SafeContentRenderer and brand logos stay because OpenCode directly uses
them for rich text/model logos. Vendored terminal sources are unchanged; only
its PATCHES.md app-name/path/build-property references changed.

## Icon A

Approved A-glass ICO and all per-size PNG/SVG assets were copied from
`artifacts/fluentcode-design-20261101-033458/icon/A-glass/` into
`src/FluentCode/Assets/AppIcon/`. The executable resource, AppWindow and tray use
the ICO; toast registration and both toast layouts use the 48 PNG. No application
splash/About image surface exists in production (composer “splash” is placeholder
text, not a bitmap); the approved 256 PNG remains available. No
Square44/150/StoreLogo assets exist in this unpackaged project.
`scripts/gen_icons.py` replaces the old generator and retains the approved
per-size glass geometry. Taskbar status-badge artwork is intentionally unchanged.

## Legacy-string exceptions

After the tracked-source grep, AIFrontend remains only in:

- `src/FluentCode/Services/Configuration/AppDataPaths.cs:140`: mandatory legacy
  migration/process-name constant.
- `docs/plans/2026-10-20-opencode-ui-repair.md:8`: historical repository-root path,
  which the user explicitly excluded from rename.
- `docs/validation/2026-10-30-opencode-ui-feedback.md`: original historical evidence.
- `tests/opencode-persistent-pty/Fixtures/nushell-session.vt`: recorded raw terminal
  validation transcript containing the historical, unchanged repository path.
- This validation/migration record, describing the source/backup and behavior.

Ignored historical artifacts, read-only slim clones and Git history were not
rewritten. No tracked project/folder/file is still named AIFrontend.

## Validation

Generated app and vendored dependency `bin/obj` directories were removed before
the renamed native builds (historical evidence and vendored sources untouched).

- Final Debug native build/publish/runtime-hash check after cancellation repair: PASS,
  `artifacts/fluentcode-step1-final-debug-20261101-0931/` (build/publish 0 warnings,
  `build.log:1194-1195`, `publish.log:711-712`).
- Final Release native build/publish/runtime-hash check after cancellation repair: PASS,
  `artifacts/fluentcode-step1-final-release-20261101-0934/`.
- Final startup smoke on the repaired published FluentCode.exe: PASS,
  `artifacts/fluentcode-step1-final-startup-20261101-0936/`.
- Disposable migration policy + native two-start/DPAPI verification: PASS,
  `artifacts/fluentcode-step1-migration-debug-20261101-0907/` and the Release
  migration runner in `artifacts/fluentcode-step1-suite-20261101-0936/evidence/data-migration/`.
- Final 28-entry suite after cancellation repair: PASS (28/28, no failures),
  `artifacts/fluentcode-step1-suite-20261101-0936/`.

The first Debug invocation hit the caller's two-minute command timeout after
compilation; the complete invocation above was rerun without that time limit.
An early Debug-publish native migration probe exposed missing loose XBF files
(generic XAML parse failure, while Release migration passed). The publish target
at `src/FluentCode/FluentCode.csproj:84-95` now copies existing application XBFs
alongside the PRI. Debug-publish policy/byte-integrity/DPAPI/native first-and-second
startup now pass at `artifacts/fluentcode-step1-migration-debug-20261101-0907/`.
The first suite was intentionally stopped by its candidate
change guard after four passing runners; final results use a fresh suite root.
The `0435` native publish attempts ran out of C: disk space while copying the
prepared browser runtime. Disk space was available after the server restart;
the final runs use new evidence roots, without changing source for that failure.

The immutable-candidate suite at `artifacts/fluentcode-step1-suite-20261101-0910/`
completed 27/28 successfully. Its `browser-runtime` runner failed with
`System.InvalidOperationException: Cancelled audit leaked sessions/subscription`
at the original `tests/browser-runtime/Program.cs:133`. A focused retry passed at
`artifacts/fluentcode-step1-browser-runtime-20261101-0918/`; the caller reproduced
the failure again at `artifacts/browser-runtime-recheck-20261101/`. The unchanged
retry suite `artifacts/fluentcode-step1-suite-20261101-0920/` passed 28/28 before
the repair below. Passing retries did not resolve the reproducible ownership race.

### Browser audit cancellation repair

Source comparison against HEAD showed only mechanical renames in all six
source-linked runtime inputs, the protocol/factory, and the Chrome test host.
The focused harness (`tests/browser-runtime/run.ps1:11-14`) has no removed web
provider/cookie-cleaner dependency and does not serialize CLR namespace names.
The actual fault was a concurrent-detach acknowledgement race: worker cleanup
and C# cancellation cleanup both detach owned sessions. The old
`if (!current.Sessions.TryUpdate(id, 1, 0)) return new();` returned immediate
success for a detach that was still running. A detached event could also remove
the session entry before its outstanding command acknowledged completion.

`src/FluentCode/Services/OpenCode/Browser/OpenCodeBrowserRuntime.cs:264-291,319-323,354`
now shares one lazily-started task per detach and waits for its actual
acknowledgement. Callback cancellation stops that caller's wait, not the owned
cleanup mutation; the existing two-second cleanup bound remains. Final cleanup
joins both owned-session and in-flight-detach keys.

`tests/browser-runtime/ChromeCdpHost.cs:104-110,149-153` holds a real root detach
across cancellation. `tests/browser-runtime/Program.cs:124-144` checks that the
public audit cannot complete before that held acknowledgement is released;
the original zero-session/subscription assertions remain, with diagnostic counts.
This regression failed on the old implementation at
`artifacts/fluentcode-step1-browser-runtime-regression-20261101-0927/` with
`Audit cancellation returned before its in-flight CDP detach completed`, then
passed after the production fix at
`artifacts/fluentcode-step1-browser-runtime-fixed-20261101-0929/`.
