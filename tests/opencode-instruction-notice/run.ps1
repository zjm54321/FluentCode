# Headless only: installed .NET 10 SDK/reference packs, no restore, no new packages, no UI, no live server.
# Fake HttpMessageHandler and inert UI/presenter probes only. Production sources compile read-only.
# The in-progress OpenCodeSessionUiState*.cs lane and the WinUI pairing service are excluded here.
param([switch]$QueueOnly, [switch]$WindowOnly, [string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-instruction-notice-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path $output) { throw "Evidence root already exists: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'OpenCodeInstructionNoticeTests.dll'
# Exact pure presenter metrics; native row classes still require the worktree app build.
$chipSource = [IO.File]::ReadAllText("$repo/src/AIFrontend/Pages/OpenCode/OpenCodeTimelineChipView.cs")
$metrics = [regex]::Match($chipSource, '(?ms)^internal static class OpenCodeTimelineRowMetrics\r?\n\{.*?^\}')
if (!$metrics.Success) { throw 'Timeline presenter metrics extraction failed.' }
$metricsSource = Join-Path $output 'TimelineRowMetrics.Extracted.cs'
[IO.File]::WriteAllText($metricsSource, "namespace AIFrontend.Pages.OpenCode;`n" + $metrics.Value)
$reasoningSource = [IO.File]::ReadAllText("$repo/src/AIFrontend/Pages/OpenCode/OpenCodeReasoningRowView.cs")
foreach ($presenter in @($chipSource, $reasoningSource)) {
    if (!$presenter.Contains('new GridLength(OpenCodeTimelineRowMetrics.IconColumn)') -or !$presenter.Contains('OpenCodeTimelineRowStyle.Apply(')) {
        throw 'Timeline presenter is not wired to shared icon/row styling.'
    }
}
if ($reasoningSource -notmatch 'TrackPointer\(_row, e\); OpenCodeToolDetailPopup.Shared.HoverEnter\(this\)') {
    throw 'Reasoning preview must capture the pointer before hover entry.'
}
$toolRowSource = [IO.File]::ReadAllText("$repo/src/AIFrontend/Pages/OpenCode/OpenCodeToolPartView.cs")
if ($toolRowSource -notmatch '(?s)_statusIcon.Visibility = active \|\| model.Status == OpenCodeToolRowStatus.Completed \|\| !chrome.ShowsStatusIcon\s*\? Visibility.Collapsed : Visibility.Visible;' -or
    $toolRowSource.Contains('Glyph = "\uE73E"') -or $reasoningSource.Contains('_statusIcon')) {
    throw 'PG2 successful tool/reasoning rows must not show a green check; progress and failure chrome stay separate.'
}
Write-Output 'PASS PG2 child-row source contract (tool/reasoning no green check; native classes compiled by app build, not rendered).'
$arguments = @('/nologo', '/target:exe', '/debug:embedded', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += '"' + $metricsSource + '"'
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
# Full production OpenCode client/projection/DTOs, minus the in-progress UI-state lane and the WinUI pairing service.
$arguments += Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" |
    Where-Object { $_.Name -NE 'OpenCodePairingService.cs' -and $_.Name -notlike 'OpenCodeSessionUiState*.cs' } |
    ForEach-Object { '"' + $_.FullName + '"' }
# Real (not stubbed) Services.Notifications types required by the wildcard-linked session-control client/DTOs.
@('OpenCodeInteractionGate.cs', 'OpenCodeExecutionProjection.cs', 'NotificationInteractions.cs',
    'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Services/Notifications" $_) + '"'
}
# Real renderer/title/parser algorithms and event args. ToolStates is included in Services DTOs above.
# Explicit list excludes all four replaced presenters and every other WinUI test host.
@('OpenCodeConversationView.cs', 'OpenCodeConversationWindow.cs', 'OpenCodeErrorRecoveryProjection.cs', 'OpenCodeSubagentPresentation.cs', 'OpenCodeSubagentTaskTitles.cs',
    'OpenCodeToolRowPresentation.cs', 'OpenCodeLineDiff.cs', 'OpenCodeStepRunPresentation.cs', 'OpenCodeQueuedPromptsView.cs', 'OpenCodeToolDetailBuiltinData.cs', 'OpenCodeExecuteInnerCatalog.cs', 'OpenCodeContextStatusParser.cs',
    'OpenCodeInteractionUi.cs', 'OpenCodeComposerActivityPolicy.cs', 'OpenCodeWorkingClock.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Pages/OpenCode" $_) + '"'
}
@('Program.cs', 'ConversationLifecycle.cs', 'ConversationView.TestHost.cs', 'RedesignRegression.cs', 'Wave5Timeline.cs', 'Wave6Timeline.cs', 'VisibleIdle.cs', 'Windowing.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path $PSScriptRoot $_) + '"'
}
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile" | Tee-Object -FilePath (Join-Path $output 'compile.txt')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeInstructionNoticeTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly $QueueOnly.IsPresent $WindowOnly.IsPresent | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
