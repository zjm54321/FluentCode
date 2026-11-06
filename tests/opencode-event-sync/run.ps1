param([string]$EvidenceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-event-sync-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'OpenCodeEventSyncTests.dll'
# HtmlSanitizer targets net8; the net10 runtime unifies its framework references (as in native runners).
$arguments = @('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+','/nowarn:CS1701,CS1702',('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += Get-ChildItem "$repo/src/FluentCode/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object { '"' + $_.FullName + '"' }
@('OpenCodeInteractionGate.cs','OpenCodeExecutionProjection.cs','OpenCodePageSource.cs','NotificationInteractions.cs','NotificationArgumentCodec.cs','NotificationActivationInfo.cs','NotificationSource.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path "$repo/src/FluentCode/Services/Notifications" $_) + '"'
}
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$arguments += '"' + (Join-Path $PSScriptRoot 'StreamingPerformanceTests.cs') + '"'
# Pure production presenter scheduling/conversion and insights: no WinUI runtime or app build.
foreach ($name in @('OpenCodeSessionInsights.cs', 'OpenCodeRichTextRenderGate.cs', 'OpenCodeMarkdownImagePlan.cs')) {
    $arguments += '"' + "$repo/src/FluentCode/Pages/OpenCode/$name" + '"'
}
$arguments += '"' + "$repo/src/FluentCode/Services/Rendering/SafeContentRenderer.cs" + '"'
$arguments += Get-ChildItem "$repo/src/FluentCode/Services/Billion/*.cs" | ForEach-Object { '"' + $_.FullName + '"' }
$nuget = Join-Path $env:USERPROFILE '.nuget/packages'
foreach ($relative in @('markdig/1.4.0/lib/net10.0/Markdig.dll', 'htmlsanitizer/9.2.1039/lib/net8.0/HtmlSanitizer.dll',
    'anglesharp/1.7.2/lib/net10.0/AngleSharp.dll', 'anglesharp.css/1.0.2/lib/net10.0/AngleSharp.Css.dll')) {
    $reference = Join-Path $nuget $relative
    $arguments += '/reference:"' + $reference + '"'
    Copy-Item $reference $output
}
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeEventSyncTests.runtimeconfig.json')
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"

# Focused permission regression: real page event/interaction methods and existing card/overlay,
# with inert native controls and fake HTTP only. No copied routing or live user permission requests.
function Extract-Member([string]$text, [string]$name) {
    $matches = [regex]::Matches($text, ('(?m)^    (?:private|internal|partial) [^\r\n]*\b' + [regex]::Escape($name) + '(?:<[^>]+>)?(?:\(| =>)'))
    if ($matches.Count -ne 1) { throw "Missing/ambiguous production interaction member: $name" }
    $start = $matches[0].Index
    $opening = $text.IndexOf('{', $start); $arrow = $text.IndexOf('=>', $start)
    $block = if ($opening -ge 0 -and ($arrow -lt 0 -or $opening -lt $arrow)) {
        [regex]::Match($text.Substring($start), '(?ms)\A.*?^    \}\r?$')
    } else { [regex]::Match($text.Substring($start), '(?ms)\A.*?=>[^;]*;') }
    if (!$block.Success) { throw "Unsupported production interaction member: $name" }
    return $block.Value
}
$page = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.xaml.cs")
$members = foreach ($name in @('SwitchToSessionAsync', 'CancelSessionOperations', 'RefreshAuxiliaryAsync', 'PollTimer_Tick',
    'IsCurrentSession', 'IsCurrentBindingFields', 'IsCurrentBinding', 'BindingLost', 'CreateOperationToken', 'TryStartRead',
    'TryResolveUnknown', 'TryResolveSessionUnknown', 'TryResolve', 'TryCompleteMutation', 'ContainsRequest',
    'SetInteractionBusy', 'SettleInteraction', 'UpdateInteractionsSection', 'ShowQueryFailure', 'DecisionLabel', 'DecisionBusyLabel',
    'SubmitPermissionDecisionAsync', 'EnterGateAsync', 'HandleProtectedPermissionAtSubmit', 'HandleProtectedFormAtSubmit',
    'HandleUnknownPermissionOutcome', 'HandleRejectSideEffects', 'SubmitFormAnswerAsync', 'SubmitFormCancelAsync', 'HandleUnknownFormOutcome',
    'RecheckPermissionAsync', 'RecheckPermissionCoreAsync', 'RecheckFormAsync', 'RecheckFormCoreAsync',
    'ResolveInteractionAsync', 'ResolveInteractionCoreAsync', 'BeginSessionRecoveryAsync', 'CompleteSessionRecoveryAsync')) {
    Extract-Member $page $name
}
$events = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.EventSync.cs")
foreach ($name in @('StopEventSync', 'RestartSessionEventSync', 'OnEventStreamConnected', 'OnEventStreamDisconnected', 'OnMessageHistoryWorkflowEnded', 'DrainEventQueue')) {
    $members += Extract-Member $events $name
}
$layout = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.ComposerLayout.cs")
foreach ($name in @('HideChildComposer', 'IsChildComposerSession', 'RefreshChildComposerVisibility')) { $members += Extract-Member $layout $name }
$members += Extract-Member ([IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.ComposerOverlays.cs")) 'SyncPermissionOverlay'
$generated = Join-Path $output 'ChildPermission.PageMembers.cs'
@'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
namespace FluentCode.Pages;
public sealed partial class OpenCodePage {
'@ + "`n" + ($members -join "`n") + "`n}" | Set-Content -Encoding UTF8 $generated
$childAssembly = Join-Path $output 'ChildPermissionTests.dll'
$childArguments = $arguments | Where-Object { !($_.StartsWith('/out:')) -and !($_.EndsWith('Program.cs"')) -and !($_.EndsWith('StreamingPerformanceTests.cs"')) }
$childArguments += '/out:"' + $childAssembly + '"'
foreach ($file in @('OpenCodeInteractionsView.cs', 'OpenCodePermissionCard.cs', 'OpenCodePermissionOverlay.cs',
    'OpenCodePermissionPresentation.cs', 'OpenCodeInteractionUi.cs', 'OpenCodeInteractionPolicy.cs', 'OpenCodeSessionTracking.cs')) {
    $childArguments += '"' + "$repo/src/FluentCode/Pages/OpenCode/$file" + '"'
}
foreach ($file in @('OpenCodePage.ChildInteractions.cs', 'OpenCodePage.QuestionOverlay.cs')) { $childArguments += '"' + "$repo/src/FluentCode/Pages/$file" + '"' }
foreach ($file in @('InteractionUi.TestHost.cs', 'ChildInteractions.TestHost.cs', 'ChildInteractionsTests.cs')) { $childArguments += '"' + (Join-Path $PSScriptRoot $file) + '"' }
$childArguments += '"' + $generated + '"'
$childRsp = Join-Path $output 'child-permissions.compile.rsp'
$childArguments | Set-Content -Encoding UTF8 $childRsp
New-Item -ItemType File -Path (Join-Path $output 'child-permissions.compile.log') -Force | Out-Null
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$childRsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'child-permissions.compile.log')
if ($LASTEXITCODE -ne 0) { throw "Child permission compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'ChildPermissionTests.runtimeconfig.json')
& $dotnet $childAssembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'child-permissions.tests.log')
if ($LASTEXITCODE -ne 0) { throw "Child permission tests failed: $LASTEXITCODE" }
