# Offline FormCard view harness: REAL card/presentation/UI-helper/DTO/policy sources + SYNTHETIC WinUI stand-ins.
# No restore/build/WinUI/network/certificate store/Page/native POST.
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$BaselineFormCard
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$EvidenceRoot = (Resolve-Path $EvidenceRoot).ProviderPath
$scratch = Join-Path $EvidenceRoot 'temp'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $root 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw 'Cached .NET 10 SDK/reference pack required; no installation attempted.' }
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$references = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | Sort-Object FullName)
$src = Join-Path $repo 'src/AIFrontend'
$themeSource = Join-Path $src 'App.xaml'
$anchorSource = Join-Path $src 'Pages/OpenCode/OpenCodeConversationView.cs'
[xml]$theme = [IO.File]::ReadAllText($themeSource)
$ns = [Xml.XmlNamespaceManager]::new($theme.NameTable)
$ns.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
foreach ($rule in @(
    @('OpenCodeQuestionCautionTextStyle','Foreground','SystemFillColorCautionBrush'),
    @('OpenCodeQuestionErrorTextStyle','Foreground','SystemFillColorCriticalBrush'),
    @('OpenCodeQuestionChromeStyle','Background','CardBackgroundFillColorDefaultBrush'),
    @('OpenCodeQuestionChromeStyle','BorderBrush','AccentFillColorDefaultBrush'),
    @('OpenCodeQuestionFallbackChromeStyle','Background','CardBackgroundFillColorDefaultBrush'),
    @('OpenCodeQuestionFallbackChromeStyle','BorderBrush','CardStrokeColorDefaultBrush'),
    @('OpenCodeQuestionStatusTextStyle','Foreground','TextFillColorPrimaryBrush'),
    @('OpenCodeCautionIconStyle','Foreground','SystemFillColorCautionBrush'),
    @('OpenCodeSubtleCardChromeStyle','Background','SubtleFillColorSecondaryBrush'),
    @('OpenCodeSubtleCardChromeStyle','BorderBrush','CardStrokeColorDefaultBrush'),
    @('OpenCodeCardChromeStyle','Background','CardBackgroundFillColorDefaultBrush'),
    @('OpenCodeCardChromeStyle','BorderBrush','CardStrokeColorDefaultBrush'),
    @('OpenCodeDividerBorderStyle','BorderBrush','DividerStrokeColorDefaultBrush'),
    @('OpenCodeDividerSurfaceStyle','Background','DividerStrokeColorDefaultBrush'),
    @('OpenCodeSubtleSurfaceStyle','Background','SubtleFillColorSecondaryBrush'),
    @('OpenCodeCardOutlineStyle','BorderBrush','CardStrokeColorDefaultBrush'),
    @('OpenCodeTransparentToolButtonStyle','Background','SubtleFillColorTransparentBrush'),
    @('OpenCodeTransparentSurfaceStyle','Background','SubtleFillColorTransparentBrush'),
    @('OpenCodeCautionBadgeStyle','Background','SystemFillColorCautionBackgroundBrush'),
    @('OpenCodeCriticalBadgeStyle','Background','SystemFillColorCriticalBackgroundBrush'),
    @('OpenCodeNeutralBadgeStyle','Background','SystemFillColorNeutralBackgroundBrush'))) {
    $setter = $theme.SelectSingleNode("//p:Style[@x:Key='$($rule[0])']/p:Setter[@Property='$($rule[1])']", $ns)
    if (!$setter -or $setter.Value -ne ('{ThemeResource ' + $rule[2] + '}')) { throw "Question theme contract failed: $rule" }
}
if ([IO.File]::ReadAllText($anchorSource) -notmatch 'ApplyStyle\(_fallback, "OpenCodeQuestionFallbackChromeStyle"\)') { throw 'Question anchor must apply theme-resource-backed chrome style.' }
$redesignSources = @(Get-ChildItem (Join-Path $src 'Pages') -Filter 'OpenCode*.cs' -Recurse)
foreach ($file in $redesignSources) {
    if ([IO.File]::ReadAllText($file.FullName) -match '\b(?:Foreground|Background|BorderBrush)\s*=\s*(?:\w+\.)?TryBrush\(') {
        throw "Frozen redesign brush assignment: $($file.FullName)"
    }
}
if ([IO.File]::ReadAllText((Join-Path $src 'Pages/OpenCode/OpenCodeFormCard.cs')) -match '\.Foreground\s*=') { throw 'Question text must not receive a local Foreground override, including null.' }
Write-Output 'PASS 21 ThemeResource setter contracts, anchor hookup and redesign frozen-brush audit; no runtime theme claim.'
$shared = @(
    (Join-Path $src 'Services/OpenCode/OpenCodeDtos.Forms.cs'),
    (Join-Path $src 'Services/OpenCode/OpenCodeDtos.Permissions.cs'),
    (Join-Path $src 'Services/OpenCode/OpenCodeJson.cs'),
    (Join-Path $src 'Pages/OpenCode/OpenCodeInteractionPolicy.cs'),
    (Join-Path $src 'Pages/OpenCode/OpenCodeInteractionUi.cs'),
    (Join-Path $src 'Pages/OpenCode/OpenCodeFormFieldPresentation.cs'),
    (Join-Path $PSScriptRoot 'WinUiStandIns.cs'),
    (Join-Path $PSScriptRoot 'Program.cs'))

function Invoke-ViewCheck([string]$Name, [string]$FormCard, [bool]$ExpectPass) {
    $output = Join-Path $EvidenceRoot $Name
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $sources = @($FormCard) + $shared
    $assembly = Join-Path $output 'FormViewTests.dll'
    $arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
    $arguments += $references | ForEach-Object { '/reference:"' + $_.FullName + '"' }
    $arguments += $sources | ForEach-Object { '"' + $_ + '"' }
    $rsp = Join-Path $output 'compile.rsp'
    $arguments | Set-Content -Encoding UTF8 $rsp
    $inputs = @($sources) + @($references.FullName) + @($compiler, $dotnet, $rsp, $themeSource, $anchorSource) + @($redesignSources.FullName)
    $before = @(Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash)
    $before | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-before.json')
    Get-FileHash -LiteralPath $sources -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes.json')
    try {
        & $dotnet exec $compiler "@$rsp" 2>&1 | Out-File -Encoding utf8 (Join-Path $output 'compile.txt')
        Get-Content (Join-Path $output 'compile.txt')
        if ($LASTEXITCODE -ne 0) { throw "$Name compile failed: $LASTEXITCODE" }
        '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' |
            Set-Content -Encoding UTF8 (Join-Path $output 'FormViewTests.runtimeconfig.json')
        & $dotnet $assembly 2>&1 | Out-File -Encoding utf8 (Join-Path $output 'results.txt')
        $code = $LASTEXITCODE
        Get-Content (Join-Path $output 'results.txt')
        if ($ExpectPass -and $code -ne 0) { throw "$Name tests failed: $code" }
        if (!$ExpectPass -and $code -eq 0) { throw "$Name unexpectedly passed (harness does not discriminate)." }
    } finally {
        $after = @(Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash)
        $after | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-after.json')
        if (Compare-Object $before $after -Property Path, Hash) { throw "$Name compiled inputs changed during validation." }
    }
    Write-Output "PASS $Name (expectPass=$ExpectPass) compiled inputs unchanged: $($sources.Count) sources, $($references.Count) references, $($inputs.Count) hashed inputs."
}

if ($BaselineFormCard) { Invoke-ViewCheck 'baseline-check' $BaselineFormCard $false }
Invoke-ViewCheck 'current-check' (Join-Path $src 'Pages/OpenCode/OpenCodeFormCard.cs') $true
Write-Output "Evidence directory: $EvidenceRoot"
