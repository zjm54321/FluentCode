# Headless BG1/SB1 source contracts. Does not launch the app or access the desktop (AU1).
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
Check ($root.Background -eq 'Transparent') 'BG1: page base exposes desktop acrylic'
Check ($page.Doc.DocumentElement.Background -eq 'Transparent') 'BG1: Page itself cannot supply an opaque base'
$dock = $page.Doc.SelectSingleNode('//p:Style[@x:Key="OpenCodeDockSurfaceStyle"]/p:Setter[@Property="Background"]', $page.Ns)
Check ($dock.Value -eq 'Transparent') 'BG1: full-height dock columns expose acrylic; cards own their surfaces'
foreach ($name in 'SessionLayoutRoot', 'TimelineGrid', 'HomePageHost', 'NewSessionHeader') {
    $node = $page.Doc.SelectSingleNode("//*[@x:Name='$name']", $page.Ns)
    Check ($null -ne $node -and (!$node.HasAttribute('Background') -or $node.Background -eq 'Transparent')) "BG1: $name has no opaque base"
}
$brush = $main.Doc.SelectSingleNode('//p:NavigationView/p:NavigationView.Resources/p:SolidColorBrush[@x:Key="NavigationViewContentBackground"]', $main.Ns)
Check ($brush.Color -eq 'Transparent') 'BG1: native NavigationView content does not cover the backdrop'
$border = $main.Doc.SelectSingleNode('//p:NavigationView/p:NavigationView.Resources/p:SolidColorBrush[@x:Key="NavigationViewContentGridBorderBrush"]', $main.Ns)
Check ($border.Color -eq 'Transparent') 'BG1: NavigationView content-grid border is transparent'
Check ($window -match '(?s)if \(DesktopAcrylicController.IsSupported\(\)\).*?else if \(MicaController.IsSupported\(\)\)') 'BG1: desktop acrylic preferred, Mica fallback'
Check ($window.Contains('Kind = DesktopAcrylicKind.Default') -and $window.Contains('IsInputActive = true')) 'BG1: default desktop acrylic stays active when unfocused'
Check ($window.Contains('SetSystemBackdropConfiguration(_backdropConfiguration)') -and $window.Contains('AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>())')) 'BG1: official controller target/configuration wiring'
Check ($window.Contains('RootElement.ActualThemeChanged += RootElement_ActualThemeChanged') -and $window.Contains('_backdropConfiguration.Theme = RootElement.ActualTheme switch')) 'BG1: controller follows root actual theme'
Check ($window.Contains('RootElement.ActualThemeChanged -= RootElement_ActualThemeChanged') -and $window.Contains('_desktopAcrylicController?.Dispose()')) 'BG1: controller and theme subscription cleaned up on close'
Check (([regex]::Matches($window, 'Closed \+= MainWindow_Closed;')).Count -eq 1) 'BG1: close cleanup registered once independently of title-bar support'
if ($EvidenceRoot) {
    New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
    $results | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'results.txt')
}
$results
