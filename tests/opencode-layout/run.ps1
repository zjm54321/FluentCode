# Headless BG1/SB1/SB2 source contracts. Does not launch the app or access the desktop (AU1).
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
$scroll = $page.Doc.SelectSingleNode('//p:ScrollViewer[@x:Name="MessageScroll"]', $page.Ns)
$messageHost = $page.Doc.SelectSingleNode('//p:Grid[@x:Name="MessageHost"]', $page.Ns)
$bar = $page.Doc.SelectSingleNode('//*[@x:Name="ConversationScrollBar"]', $page.Ns)
Check (!$timeline.HasAttribute('ColumnDefinitions') -and !$scroll.HasAttribute('Grid.Column') -and !$bar.HasAttribute('Grid.Column')) 'SB1: single grid column keeps the wheel-active scroll viewport full width'
Check ($timeline.SizeChanged -eq 'TimelineColumn_SizeChanged' -and $messageHost.SizeChanged -eq 'TimelineColumn_SizeChanged') 'SB1: placement recalculates for timeline and message-host size changes'
Check ($bar.HorizontalAlignment -eq 'Left' -and $bar.Width -eq '56') 'SB1: native annotated bar is left positioned with its original 56px width'
Check ($bar.Loaded -eq 'ConversationScrollBar_Loaded' -and $bar.Scrolling -eq 'ConversationScrollBar_Scrolling' -and $bar.DetailLabelRequested -eq 'ConversationScrollBar_DetailLabelRequested') 'SB1: native bridge events preserved'
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
Check ($bridge -match 'Text = item.User \? "[^"]+" : "[^"]+"' -and $bridge.Contains('Text = item.Text, FontWeight = FontWeights.SemiBold')) 'SB2: hover details preserve role and full message source'
Check ($bridge.Contains('args.Cancel = true;') -and $bridge.Contains('MessageScroll.ChangeView(null, Math.Clamp(args.ScrollOffset, 0, MessageScroll.ScrollableHeight), null, disableAnimation: true)')) 'SB2: native track/dash navigation still uses the single conversation scroll owner'
$conversation = [IO.File]::ReadAllText((Join-Path $src 'Pages/OpenCode/OpenCodeConversationView.cs'))
Check (!$conversation.Contains('AnnotationLabel(') -and !$bridge.Contains('AnnotationLabel(')) 'SB2: obsolete A6 two-character label computation removed'
if ($EvidenceRoot) {
    New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
    $results | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'results.txt')
}
$results
