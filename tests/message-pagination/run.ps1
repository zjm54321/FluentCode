# Real production pagination partial + ContextEstimate/usage tokens + real OpenCode transport;
# only the WinUI/XAML page surface is stubbed by the test host. Same headless .NET 10 pattern as the
# existing tests; no restore, UI, or new dependencies.
param([string]$EvidenceRoot = '', [string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$repo = if ($SourceRoot) { (Resolve-Path $SourceRoot).ProviderPath } else { (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath }
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('message-pagination-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
$env:TEMP = Join-Path $output 'temp'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP | Out-Null
Write-Output "Evidence directory: $output"
$assembly = Join-Path $output 'MessagePaginationTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$openCode = @(
    'OpenCodeClient.cs', 'OpenCodeClient.Events.cs', 'OpenCodeEventReducer.cs', 'OpenCodeClient.Interactions.cs', 'OpenCodeClient.Agents.cs', 'OpenCodeClient.SessionInfo.cs',
    'OpenCodeClient.View.cs', 'OpenCodeClient.Usage.cs', 'OpenCodeClient.Capabilities.cs',
    'OpenCodeClient.SessionControls.cs', 'OpenCodeClient.SessionManagement.cs', 'OpenCodeDtos.SessionControls.cs',
    'OpenCodeAuthenticationMode.cs', 'OpenCodeClientOptions.cs', 'OpenCodeConstants.cs', 'OpenCodeExceptions.cs',
    'OpenCodeJson.cs', 'OpenCodeInstructionNotice.cs', 'OpenCodeDtos.Common.cs', 'OpenCodeDtos.Sessions.cs',
    'OpenCodeDtos.Messages.cs', 'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Inbox.cs', 'OpenCodeDtos.Permissions.cs',
    'OpenCodeDtos.Forms.cs', 'OpenCodeDtos.Agents.cs', 'OpenCodeDtos.Models.cs', 'OpenCodeDtos.Capabilities.cs',
    'OpenCodeUsageAggregator.cs', 'OpenCodeContextEstimate.cs'
    'OpenCodeCompletionCrashTrace.cs'
) | ForEach-Object { Join-Path "$repo/src/AIFrontend/Services/OpenCode" $_ }
$sources = $openCode + @(
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeHomeProjection.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeSessionControlCoordinator.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeSessionTracking.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Notifications/OpenCodePageSource.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Notifications/OpenCodeExecutionProjection.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Notifications/NotificationArgumentCodec.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Notifications/NotificationActivationInfo.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Notifications/NotificationSource.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Notifications/OpenCodeInteractionGate.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Notifications/NotificationInteractions.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.MessagePagination.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.MessageBinding.cs'),
    (Join-Path $PSScriptRoot 'OpenCodePage.TestHost.cs'),
    (Join-Path $PSScriptRoot 'EventLatencyHost.cs'),
    (Join-Path $PSScriptRoot 'EventLatencyTests.cs'),
    (Join-Path $PSScriptRoot 'SessionBindingTests.cs'),
    (Join-Path $PSScriptRoot 'Program.cs'))
$pagePath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.xaml.cs'
$builtinPath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.BuiltinActions.cs'
$ackPath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.ViewAcknowledgement.cs'
$eventPath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.EventSync.cs'
$extractions = @(@{ Path = $pagePath; Names = @('SwitchToSessionAsync', 'NavigateToSessionAsync', 'UpdateEmptyState', 'RefreshMessagesAsync', 'CancelSessionOperations', 'OrderMessages', 'ObserveExecutionState', 'MarkWorking') },
    @{ Path = "$repo/src/AIFrontend/Pages/OpenCodePage.Home.cs"; Names = @('HomeEventCurrent', 'OwnsHome', 'RemoteChildDirectory', 'HomeSessionActionTargetCurrent', 'CopyHomeSessionId', 'OpenHomeSessionAsync') },
    @{ Path = "$repo/src/AIFrontend/Pages/OpenCodePage.HomeActions.cs"; Names = @('HomeActionOwner', 'CaptureHomeActionRoots', 'CanRequestHomeSessionAction', 'OwnsHomeAction', 'HomeActionRootCurrent', 'CanStartHomeAction', 'RunHomeSessionActionAsync', 'SetHomeActionStatus', 'InvalidateDeletedHomeSession', 'RefreshHomeActionMetadataAsync') },
    @{ Path = "$repo/src/AIFrontend/Pages/OpenCodePage.ComposerLayout.cs"; Names = @('ConversationColumn') },
    @{ Path = $builtinPath; Names = @('ResetAcceptedHistoryProjection') },
    @{ Path = $ackPath; Names = @('InvalidateViewAcknowledgement') },
    @{ Path = $eventPath; Names = @('PrepareEventSnapshot', 'OnEventStreamConnected', 'OnMessageHistoryWorkflowEnded', 'OnEventMessageHistoryReset', 'StopEventSync', 'DrainEventQueue', 'RefreshListedSessionStatusesAsync', 'RefreshConversationSessionActivity') })
$members = @()
foreach ($extraction in $extractions) {
    $text = [IO.File]::ReadAllText($extraction.Path)
    foreach ($name in $extraction.Names) {
        $matches = [regex]::Matches($text, ('(?m)^    (?:private|internal|partial) [^\r\n]*\b' + $name + '\('))
        if ($matches.Count -ne 1) { throw "Missing/ambiguous production member: $name" }
        $start = $matches[0].Index
        $opening = $text.IndexOf('{', $start); $arrow = $text.IndexOf('=>', $start)
        $block = if ($opening -ge 0 -and ($arrow -lt 0 -or $opening -lt $arrow)) {
            [regex]::Match($text.Substring($start), '(?ms)\A.*?^    \}\r?$')
        } else { [regex]::Match($text.Substring($start), '(?ms)\A.*?=>[^;]*;') }
        if (!$block.Success) { throw "Extraction failed: $name" }; $members += $block.Value
    }
}
$generated = Join-Path $output 'PageMembers.cs'
@'
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using OpenCodePageSource = AIFrontend.Services.Notifications.OpenCodePageSource;
using SessionControlKind = AIFrontend.Services.Notifications.SessionControlKind;
using Windows.ApplicationModel.DataTransfer;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
namespace AIFrontend.Pages;
public sealed partial class OpenCodePage {
'@ + "`n" + ($members -join "`n") + "`n}" | Set-Content -Encoding UTF8 $generated
$sources += $generated
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
$runtime = Join-Path $output 'MessagePaginationTests.runtimeconfig.json'
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 $runtime
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$references = @(Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll')).FullName
$inputs = @($sources + $references + @($extractions.Path) + @($PSCommandPath, $dotnet, $compiler, $responseFile, $pagePath, $builtinPath, $ackPath, $eventPath, $runtime)) | Sort-Object -Unique
@{ CompiledSources = $sources; Extractions = $extractions; Compiler = $compiler; Host = $dotnet; References = $references; Rsp = $responseFile; RuntimeConfig = $runtime } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $output 'input-membership.json')
Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes-before.json')
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
Write-Output 'Headless compilation passed (warnings treated as errors).' | Tee-Object -FilePath (Join-Path $output 'compile.log') -Append
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes-after.json')
if ([IO.File]::ReadAllText((Join-Path $output 'source-hashes-before.json')) -ne [IO.File]::ReadAllText((Join-Path $output 'source-hashes-after.json'))) { throw 'Inputs changed during validation.' }
Get-FileHash -LiteralPath $assembly, $responseFile, (Join-Path $output 'compile.log'), (Join-Path $output 'tests.log'), (Join-Path $output 'input-membership.json') -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'artifact-hashes.json')
Write-Output "Evidence directory: $output"
