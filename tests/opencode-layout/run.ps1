# Headless layout source contracts + production SB3 scroll-coordinate computations (AU1).
param([string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$src = Join-Path $repo 'src/AIFrontend'
$results = [Collections.Generic.List[string]]::new()
function Check([bool]$condition, [string]$message) {
    if (!$condition) { throw $message }
    $results.Add("PASS: $message")
}
function Read-Xaml([string]$path) {
    $doc = [xml][IO.File]::ReadAllText($path)
    $ns = [Xml.XmlNamespaceManager]::new($doc.NameTable)
    $ns.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
    $ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
    return @{ Doc = $doc; Ns = $ns }
}
$page = Read-Xaml (Join-Path $src 'Pages/OpenCodePage.xaml')
$main = Read-Xaml (Join-Path $src 'MainWindow.xaml')
$window = [IO.File]::ReadAllText((Join-Path $src 'MainWindow.xaml.cs'))
$root = $page.Doc.SelectSingleNode('//p:Grid[@x:Name="PageRoot"]', $page.Ns)
Check ($root.Background -eq 'Transparent') 'BG1: page base exposes the window material'
Check ($page.Doc.DocumentElement.Background -eq 'Transparent') 'BG1: Page itself cannot supply an opaque base'
$dock = $page.Doc.SelectSingleNode('//p:Style[@x:Key="OpenCodeDockSurfaceStyle"]/p:Setter[@Property="Background"]', $page.Ns)
Check ($dock.Value -eq 'Transparent') 'BG1: full-height dock columns expose acrylic; cards own their surfaces'
foreach ($name in 'SessionLayoutRoot', 'TimelineGrid', 'HomePageHost', 'NewSessionHeader') {
    $node = $page.Doc.SelectSingleNode("//*[@x:Name='$name']", $page.Ns)
    Check ($null -ne $node -and (!$node.HasAttribute('Background') -or $node.Background -eq 'Transparent')) "BG1: $name has no opaque base"
}
$brush = $main.Doc.SelectSingleNode('//p:NavigationView/p:NavigationView.Resources/p:SolidColorBrush[@x:Key="NavigationViewContentBackground"]', $main.Ns)
Check ($null -eq $brush) 'BG1 (choice B): native NavigationView content layer is kept, so shell and content differ'
Check ($window -match '(?s)if \(MicaController.IsSupported\(\)\).*?else if \(DesktopAcrylicController.IsSupported\(\)\)') 'BG1: Mica preferred, desktop acrylic fallback'
Check ($window.Contains('Kind = MicaKind.BaseAlt') -and $window.Contains('IsInputActive = true')) 'BG1: Mica BaseAlt stays active when unfocused'
Check ($window.Contains('SetSystemBackdropConfiguration(_backdropConfiguration)') -and $window.Contains('AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>())')) 'BG1: official controller target/configuration wiring'
Check ($window.Contains('RootElement.ActualThemeChanged += RootElement_ActualThemeChanged') -and $window.Contains('_backdropConfiguration.Theme = RootElement.ActualTheme switch')) 'BG1: controller follows root actual theme'
Check ($window.Contains('RootElement.ActualThemeChanged -= RootElement_ActualThemeChanged') -and $window.Contains('_micaController?.Dispose()')) 'BG1: controller and theme subscription cleaned up on close'
Check (([regex]::Matches($window, 'Closed \+= MainWindow_Closed;')).Count -eq 1) 'BG1: close cleanup registered once independently of title-bar support'
$timeline = $page.Doc.SelectSingleNode('//p:Grid[@x:Name="TimelineGrid"]', $page.Ns)
$scroll = $page.Doc.SelectSingleNode('//p:ScrollView[@x:Name="MessageScroll"]', $page.Ns)
$messageHost = $page.Doc.SelectSingleNode('//p:Grid[@x:Name="MessageHost"]', $page.Ns)
$bar = $page.Doc.SelectSingleNode('//*[@x:Name="ConversationScrollBar"]', $page.Ns)
Check (!$timeline.HasAttribute('ColumnDefinitions') -and !$scroll.HasAttribute('Grid.Column') -and !$bar.HasAttribute('Grid.Column')) 'SB1: single grid column keeps the wheel-active scroll viewport full width'
Check ($timeline.SizeChanged -eq 'TimelineColumn_SizeChanged' -and $messageHost.SizeChanged -eq 'TimelineColumn_SizeChanged') 'SB1: placement recalculates for timeline and message-host size changes'
Check ($bar.HorizontalAlignment -eq 'Left' -and $bar.Width -eq '56') 'SB1: native annotated bar is left positioned with its original 56px width'
Check ($bar.Loaded -eq 'ConversationScrollBar_Loaded' -and !$bar.HasAttribute('Scrolling') -and $bar.DetailLabelRequested -eq 'ConversationScrollBar_DetailLabelRequested') 'SB1: native controller owns scrolling, page owns annotations and detail only'
$layout = [IO.File]::ReadAllText((Join-Path $src 'Pages/OpenCodePage.ComposerLayout.cs'))
Check ($layout.Contains('ConversationScrollBar.Margin = new Thickness(ConversationScrollBarLeft(available, column.Width), 0, 0, 0)')) 'SB1: bar uses the same finite reading-column width as the composer/messages'
$label = $bar.SelectSingleNode('p:AnnotatedScrollBar.LabelTemplate/p:DataTemplate/p:Border', $page.Ns)
$dash = $label.SelectSingleNode('p:Rectangle', $page.Ns)
Check ($null -ne $label -and $label.HorizontalAlignment -eq 'Right' -and $label.Padding -eq '0,6,0,6') 'SB2: native label template right-aligns marks and reserves 14px height for collision handling'
Check ($dash.Width -eq '12' -and $dash.Height -eq '2' -and $dash.RadiusX -eq '1' -and $dash.RadiusY -eq '1' -and $dash.HorizontalAlignment -eq 'Right') 'SB2: each visible annotation is a short rounded horizontal dash'
Check ($dash.Fill -eq '{ThemeResource TextFillColorSecondaryBrush}') 'SB2: dashes use the native secondary-text theme brush'
Check ($label.GetAttribute('AutomationProperties.Name') -eq '{Binding}' -and $label.ChildNodes.Count -eq 1) 'SB2: template binds meaningful label content for accessibility and draws no text'
$bridge = [IO.File]::ReadAllText((Join-Path $src 'Pages/OpenCodePage.AnnotatedScrollBar.cs'))
Check ($bridge -match 'new AnnotatedScrollBarLabel\(\$"\{\(item.User \? "[^"]+" : "[^"]+"\)\}.\{item.Text\}", item.Offset\)') 'SB2: labels retain full role/text and offsets instead of A6 shortened content'
Check ($bridge.Contains('args.Content = OpenCodeScrollBarProjection.DetailText(item.User, item.Text)') -and !$bridge.Contains('args.Content = body')) 'SB3b: native hover content is role/message text, not an unrendered UIElement'
$detail = $bar.SelectSingleNode('p:AnnotatedScrollBar.DetailLabelTemplate/p:DataTemplate/p:TextBlock', $page.Ns)
Check ($null -ne $detail -and $detail.Text -eq '{Binding}' -and $detail.MaxLines -eq '3' -and $detail.MaxWidth -eq '260' -and $detail.TextWrapping -eq 'Wrap' -and $detail.TextTrimming -eq 'CharacterEllipsis') 'SB3b: native detail template binds text and bounds role plus excerpt to three lines'
Check ($detail.Foreground -eq '{ThemeResource TextFillColorPrimaryBrush}') 'SB3b: native detail text follows light/dark/high-contrast theme resources'
Check ($bridge.Contains('_annotatedPresenter.VerticalScrollController = ConversationScrollBar.ScrollController') -and !$bridge.Contains('args.Cancel = true;') -and !$bridge.Contains('MessageScroll.ChangeView')) 'SB2: native ScrollPresenter receives the real annotated scroll controller without manual request forwarding'
$conversation = [IO.File]::ReadAllText((Join-Path $src 'Pages/OpenCode/OpenCodeConversationView.cs'))
Check (!$conversation.Contains('AnnotationLabel(') -and !$bridge.Contains('AnnotationLabel(')) 'SB2: obsolete A6 two-character label computation removed'
Check (!$bridge.Contains('controller.SetValues(') -and !$bridge.Contains('InsertScalar(') -and !$bridge.Contains('CreatePropertySet(')) 'SB3a: native scrolling owns range, viewport length and composition offset'
Check (!$bridge.Contains('SetPanningElementExpressionAnimationSources') -and $bridge.Contains('presenter.VerticalScrollController = null!')) 'SB3a: native presenter owns the panning expression and releases controller on unload'
Check ($bridge.Contains('MessageScroll.LayoutUpdated += AnnotatedLayoutUpdated') -and $bridge.Contains('OpenCodeScrollBarProjection.LabelOffset(item.Offset, MessageScroll.ExtentHeight)')) 'SB3a: layout refreshes native document-coordinate labels after measuring retained history'
Check ($conversation.Contains('var viewTop = ContentTop()') -and $conversation.Contains('OpenCodeScrollBarProjection.ContentOffset(viewTop, Padding.Top, precedingHeight)') -and $conversation.Contains('precedingHeight += _window.Height(entry.Key)') -and $conversation.Contains('_viewport.VerticalOffset - ContentTop() - Padding.Top')) 'SB3a: labels and window anchors share content-top coordinates and current row heights'
if ($EvidenceRoot) {
    New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
    $results | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'results.txt')
}
$results

$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-layout-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$assembly = Join-Path $output 'ScrollBarTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += '"' + (Join-Path $src 'Pages/OpenCode/OpenCodeScrollBarProjection.cs') + '"'
$arguments += '"' + (Join-Path $PSScriptRoot 'ScrollBarTests.cs') + '"'
$arguments += '"' + (Join-Path $src 'Pages/OpenCodePage.AnnotatedScrollBar.cs') + '"'
$arguments += '"' + (Join-Path $PSScriptRoot 'AnnotatedScrollBarBridgeTests.cs') + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'ScrollBarTests.runtimeconfig.json')
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'computations.txt')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }

# Compile the actual page/controller integration against real cached WinUI refs.
# This is a library/API check only: no app build, XAML compiler, desktop or native execution.
$nuget = Join-Path $env:USERPROFILE '.nuget/packages'
$nativeRefs = @(Get-Item "$nuget/microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/Microsoft.Windows.SDK.NET.dll", "$nuget/microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/WinRT.Runtime.dll")
$nativeRefs += Get-ChildItem "$nuget/microsoft.windowsappsdk.winui/1.8.260803003/lib/net6.0-windows10.0.17763.0/*.dll"
$nativeRefs += Get-ChildItem "$nuget/microsoft.windowsappsdk.foundation/1.8.260803002/lib/net6.0-windows10.0.17763.0/*.dll"
$nativeRefs += Get-ChildItem "$nuget/microsoft.windowsappsdk.interactiveexperiences/1.8.260708001/lib/net6.0-windows10.0.18362.0/*.dll"
$nativeArgs = @('/nologo', '/target:library', '/langversion:latest', '/nullable:enable', '/warnaserror+', '/nowarn:CS1701,CS1702', ('/out:"' + (Join-Path $output 'NativeScrollingCheck.dll') + '"'))
$nativeArgs += @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll')) + $nativeRefs | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$nativeArgs += @("$src/Pages/OpenCodePage.AnnotatedScrollBar.cs", "$src/Pages/OpenCode/OpenCodeScrollBarProjection.cs", (Join-Path $PSScriptRoot 'NativeScrolling.WinUI.cs')) | ForEach-Object { '"' + $_ + '"' }
$nativeRsp = Join-Path $output 'native-compile.rsp'
$nativeArgs | Set-Content -Encoding UTF8 $nativeRsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$nativeRsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'native-compile.log')
if ($LASTEXITCODE -ne 0) { throw "Native scroll-controller API compilation failed: $LASTEXITCODE" }
Write-Output 'PASS native ScrollView/ScrollPresenter controller, events and ScrollTo API compile (not executed).'
Write-Output "Evidence directory: $output"
