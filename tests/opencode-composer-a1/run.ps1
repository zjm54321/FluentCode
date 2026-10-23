# Focused offline A1 checks: compile verbatim production policies/color application against
# explicit WinUI doubles; inspect real XAML/handler wiring. No native UI, HTTP, restore or app build.
param([string]$EvidenceRoot = '', [string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = if ($SourceRoot) { (Resolve-Path $SourceRoot).ProviderPath } else { (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath }
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $root 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-composer-a1-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$members = @()
$blankPath = "$repo/src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs"
$blank = [IO.File]::ReadAllText($blankPath)
$toolbar = [IO.File]::ReadAllText("$repo/src/AIFrontend/Pages/OpenCodePage.ComposerToolbar.cs")
$expanded = [regex]::Match($toolbar, '(?ms)^    internal static bool ExpandBlankComposerChoices\(.*?;')
if (!$expanded.Success) { throw 'Missing composer choice width policy' }
$members += $expanded.Value
foreach ($spec in @(@($blank, 'ApplyBlankAgentColor'), @($blank, 'IsBlankAgentSelectable'))) {
    $pattern = if ($spec[1] -eq 'IsBlankAgentSelectable') { '(?ms)^    private static bool IsBlankAgentSelectable\(.*?;' }
        else { '(?ms)^    (?:private|internal) [^\r\n]*\b' + $spec[1] + '\(.*?^    \}' }
    $m = [regex]::Match($spec[0], $pattern)
    if (!$m.Success) { throw "Missing production member: $($spec[1])" }
    $members += $m.Value
}
$generated = Join-Path $output 'ProductionMembers.cs'
"using System; using System.Globalization; using AIFrontend.Pages.OpenCode; using AIFrontend.Services.OpenCode; using Microsoft.UI.Xaml.Controls; using Microsoft.UI.Xaml.Media; namespace AIFrontend.Pages { public sealed partial class OpenCodePage {`n" + ($members -join "`n") + "`n} }" | Set-Content -Encoding UTF8 $generated
$assembly = Join-Path $output 'ComposerA1Tests.dll'
$sources = @($generated, (Join-Path $PSScriptRoot 'Program.cs'), "$repo/src/AIFrontend/Services/OpenCode/OpenCodeDtos.Agents.cs", "$repo/src/AIFrontend/Services/OpenCode/OpenCodeDtos.Location.cs")
$sources+=@("$repo/src/AIFrontend/Pages/OpenCode/OpenCodeAgentPresentation.cs","$repo/tests/opencode-tabs/Tab.TestHost.cs","$repo/src/AIFrontend/Pages/OpenCode/OpenCodeWorkingClock.cs")
$sources+=@("$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerEffort.cs","$repo/src/AIFrontend/Services/OpenCode/OpenCodeDtos.Models.cs")
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$references=@(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll')|ForEach-Object FullName)
$arguments += $references | ForEach-Object { '/reference:"' + $_ + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'ComposerA1Tests.runtimeconfig.json')
New-Item -ItemType File -Path (Join-Path $output 'compile.log') | Out-Null
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "A1 focused compile failed: $LASTEXITCODE" }
& $dotnet $assembly $repo | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "A1 focused tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
