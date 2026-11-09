# Focused source-linked tool naming, generic detail, home layout, header-affordance and detail-preview regressions. No app build or live UI.
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
$homeLayout = Extract-Method 'OpenCodeHomeView.cs' 'ApplyLayout'
$homeSettingsLayout = Extract-Method 'OpenCodeHomeView.cs' 'ApplySettingsButtonLayout'
$homeSource = [IO.File]::ReadAllText((Join-Path "$repo/src/FluentCode/Pages/OpenCode" 'OpenCodeHomeView.cs'))
$homeGeometry = [regex]::Match($homeSource, '(?ms)^    internal const double SideCardWidth\b.*?^    internal const double WideThreshold[^\r\n]*')
if (!$homeGeometry.Success) { throw 'Missing production home geometry constants.' }
$extracted = Join-Path $output 'ViewMethods.cs'
(@(
    'using System;'
    'using FluentCode.Pages.OpenCode;'
    "internal sealed partial class HoverProbe {`n$hover`n}"
    "internal static partial class GenericProbe {`n$generic`n}"
    "internal sealed partial class HomeLayoutProbe {`n$($homeGeometry.Value)`n$homeLayout`n$homeSettingsLayout`n}"
) -join "`n") | Set-Content -Encoding UTF8 $extracted

# Execute the actual tooltip-only projections; keep UI labels, error text and announcements separate.
$tooltipProjection = foreach ($item in @(
    @{ File = 'Motion/ActionFeedback.cs'; Name = 'FailureTooltip'; Class = 'FeedbackTooltipProbe'; Access = 'private' },
    @{ File = 'Pages/OpenCode/OpenCodeSessionInsightsView.cs'; Name = 'StateTooltip'; Class = 'CapabilityTooltipProbe'; Access = 'private' },
    @{ File = 'Pages/OpenCode/OpenCodeBrowserPanelState.cs'; Name = 'CompactTooltipStatus'; Class = 'BrowserTooltipProbe'; Access = 'public' }
)) {
    $text = [IO.File]::ReadAllText((Join-Path "$repo/src/FluentCode" $item.File))
    $match = [regex]::Matches($text, ('(?ms)^    ' + $item.Access + ' static string ' + $item.Name + '\([^\r\n]*\) =>.*?^    \};'))
    if ($match.Count -ne 1) { throw "Missing/ambiguous production tooltip projection: $($item.Name)" }
    "internal static partial class $($item.Class) {`n$($match[0].Value)`n}"
}
$browserContract = [IO.File]::ReadAllText("$repo/src/FluentCode/Services/OpenCode/Browser/OpenCodeBrowserContracts.cs")
$availability = [regex]::Matches($browserContract, '(?ms)^public enum OpenCodeBrowserAvailability\r?\n\{.*?^\}')
if ($availability.Count -ne 1) { throw 'Missing/ambiguous production browser availability enum.' }
$tooltipGenerated = Join-Path $output 'TooltipProjections.cs'
(@('using FluentCode.Services.OpenCode;', 'using FluentCode.Services.OpenCode.Browser;',
    "namespace FluentCode.Services.OpenCode.Browser {`n$($availability[0].Value)`n}") + $tooltipProjection) -join "`n" | Set-Content -Encoding UTF8 $tooltipGenerated

$production = @()
foreach ($name in @('OpenCodeConstants.cs', 'OpenCodeJson.cs', 'OpenCodeExceptions.cs', 'OpenCodeDtos.Common.cs',
    'OpenCodeDtos.Messages.cs', 'OpenCodeDtos.Sessions.cs', 'OpenCodeDtos.Agents.cs', 'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Models.cs', 'OpenCodeDtos.Capabilities.cs',
    'OpenCodeShellCompletion.cs', 'OpenCodeInstructionNotice.cs', 'OpenCodeInstructionSource.cs', 'OpenCodeTodoReplay.cs',
    'OpenCodeRecentModels.cs', 'OpenCodeCapabilityStatus.cs')) {
    $production += Join-Path "$repo/src/FluentCode/Services/OpenCode" $name
}
foreach ($name in @('OpenCodeToolRowPresentation.cs', 'OpenCodeDetailPopupLayout.cs', 'OpenCodeStepRunPresentation.cs', 'OpenCodeLineDiff.cs',
    'OpenCodeToolDetailBuiltinData.cs', 'OpenCodeDisplayText.cs', 'OpenCodeBrowserToolPresentation.cs', 'OpenCodeSubagentPresentation.cs',
    'OpenCodeExecuteInnerCatalog.cs', 'OpenCodeSessionToolData.cs', 'OpenCodeContextStatusParser.cs', 'OpenCodeWorkingPhrases.cs', 'OpenCodeWorkingClock.cs',
    'OpenCodeWorkingFamily.cs', 'OpenCodeModelPresentation.cs', 'OpenCodeAgentIcons.cs', 'OpenCodeAgentMentionScope.cs', 'OpenCodeAgentTreeProjection.cs', 'OpenCodeComposerNativeDialogModels.cs', 'OpenCodeComposerInputPolicy.cs')) {
    $production += Join-Path "$repo/src/FluentCode/Pages/OpenCode" $name
}
$assembly = Join-Path $output 'OpenCodeToolPresentationTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += ($production + @($extracted, $tooltipGenerated, (Join-Path $PSScriptRoot 'View.TestHost.cs'), (Join-Path $PSScriptRoot 'Program.cs'), (Join-Path $PSScriptRoot 'Omo307PresentationTests.cs'), (Join-Path $PSScriptRoot 'AgentIdentityTests.cs'), (Join-Path $PSScriptRoot 'TooltipCopyTests.cs'), (Join-Path $PSScriptRoot 'QuestionTodoFormatTests.cs'))) | ForEach-Object { '"' + $_ + '"' }
$response = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $response
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$response" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeToolPresentationTests.runtimeconfig.json')
Write-Output "Headless compilation passed (SDK $($sdk.Name); warnings treated as errors)."
& $dotnet $assembly $repo 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }

# Execute the actual tab classifier, header/state methods and status slot with inert WinUI
# surfaces. This verifies geometry/automation contracts, not native rendering or Narrator audio.
function Extract-TabMember([string]$Text, [string]$Name) {
    $matches = [regex]::Matches($Text, ('(?ms)^    (?:private|internal) [^\r\n]*\b' + [regex]::Escape($Name) + '\([^\r\n]*\)\r?\n    \{.*?^    \}'))
    if ($matches.Count -ne 1) { throw "Missing/ambiguous production tab member: $Name" }
    return $matches[0].Value
}
$tabsSource = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.Tabs.cs")
$stateSource = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.SessionStateUi.cs")
$entry = [regex]::Matches($tabsSource, '(?ms)^    private sealed class SessionTabEntry\([^\r\n]+\)\r?\n    \{.*?^    \}')
$tone = [regex]::Matches($stateSource, '(?ms)^    internal enum SessionIndicatorTone\r?\n    \{.*?^    \}')
if ($entry.Count -ne 1 -or $tone.Count -ne 1) { throw 'Missing/ambiguous production tab entry or indicator enum.' }
$tabMembers = @($entry[0].Value, $tone[0].Value, (Extract-TabMember $stateSource 'ClassifySessionIndicator'))
foreach ($name in @('CreateSessionTab', 'ApplySessionTabState', 'SetTabItemStatus')) { $tabMembers += Extract-TabMember $tabsSource $name }
$tabGenerated = Join-Path $output 'TabStatus.PageMembers.cs'
@'
using System;
using System.Collections.Generic;
using System.Globalization;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
namespace FluentCode.Pages;
public sealed partial class OpenCodePage {
'@ + "`n" + ($tabMembers -join "`n") + "`n}" | Set-Content -Encoding UTF8 $tabGenerated
$tabAssembly = Join-Path $output 'OpenCodeTabStatusTests.dll'
$tabArguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $tabAssembly + '"'))
$tabArguments += Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$motionSource = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCode/OpenCodeWorkingPhrases.cs")
$motion = [regex]::Matches($motionSource, '(?ms)^internal static class OpenCodeWorkingMotion\r?\n\{.*?^\}')
if ($motion.Count -ne 1) { throw 'Missing/ambiguous production working-motion constants.' }
$motionGenerated = Join-Path $output 'TabStatus.WorkingMotion.cs'
"using System; namespace FluentCode.Pages.OpenCode;`n" + $motion[0].Value | Set-Content -Encoding UTF8 $motionGenerated
$tabSources = @("$repo/src/FluentCode/Services/OpenCode/OpenCodeSessionUiState.cs",
    "$repo/src/FluentCode/Services/OpenCode/OpenCodeSessionUiStateStore.cs", "$repo/src/FluentCode/Services/OpenCode/OpenCodeConstants.cs",
    "$repo/src/FluentCode/Pages/OpenCode/OpenCodeTabStatusSlot.cs", $tabGenerated,
    $motionGenerated, (Join-Path $PSScriptRoot 'TabStatus.TestHost.cs'), (Join-Path $PSScriptRoot 'TabStatusTests.cs'))
$tabArguments += $tabSources | ForEach-Object { '"' + $_ + '"' }
$tabResponse = Join-Path $output 'tab-status.compile.rsp'
$tabArguments | Set-Content -Encoding UTF8 $tabResponse
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$tabResponse" 2>&1 | Tee-Object -FilePath (Join-Path $output 'tab-status.compile.log')
if ($LASTEXITCODE -ne 0) { throw "Tab status compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeTabStatusTests.runtimeconfig.json')
& $dotnet $tabAssembly $repo 2>&1 | Tee-Object -FilePath (Join-Path $output 'tab-status.tests.log')
if ($LASTEXITCODE -ne 0) { throw "Tab status tests failed: $LASTEXITCODE" }
