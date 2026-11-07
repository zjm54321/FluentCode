# Focused source-linked tool naming, generic detail, header-affordance and detail-preview theme/placement regressions. No app build or live UI.
param([string]$EvidenceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-tool-presentation-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path $output) { throw 'A new evidence directory is required.' }
New-Item -ItemType Directory -Path $output | Out-Null
Write-Output "Evidence directory: $output"

# Compile the actual small view methods against inert surfaces; fail if their source boundaries change.
function Extract-Method([string]$File, [string]$Name) {
    $text = [IO.File]::ReadAllText((Join-Path "$repo/src/FluentCode/Pages/OpenCode" $File))
    $matches = [regex]::Matches($text, ('(?ms)^    private [^\r\n]*\b' + [regex]::Escape($Name) + '\([^\r\n]*\)\r?\n    \{.*?^    \}'))
    if ($matches.Count -ne 1) { throw "Missing/ambiguous production method: $Name" }
    return $matches[0].Value
}
$hover = Extract-Method 'OpenCodeToolGroupView.cs' 'UpdateChevronAffordance'
$generic = Extract-Method 'OpenCodeToolDetailContent.cs' 'BuildGeneric'
$extracted = Join-Path $output 'ViewMethods.cs'
('using FluentCode.Pages.OpenCode;' + "`ninternal sealed partial class HoverProbe {`n" + $hover + "`n}`ninternal static partial class GenericProbe {`n" + $generic + "`n}") | Set-Content -Encoding UTF8 $extracted

$production = @()
foreach ($name in @('OpenCodeConstants.cs', 'OpenCodeJson.cs', 'OpenCodeExceptions.cs', 'OpenCodeDtos.Common.cs',
    'OpenCodeDtos.Messages.cs', 'OpenCodeDtos.Sessions.cs', 'OpenCodeDtos.Agents.cs', 'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Models.cs', 'OpenCodeDtos.Capabilities.cs',
    'OpenCodeShellCompletion.cs', 'OpenCodeInstructionNotice.cs', 'OpenCodeInstructionSource.cs', 'OpenCodeTodoReplay.cs',
    'OpenCodeRecentModels.cs', 'OpenCodeCapabilityStatus.cs')) {
    $production += Join-Path "$repo/src/FluentCode/Services/OpenCode" $name
}
foreach ($name in @('OpenCodeToolRowPresentation.cs', 'OpenCodeDetailPopupLayout.cs', 'OpenCodeStepRunPresentation.cs', 'OpenCodeLineDiff.cs',
    'OpenCodeToolDetailBuiltinData.cs', 'OpenCodeBrowserToolPresentation.cs', 'OpenCodeSubagentPresentation.cs',
    'OpenCodeExecuteInnerCatalog.cs', 'OpenCodeSessionToolData.cs', 'OpenCodeContextStatusParser.cs', 'OpenCodeWorkingPhrases.cs', 'OpenCodeWorkingClock.cs',
    'OpenCodeWorkingFamily.cs', 'OpenCodeModelPresentation.cs', 'OpenCodeAgentIcons.cs', 'OpenCodeAgentMentionScope.cs', 'OpenCodeAgentTreeProjection.cs', 'OpenCodeComposerNativeDialogModels.cs', 'OpenCodeComposerInputPolicy.cs')) {
    $production += Join-Path "$repo/src/FluentCode/Pages/OpenCode" $name
}
$assembly = Join-Path $output 'OpenCodeToolPresentationTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += ($production + @($extracted, (Join-Path $PSScriptRoot 'View.TestHost.cs'), (Join-Path $PSScriptRoot 'Program.cs'), (Join-Path $PSScriptRoot 'Omo307PresentationTests.cs'), (Join-Path $PSScriptRoot 'AgentIdentityTests.cs'))) | ForEach-Object { '"' + $_ + '"' }
$response = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $response
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$response" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeToolPresentationTests.runtimeconfig.json')
Write-Output "Headless compilation passed (SDK $($sdk.Name); warnings treated as errors)."
& $dotnet $assembly $repo 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
