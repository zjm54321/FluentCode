# Offline production-linked .NET 10 csc harness for F11 user fonts: no restore, no WinUI, no App launch.
# Links the real settings store (SettingsService + ConfigurationJson + AppSettings) and the pure font logic,
# plus the DirectWrite system font catalog (Windows dwrite.dll only; read-only enumeration).
# Executes real AppFonts registration/Loaded/dispatcher logic with explicitly synthetic WinUI doubles.
param(
    [string]$SourceRoot
)
$ErrorActionPreference = 'Stop'
$repo = if ($SourceRoot) { (Resolve-Path $SourceRoot).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path }
$src = Join-Path $repo 'src/AIFrontend'
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }

# ---- Source contracts (XAML / wiring) ----
function Read-Source([string]$relative) { [IO.File]::ReadAllText((Join-Path $src $relative)) }
$appXaml = Read-Source 'App.xaml'
$tokens = Read-Source 'Styles/AppTokens.xaml'
$appCs = Read-Source 'App.xaml.cs'
$mainXaml = Read-Source 'MainWindow.xaml'
$mainCs = Read-Source 'MainWindow.xaml.cs'
$fontsView = Read-Source 'Pages/Settings/SettingsView.Fonts.cs'
$settingsView = Read-Source 'Pages/Settings/SettingsView.cs'

[xml]$appDoc = $appXaml
$ns = New-Object Xml.XmlNamespaceManager($appDoc.NameTable)
$ns.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
foreach ($key in 'BaseTextBlockStyle') {
    $setter = $appDoc.SelectSingleNode("//p:Style[@x:Key='$key']/p:Setter[@Property='FontFamily']", $ns)
    if (!$setter -or $setter.Value -ne '{ThemeResource ContentControlThemeFontFamily}') { throw "Font contract: $key must use ThemeResource ContentControlThemeFontFamily" }
}
foreach ($key in 'BodyTextBlockStyle','BodyStrongTextBlockStyle','BodyLargeTextBlockStyle','CaptionTextBlockStyle','SubtitleTextBlockStyle','TitleTextBlockStyle','TitleLargeTextBlockStyle','DisplayTextBlockStyle') {
    $style = $appDoc.SelectSingleNode("//p:Style[@x:Key='$key']", $ns)
    if (!$style -or $style.BasedOn -ne '{StaticResource BaseTextBlockStyle}') { throw "Font contract: $key must be redefined BasedOn the app BaseTextBlockStyle" }
}
# Overrides must precede every derived style that is BasedOn a built-in text style (forward-only StaticResource).
$styles = @($appDoc.SelectNodes("/p:Application/p:Application.Resources/p:ResourceDictionary/p:Style", $ns))
$baseIndex = [Array]::FindIndex($styles, [Predicate[object]]{ param($s) $s.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml') -eq 'DisplayTextBlockStyle' })
$firstDerived = [Array]::FindIndex($styles, [Predicate[object]]{ param($s) $s.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml') -eq 'BodySubtleTextBlockStyle' })
if ($baseIndex -lt 0 -or $firstDerived -lt 0 -or $baseIndex -gt $firstDerived) { throw 'Font contract: text style overrides must precede derived styles.' }
foreach ($target in 'FlyoutPresenter','MenuFlyoutPresenter','TeachingTip') {
    $setter = $appDoc.SelectSingleNode("//p:Style[@TargetType='$target' and not(@x:Key)]/p:Setter[@Property='FontFamily']", $ns)
    if (!$setter -or $setter.Value -ne '{ThemeResource ContentControlThemeFontFamily}') { throw "Font contract: popup host $target must inherit the body font" }
}
$code = $appDoc.SelectSingleNode("//p:Style[@x:Key='OpenCodeCodeTextBlockStyle']/p:Setter[@Property='FontFamily']", $ns)
if (!$code -or $code.Value -ne '{ThemeResource AppMonospaceFontFamily}') { throw 'Font contract: code style must follow AppMonospaceFontFamily live.' }
foreach ($pair in @(
    @('ContentControlThemeFontFamily', 'XamlAutoFontFamily'),
    @('AppBodyFontFamily', 'XamlAutoFontFamily'),
    @('AppSerifFontFamily', 'Cambria, Noto Serif SC, Source Han Serif SC, SimSun'),
    @('AppMonospaceFontFamily', 'Cascadia Mono, Consolas'),
    @('OpenCodeMonospaceFontFamily', 'Cascadia Mono, Consolas'))) {
    if ($tokens -notmatch ('<FontFamily x:Key="' + $pair[0] + '">' + [regex]::Escape($pair[1]) + '</FontFamily>')) { throw "Font token default missing: $($pair[0])" }
}
if ($appCs -notmatch '(?s)AppFonts\.InitializeAsync\(.*new MainWindow\(\)') { throw 'Fonts must be initialized before MainWindow is created.' }
if ($mainXaml -notmatch '(?s)x:Name="NavView"[^>]*FontFamily="\{ThemeResource ContentControlThemeFontFamily\}"') { throw 'NavView must inherit the body font to all pages.' }
if ($mainCs -notmatch 'AppFonts\.RegisterRoot\(RootElement\)') { throw 'MainWindow root must be registered for live font refresh.' }
foreach ($id in 'SettingsFontsSection','SettingsFontResetAll','SettingsFontCatalogStatus','SettingsFont{id}Combo','SettingsFont{id}Reset','SettingsFont{id}Preview','SettingsFont{id}Resolved','SettingsFont{id}Card') {
    if (!$fontsView.Contains('"' + $id + '"') -and !$fontsView.Contains('$"' + $id + '"')) { throw "Missing AutomationId: $id" }
}
if ($settingsView -notmatch 'BuildFontsSection\(\)' -or $settingsView -notmatch 'ApplyFontsToUi\(\);') { throw 'Fonts section must be wired into the appearance category and settings refresh.' }
foreach ($relative in 'Pages/OpenCode/OpenCodeRichTextPresenter.cs','Pages/OpenCode/OpenCodePermissionCard.cs','Pages/Settings/SettingsView.Fonts.cs') {
    if ((Read-Source $relative) -match 'new\s*(?:FontFamily)?\(\s*"(?:Consolas|Cascadia)') { throw "Hard-coded monospace font left in $relative" }
}
Write-Output 'PASS: font XAML/wiring source contracts.'

# Wave 6: retain exact default type-ramp definitions; size is a consumer metric transform,
# not a replacement of framework resources (icons/templates use those same resource keys).
foreach ($pair in @(
    @('BaseTextBlockStyle','BodyTextBlockFontSize'),
    @('BodyStrongTextBlockStyle','BodyStrongTextBlockFontSize'),
    @('BodyLargeTextBlockStyle','BodyLargeTextBlockFontSize'),
    @('CaptionTextBlockStyle','CaptionTextBlockFontSize'),
    @('SubtitleTextBlockStyle','SubtitleTextBlockFontSize'),
    @('TitleTextBlockStyle','TitleTextBlockFontSize'),
    @('TitleLargeTextBlockStyle','TitleLargeTextBlockFontSize'),
    @('DisplayTextBlockStyle','DisplayTextBlockFontSize'))) {
    $setter = $appDoc.SelectSingleNode("//p:Style[@x:Key='$($pair[0])']/p:Setter[@Property='FontSize']", $ns)
    if (!$setter -or $setter.Value -ne ('{StaticResource ' + $pair[1] + '}')) { throw "Typography baseline changed: $($pair[0])" }
}
foreach ($id in 'SettingsTextSizeCombo','SettingsTextSizeReset','SettingsTextSizeCurrent') {
    if (!$fontsView.Contains('"' + $id + '"')) { throw "Missing accessible text-size control: $id" }
}
if ($fontsView -notmatch 'JsonNumberHandling' -and (Read-Source 'Services/Appearance/AppFontSettings.cs') -notmatch 'JsonNumberHandling\(JsonNumberHandling.Strict\)') { throw 'Text size must reject JSON numeric strings despite shared Web serializer defaults.' }
if ($fontsView -notmatch '\u4ec5\u6062\u590d\u9ed8\u8ba4\u5b57\u53f7' -or $fontsView -notmatch '\u5b57\u4f53\u4e0e\u5b57\u53f7\u5168\u90e8\u6062\u590d\u9ed8\u8ba4') { throw 'Size-only and all-font resets must be explicitly distinguished.' }
if ((Read-Source 'Services/Appearance/AppFonts.cs') -notmatch 'root.LayoutUpdated \+= entry.OnLayoutUpdated') { throw 'Newly rendered text must pick up the selected size without another save.' }
Write-Output 'PASS: text-size baseline, accessible controls, strict numeric contract, and dynamic-consumer wiring.'

# D1: live font apply must never flip RequestedTheme (same-frame opposite->original flip crashed CoreMessagingXP.dll 0xc000027b).
$appFonts = Read-Source 'Services/Appearance/AppFonts.cs'
foreach ($pair in @(@('Services/Appearance/AppFonts.cs', $appFonts), @('Pages/Settings/SettingsView.Fonts.cs', $fontsView))) {
    if ($pair[1] -match 'RequestedTheme\s*=') { throw "D1: $($pair[0]) must not assign RequestedTheme to refresh fonts." }
}
if ($appFonts -notmatch 'TryEnqueue\(DispatcherQueuePriority\.Low,\s*RunDeferredRefresh\)') { throw 'D1: runtime font refresh must be deferred to a later low-priority dispatcher tick.' }
if ($appFonts -notmatch '(?s)public static void ApplySettings\(.*?ScheduleRefresh\(\);' ) { throw 'D1: ApplySettings must schedule (not run) the refresh.' }
$applyBody = [regex]::Match($appFonts, '(?s)public static void ApplySettings\(.*?\n    \}').Value
if ($applyBody -match 'RefreshRoots\(\)|RaiseChanged\(\)') { throw 'D1: ApplySettings must not refresh roots or raise Changed synchronously.' }
if ($appFonts -notmatch 'GetOpenPopupsForXamlRoot' -or $appFonts -notmatch 'VisualTreeHelper\.GetChild\(') { throw 'D1: deferred refresh must re-apply fonts across the visual tree and open popups.' }
if ($mainCs -notmatch 'AppFonts\.RegisterRoot\(page\)') { throw 'D1: Frame pages (cached NormalPage) must be registered for catch-up refresh.' }
Write-Output 'PASS: D1 crash-safe live font refresh source contracts.'

# H15: Mica Alt window material (DesktopAcrylic fallback) with translucent layering brushes.
if ($mainCs -notmatch 'new MicaBackdrop \{ Kind = MicaKind\.BaseAlt \}' -or $mainCs -notmatch 'new DesktopAcrylicBackdrop\(\)') { throw 'H15: main window must use Mica Alt with a DesktopAcrylic fallback.' }
foreach ($theme in 'Default','Light') {
    $res = $appDoc.SelectSingleNode("//p:ResourceDictionary.ThemeDictionaries/p:ResourceDictionary[@x:Key='$theme']/p:StaticResource[@x:Key='TabViewItemHeaderBackgroundSelected']", $ns)
    if (!$res -or $res.ResourceKey -ne 'LayerFillColorDefaultBrush') { throw "H15: $theme theme must map TabViewItemHeaderBackgroundSelected to LayerFillColorDefaultBrush (opaque default hides Mica)." }
}
$openCodeXaml = Read-Source 'Pages/OpenCodePage.xaml'
[xml]$openCodeDoc = $openCodeXaml
$openCodeNs = New-Object Xml.XmlNamespaceManager($openCodeDoc.NameTable)
$openCodeNs.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$openCodeNs.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
foreach ($theme in 'Default','Light') {
    $pageRes = $openCodeDoc.SelectSingleNode("//p:ResourceDictionary.ThemeDictionaries/p:ResourceDictionary[@x:Key='$theme']/p:StaticResource[@x:Key='TabViewItemHeaderBackgroundSelected']", $openCodeNs)
    if (!$pageRes -or $pageRes.ResourceKey -ne 'LayerFillColorDefaultBrush') { throw "H15: OpenCodePage.xaml $theme ThemeDictionary must define element-scoped TabViewItemHeaderBackgroundSelected -> LayerFillColorDefaultBrush so XamlControlsResources cannot shadow it." }
}
$hcRes = $openCodeDoc.SelectSingleNode("//p:ResourceDictionary.ThemeDictionaries/p:ResourceDictionary[@x:Key='HighContrast']/p:StaticResource[@x:Key='TabViewItemHeaderBackgroundSelected']", $openCodeNs)
if (!$hcRes -or $hcRes.ResourceKey -ne 'SystemColorWindowColorBrush') { throw 'H15: OpenCodePage.xaml HighContrast ThemeDictionary must define TabViewItemHeaderBackgroundSelected -> SystemColorWindowColorBrush.' }
$sessionPaneNode = $openCodeDoc.SelectSingleNode("//p:SplitView[@x:Name='SessionPane']", $openCodeNs)
if (!$sessionPaneNode -or $sessionPaneNode.HasAttribute('Background')) { throw 'H15: SessionPane in Pages/OpenCodePage.xaml must not set Background= (NavigationView ContentGrid already provides translucent LayerFillColorDefaultBrush over Mica).' }
foreach ($pane in 'SessionPane','InsightsPane') {
    if ($openCodeXaml -notmatch ('(?s)x:Name="' + $pane + '"[^>]*PaneBackground="\{ThemeResource AcrylicInAppFillColorDefaultBrush\}"')) { throw "H15: $pane must use AcrylicInAppFillColorDefaultBrush." }
}
# Wave 7: only the surrounding frame keeps Mica; the OpenCode reading area is deliberately opaque.
foreach ($relative in 'MainWindow.xaml','Pages/NormalPage.xaml','Pages/BoardPage.xaml','Pages/Settings/SettingsPage.xaml') {
    if ((Read-Source $relative) -match 'ApplicationPageBackgroundThemeBrush|SolidBackgroundFillColorBase') { throw "H15: opaque page background in $relative hides Mica." }
}
if ($openCodeXaml -notmatch 'Background="\{ThemeResource SolidBackgroundFillColorBaseBrush\}"') { throw 'Wave7: OpenCode reading surface must use opaque themed Fluent fill.' }
if ($settingsView -notmatch 'SettingsCodeThemeCombo' -or $settingsView -notmatch 'CodeTheme = theme') { throw 'Wave7: selectable code-only theme must use the saved settings path.' }
if ($mainXaml -notmatch '<Grid x:Name="RootElement">') { throw 'H15: window root must stay transparent (no Background).' }
Write-Output 'PASS: H15 Mica/Acrylic material source contracts.'


# ---- Production-linked headless tests ----
$output = Join-Path ([IO.Path]::GetTempPath()) ('appearance-fonts-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'AppearanceFontsTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', '/platform:x64', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$files = @(
    'Services/Appearance/AppFontSettings.cs',
    'Services/Appearance/SystemFontCatalog.cs',
    'Services/Appearance/AppFontRefreshPlan.cs',
    'Services/Appearance/AppTextSizeRefreshPlan.cs',
    'Services/Appearance/AppFonts.cs',
    'Services/OpenCode/OpenCodeAuthenticationMode.cs',
    'Services/Configuration/AppDataPaths.cs', 'Services/Configuration/AtomicFile.cs',
    'Services/Configuration/ConfigurationExceptions.cs', 'Services/Configuration/ConfigurationJson.cs',
    'Services/Configuration/SettingsService.cs'
)
$arguments += $files | ForEach-Object { '"' + (Join-Path $src $_) + '"' }
# AppSettings references ProviderEndpoint / ModelDescriptor / ChatRequest types: link the whole (pure BCL) Models folder.
$arguments += Get-ChildItem (Join-Path $src 'Models') -Filter '*.cs' | ForEach-Object { '"' + $_.FullName + '"' }
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$arguments += '"' + (Join-Path $PSScriptRoot 'WinUiFontTestHost.cs') + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'AppearanceFontsTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly (Join-Path $output 'data') | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
