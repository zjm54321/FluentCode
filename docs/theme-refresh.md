# Runtime theme refresh (2026-10-31)

## Mechanism

`AppThemeController` listens to `UISettings.ColorValuesChanged`, marshals to the window dispatcher, and explicitly resolves Windows' current light/dark app theme when the saved choice is System. Explicit Light/Dark choices remain authoritative. Disposal removes the native subscription.

Code-selected assets use a weak, window-scoped refresh registry plus Loaded/ActualThemeChanged reconciliation. The root notification is queued until theme inheritance finishes. The native regression reproduced an Image whose ActualTheme was Light while its SVG still referenced `logo-dark.svg` after Light → Dark → Light; listening only to the Image or an ancestor was insufficient. The registry does not retain detached pages.

Native checked-state brush aliases live in Light/Dark ThemeDictionaries. Replacing ordinary ResourceDictionary brush entries during a theme callback updated the dictionary but left the active checked icon black in dark mode; the regression now asserts the actual FontIcon foreground, not only the resource entry. High-contrast template resources remain native.

## Fixed sites

Paths are relative to the repository root; line numbers refer to this change.

| Site | Change |
| --- | --- |
| `src/AIFrontend/Services/Appearance/AppThemeController.cs:17`, `src/AIFrontend/MainWindow.xaml.cs:83`, `src/AIFrontend/MainWindow.xaml.cs:898` | System changes now propagate through the live window root; saved app theme uses the same controller. |
| `src/AIFrontend/Services/Appearance/ThemeResources.cs:18`, `src/AIFrontend/Services/Appearance/ThemeResources.cs:31` | Weak refresh registry for code-selected assets; UI-queued root reconciliation. |
| `src/AIFrontend/Pages/OpenCode/OpenCodeThemeAssets.cs:10`, `src/AIFrontend/Pages/OpenCodePage.Home.cs:57`, `src/AIFrontend/Pages/OpenCodePage.Home.cs:58` | New-conversation and home OpenCode wordmarks choose the current host theme on attach and every root change. |
| `src/AIFrontend/Pages/OpenCode/OpenCodeRightPanelView.cs:99` | Connection/footer wordmark uses the same refresh path. |
| `src/AIFrontend/Pages/OpenCode/OpenCodeModelLogo.cs:45` | OpenAI, Grok, DeepSeek, GLM and Kimi light/dark provider SVG/bitmap variants reconcile after root changes; existing async revision guard retained. |
| `src/AIFrontend/Pages/OpenCode/OpenCodeAgentIconTheme.cs:15` | Shared agent glyph colors reconcile after root changes. |
| `src/AIFrontend/Pages/NormalPage.xaml.cs:72`, `src/AIFrontend/Pages/Web/ProviderBrandIcons.cs:110` | Website provider icons reconcile via the root hub; old async loads cannot overwrite a different current theme. |
| `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserButton.cs:44`, `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserButton.cs:88` | Checked/hover/pressed native aliases use ThemeDictionaries; status-dot brushes resolve in the host theme, not the application's startup theme. |
| `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserStrip.cs:311`, `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserStrip.cs:313`, `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserStrip.cs:315` | Preview outline/badge use the current host theme; error/agent glyphs use ThemeResource styles. |
| `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserOverlay.cs:147`, `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserOverlay.cs:180`, `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserOverlay.cs:186`, `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserOverlay.cs:273` | Pointer ripple, page-tool checked aliases, agent-pill background/border and activity text no longer retain resolved application brushes. |
| `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserSelection.cs:71`, `src/AIFrontend/Pages/OpenCode/OpenCodeBrowserSelection.cs:80` | Selection-mode glyph and annotation pill use ThemeResource styles. |
| `src/AIFrontend/Pages/OpenCode/OpenCodePersistentPtyInventoryView.cs:97`, `src/AIFrontend/Pages/OpenCode/OpenCodePersistentPtyInventoryView.cs:244`, `src/AIFrontend/Pages/OpenCode/OpenCodePersistentPtyInventoryView.cs:321`, `src/AIFrontend/Pages/OpenCode/OpenCodePersistentPtyInventoryView.cs:438` | Terminal checked aliases use ThemeDictionaries; live dot, chip titles/outlines and switcher selected/title/bar brushes use ThemeResource styles. |
| `src/AIFrontend/Pages/OpenCode/OpenCodePersistentPtyView.cs:145`, `src/AIFrontend/Pages/OpenCode/OpenCodePersistentPtyView.cs:410`, `src/AIFrontend/Pages/OpenCode/OpenCodePersistentPtyView.cs:421` | Terminal badge/marker resolve in the actual theme and reconcile on load; terminate-action foreground uses a ThemeResource style. |
| `src/AIFrontend/Pages/OpenCode/OpenCodeWslQuickAccessView.cs:43` | Editor SplitButton background override uses ThemeDictionaries. |
| `src/AIFrontend/Pages/Local/LocalChatView.cs:1463` | Session-pane fallback background resolves against the pane's actual theme rather than the application's startup theme. |

## Audited existing paths

- `src/AIFrontend/Pages/OpenCode/OpenCodeWslQuickAccessIcons.cs:47`: VS Code/VSCodium/Zed PathIcons inherit native foreground; executable thumbnails are not theme variants. All three inherited colors are asserted by the regression.
- `src/AIFrontend/Pages/Web/WebViewHost.cs:519`, `src/AIFrontend/Pages/Web/WebViewHost.cs:544`: WebView2 already reapplies background/Profile.PreferredColorScheme on ActualThemeChanged, Loaded and activation, without reload or profile replacement. It now receives live system-theme propagation from the root.
- `src/ThirdParty/WindowsTerminalControl/src/WindowsTerminalControl.WinUI/TerminalControl.cs:132`, `src/ThirdParty/WindowsTerminalControl/src/WindowsTerminalControl.WinUI/TerminalControl.cs:516`: native terminal already reapplies SurfaceColors on ActualThemeChanged and load; terminal well/status rendering also had a theme listener. No third-party changes.
- `src/AIFrontend/MainWindow.xaml.cs:148`: native Mica backdrop already updates its SystemBackdropConfiguration theme. Existing Composition uses clipping, offset/opacity/scale and shadows rather than cached theme-dependent color brushes.
- `src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailSurface.cs:237`: diff surfaces already recompute theme colors. Tool rows, timeline/reasoning rows, session insights and Mermaid already have theme refresh paths.
- `src/AIFrontend/AIFrontend.csproj:13`: unpackaged application, no separate themed native launch splash. The composer “splash” is placeholder text, not a theme-selected image.

## Regression and evidence

`tests/theme-refresh/run.ps1 -PublishDirectory <native app output> -EvidenceRoot <new directory>` launches an isolated startup-smoke process and disposable profile. It temporarily changes the current user's `AppsUseLightTheme`, broadcasts the Windows setting change and restores the original registry value in `finally`. It closes only its own PID. Do not run concurrent theme-setting tests on the same desktop.

The opt-in fixture (`AIF_THEME_REFRESH_TEST=1`) captures XAML-root PNGs and asserts, in the same process:

- App Light → Dark → Light, including a wordmark detached/reinserted across the change.
- Four real/fixture/footer wordmark URI variants; wordmark and five provider images change pixels and restore their original light pixels.
- Agent color, VS Code/VSCodium/Zed inherited foreground, and the actual checked browser glyph foreground.
- Windows Light → Dark while following System.
- App Light stays Light while Windows switches Light → Dark.

Final native build/publish/runtime-copy hashes passed: `artifacts/theme-refresh-20261031-v4/results.json`.

Final live regression passed in PID 87676: `artifacts/theme-refresh-20261031-proof-4/results.json`; assertions/hashes in `artifacts/theme-refresh-20261031-proof-4/assertions.json`; before/after screenshots in `artifacts/theme-refresh-20261031-proof-4/app-light.png`, `artifacts/theme-refresh-20261031-proof-4/app-dark.png`, `artifacts/theme-refresh-20261031-proof-4/system-light.png` and `artifacts/theme-refresh-20261031-proof-4/system-dark.png` (plus round-trip/override images). These are RenderTargetBitmap captures of the live XAML root; native backdrop pixels are transparent.

The live regression uses the Debug **build output**. The Debug **publish output** produced by the assigned restoration script fails before the fixture with `Microsoft.UI.Xaml.Markup.XamlParseException`, HRESULT `0x802B000A`; that output has no loose `.xbf` files, while the working build output has six. Build/publish commands and runtime-copy verification themselves pass. This packaging issue is separate from the theme changes and was left for the publishing lane; the runner records executable/DLL/PRI hashes for its actual tested candidate.
