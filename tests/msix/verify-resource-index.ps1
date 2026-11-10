[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PriPath,
    [string]$SourcePriPath,
    [Parameter(Mandatory)][string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
$makepri = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\makepri.exe'
if (!(Test-Path -LiteralPath $makepri)) { throw 'Windows SDK 26100 makepri is required to validate the package resource index.' }
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$dump = Join-Path $EvidenceRoot 'package-resources.xml'
& $makepri dump /if $PriPath /of $dump /dt detailed /o | Out-Null
if ($LASTEXITCODE) { throw "makepri dump failed: $LASTEXITCODE" }
[xml]$index = Get-Content -Raw -LiteralPath $dump
$map = $index.SelectSingleNode('/PriInfo/ResourceMap[@primary="true"]')
if (!$map -or $map.name -ne 'mingz.SST.OpenCodeDesktop.Fluent') { throw 'Primary resource map must match the MSIX package identity.' }
$required = @(
    'Files/App.xbf',
    'Files/MainWindow.xbf',
    'Files/Pages/OpenCodePage.xbf',
    'Files/Styles/AppTokens.xbf',
    'Files/Microsoft.UI.Xaml/Themes/themeresources.xbf',
    'Files/Microsoft.UI.Xaml/Themes/generic.xbf',
    'Files/WinUIEx/Themes/Generic.xbf',
    'Files/DevWinUI/Themes/Generic.xbf',
    'Files/CommunityToolkit.WinUI.Controls.SettingsControls/Themes/Generic.xbf'
)
foreach ($relative in $required) {
    $uri = 'ms-resource://mingz.SST.OpenCodeDesktop.Fluent/' + $relative
    $resource = $map.SelectSingleNode(".//NamedResource[@uri='$uri']")
    if (!$resource -or !$resource.SelectSingleNode('Candidate[@type="EmbeddedData"]/Base64Value')) {
        throw "Package primary resources.pri is missing embedded XAML: $relative. Sidecar PRIs alone are insufficient for installed activation."
    }
}
$mergedResources = 0
if ($SourcePriPath) {
    $sourceDump = Join-Path $EvidenceRoot 'app-resources.xml'
    & $makepri dump /if $SourcePriPath /of $sourceDump /dt detailed /o | Out-Null
    if ($LASTEXITCODE) { throw "makepri source dump failed: $LASTEXITCODE" }
    [xml]$sourceIndex = Get-Content -Raw -LiteralPath $sourceDump
    foreach ($source in $sourceIndex.SelectNodes('/PriInfo/ResourceMap//NamedResource')) {
        $relative = $source.uri -replace '^ms-resource://[^/]+/', ''
        $target = $map.SelectSingleNode(".//NamedResource[@uri='ms-resource://mingz.SST.OpenCodeDesktop.Fluent/$relative']")
        if (!$target) { throw "App PRI resource omitted from primary index: $relative" }
        foreach ($candidate in $source.SelectNodes('Candidate')) {
            $value = if ($candidate.type -eq 'EmbeddedData') { $candidate.Base64Value } else { $candidate.Value }
            $matches = @($target.SelectNodes('Candidate') | Where-Object {
                $_.type -eq $candidate.type -and $(if ($_.type -eq 'EmbeddedData') { $_.Base64Value } else { $_.Value }) -ceq $value
            })
            if (!$matches.Count) { throw "App PRI candidate changed while merging: $relative" }
        }
        $mergedResources++
    }
}
$result = @{ Passed = $true; Pri = $PriPath; ResourceMap = $map.name; EmbeddedXamlChecked = $required; AppResourcesCompared = $mergedResources; InstalledOrLaunched = $false }
$result | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'resource-index-validation.json')
$result | ConvertTo-Json
