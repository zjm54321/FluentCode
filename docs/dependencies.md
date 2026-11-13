# Dependency update policy and integration

Use maintained community packages and stable releases. Dependency upgrades do not authorize replacing controls or changing the application's Windows API contract.

- Toolchain: .NET SDK 10.0.403; Windows SDK BuildTools 10.0.28000.2957 and MSIX tools 1.7.261102100.
- Keep `net10.0-windows10.0.26100.0`, the application's 19044 minimum, x64 RID and self-contained Windows App SDK deployment.
- BrowserRuntime: integrity-pinned Node 26.13.0 LTS and Lighthouse 13.6.0. Platform-inapplicable optional npm bindings stay in the lock but are not shipped; applicable Windows x64 bindings remain required.
- Mermaid 12.3.0 is the unchanged npm browser bundle, with its integrity, SHA-256 and embedded licences retained. The adapter uses root `htmlLabels: false`. Headless application tests do not render Mermaid; actual diagrams still need a desktop check.

## Windows App SDK 2.6.5

The app and patched terminal library use the 2.6.5 umbrella package. A split-only WinUI reference left older umbrella packages requested by stable Charts, Mapsui, Toolkit and the terminal beta in the graph. Their legacy targets duplicated the 2.x WinUI imports and broke the MSIX tools' `CustomBeforeMicrosoftCommonTargets` hook. The direct umbrella pin unifies those targets on 2.6.5. The independent RichEdit prototype uses the smaller split `Microsoft.WindowsAppSDK.WinUI` 2.6.5 package successfully.

App notifications, AppInstance and windowing compile against the new packages. Child-HWND terminal hosting is unchanged. Packaged publish and manifest checks are headless; installed activation, notification delivery, theme changes and window interaction require a user-approved desktop session.

## DevWinUI 10.9.1

`DevWinUI` brings `DevWinUI.Base` 10.9.1 and `Microsoft.Graphics.Win2D` 1.4.0. The dependency upgrade itself instantiated no DevWinUI control. Since 1.2611.18 two are in use, each behind one adapter file, and a headless check keeps the vendor namespace out of every other C# file:

- `IssueLabel`, through `Controls/CommunityTextBadge.cs`: badges without an icon in Native UI cards. Badges with an icon are still built by hand and styled to match.
- `ConfettiCannon`, through `Pages/OpenCode/NativeUi/NativeUiCommunityEffects.cs`: the Celebration burst.

That effects file also holds an adapter for `Shimmer`, and `App.xaml` a style for it (`AppStreamingShimmerStyle`). Nothing calls them yet: the streaming placeholder still draws its own bars.

Application dictionaries are merged in this order:

1. WinUI `XamlControlsResources`.
2. `ms-appx:///DevWinUI/Themes/Generic.xaml`.
3. `ms-appx:///CommunityToolkit.WinUI.Controls.SettingsControls/Themes/Generic.xaml`.
4. App tokens and existing application resources.

Later merged dictionaries win duplicate-key lookups. The actual Toolkit PRI confirms its Generic dictionary merges the unchanged SettingsCard and SettingsExpander dictionaries. Comparing the PRI identifiers with DevWinUI resources confirms 48 shared identifiers, including the three standard theme dictionary names. Loading the entire Toolkit dictionary after DevWinUI preserves the Toolkit definitions instead of copying selected values or relying on generic-theme fallback. DevWinUI has no application-level implicit style targeting stock WinUI controls; nested styles remain in their control/template scopes.

The MSIX check asserts this merge order, verifies both Generic XBFs in the primary PRI, and verifies the Win2D DLLs and harvested activation classes. These checks establish source/resource provenance and package completeness. Settings cards in Light, Dark and HighContrast, and application startup/resource initialization, still need visual/runtime verification on the desktop.

## CommunityToolkit.WinUI.Animations 8.2.251219

Referenced since 1.2611.18 for a flashcard flip animation that is not written yet. The flashcard swaps its faces in one frame, as before, and no application code uses the package.

## Net.Codecrete.QrCodeGenerator 3.2.1

Encodes the pairing link shown by “连接其他设备”. MIT, no dependencies, `net6.0` and `netstandard2.0` assets, actively released (3.0.0 in May 2026, 3.2.1 in September 2026). The licence text ships as `licenses/qrcodegenerator-LICENSE.txt`.

QRCoder 1.8.1 was the other candidate. Its `net6.0` dependency group brings `System.Drawing.Common` 6.0.0 and `Microsoft.Win32.SystemEvents` 6.0.0, which the application graph does not otherwise contain.

Only `QrCode.EncodeText` and `QrCode.ToOutlines` are used. The application draws the outline polygons as one XAML path, so no bitmap, GDI+ or SVG code path is involved. The build confirms the assembly restores and publishes; whether a phone camera reads the rendered code still needs a desktop check.

## Intentional holds

- SkiaSharp and its views/native-assets/HarfBuzz bindings stay at 3.119.2. Stable LiveCharts 2.0.5 and Mapsui WinUI 5.1.0 target this ecosystem; Mapsui's SkiaSharp 4 support is a beta and does not establish stable WinUI support.
- HarfBuzzSharp/native assets stay at 8.3.1.3 (the legacy Linux asset remains 2.8.2.3). The Svg/ ShimSkiaSharp family stays at 3.2.1 and Topten.RichTextKit at 0.4.167 with the same graphics graph.
- `CI.Microsoft.Terminal.WinUI3.Unofficial` remains 1.0.20-beta.1: no stable exists, and the vendored ABI/hosting patches depend on that exact renderer build.
- Do not substitute .NET 11 RC, Toolkit 8.3 preview, Mapsui 5.2 beta, Mermaid 13 alpha or experimental App SDK releases for stable pins.
- Read-only `.slim` source pins and historical gallery provenance are not shipped runtime packages. Changing them would modify protected reference inputs or invalidate the fixture baseline.
