# Real production pagination partial + ContextEstimate/usage tokens + real OpenCode transport;
# only the WinUI/XAML page surface is stubbed by the test host. Same headless .NET 10 pattern as the
# existing tests; no restore, UI, or new dependencies.
param([string]$EvidenceRoot = '', [string]$SourceRoot = '', [string]$BaseSourceRoot = '', [switch]$BaseRegression)
$ErrorActionPreference = 'Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$repo = if ($SourceRoot) { (Resolve-Path $SourceRoot).ProviderPath } else { (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath }
function Production-Path([string]$path) {
    if ($BaseRegression) {
        $relative = [IO.Path]::GetRelativePath($repo, $path)
        $base = Join-Path $BaseSourceRoot $relative
        if (Test-Path -LiteralPath $base) { return $base }
    }
    return $path
}
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
if ($BaseRegression) { $arguments += '/nowarn:CS0169,CS0414,CS0649' } # Inert new host state is unused by the old implementation only.
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$openCode = @(
    'OpenCodeClient.cs', 'OpenCodeAuxiliarySnapshot.cs', 'OpenCodeClient.Events.cs', 'OpenCodeEventReducer.cs', 'OpenCodeClient.Interactions.cs', 'OpenCodeClient.Agents.cs', 'OpenCodeClient.SessionInfo.cs',
    'OpenCodeClient.View.cs', 'OpenCodeClient.Usage.cs', 'OpenCodeClient.Capabilities.cs', 'OpenCodeClient.Projects.cs', 'OpenCodeDtos.Projects.cs',
    'OpenCodeDesktopBridge.cs', 'OpenCodeClient.PluginRpc.cs', 'OpenCodeModelFavoritesRpc.cs',
    'OpenCodeClient.SessionControls.cs', 'OpenCodeClient.SessionManagement.cs', 'OpenCodeDtos.SessionControls.cs',
    'OpenCodeAuthenticationMode.cs', 'OpenCodeClientOptions.cs', 'OpenCodeSocketTransport.cs', 'OpenCodeConstants.cs', 'OpenCodeExceptions.cs',
    'OpenCodeJson.cs', 'OpenCodeInstructionNotice.cs', 'OpenCodeInstructionSource.cs', 'OpenCodeDtos.Common.cs', 'OpenCodeDtos.Sessions.cs',
    'OpenCodeDtos.Messages.cs', 'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Inbox.cs', 'OpenCodeDtos.Permissions.cs',
    'OpenCodeDtos.Forms.cs', 'OpenCodeDtos.Agents.cs', 'OpenCodeDtos.Models.cs', 'OpenCodeDtos.Capabilities.cs',
    'OpenCodeUsageAggregator.cs', 'OpenCodeContextEstimate.cs', 'OpenCodeSessionUiState.cs', 'OpenCodeSessionUiStateStore.cs',
    'OpenCodeManualBackgroundIndex.cs', 'OpenCodeShellCompletion.cs', 'OpenCodeClient.AgentTreeShells.cs', 'OpenCodeTodoReplay.cs', 'OpenCodeCapabilityStatus.cs'
) | ForEach-Object { Join-Path "$repo/src/FluentCode/Services/OpenCode" $_ }
$sources = $openCode + @(
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeAgentTreeProjection.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeToolRowPresentation.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeStepRunPresentation.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeSubagentPresentation.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeBrowserToolPresentation.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeToolDetailBuiltinData.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeDisplayText.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeExecuteInnerCatalog.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeSessionToolData.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeContextStatusParser.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeLineDiff.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeHomeProjection.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeTurnOutline.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeSessionControlCoordinator.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeSessionTracking.cs'),
    (Join-Path $repo 'src/FluentCode/Services/Notifications/OpenCodePageSource.cs'),
    (Join-Path $repo 'src/FluentCode/Services/Notifications/OpenCodeExecutionProjection.cs'),
    (Join-Path $repo 'src/FluentCode/Services/Notifications/NotificationArgumentCodec.cs'),
    (Join-Path $repo 'src/FluentCode/Services/Notifications/NotificationActivationInfo.cs'),
    (Join-Path $repo 'src/FluentCode/Services/Notifications/NotificationSource.cs'),
    (Join-Path $repo 'src/FluentCode/Services/Notifications/OpenCodeInteractionGate.cs'),
    (Join-Path $repo 'src/FluentCode/Services/Notifications/NotificationInteractions.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.MessagePagination.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.MessageBinding.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.SessionPagination.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.BackgroundState.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.SwitchPerformance.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.ViewAcknowledgement.cs'),
    (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.ViewVisibility.cs'),
    (Join-Path $PSScriptRoot 'OpenCodePage.TestHost.cs'),
    (Join-Path $PSScriptRoot 'EventLatencyHost.cs'),
    (Join-Path $PSScriptRoot 'EventLatencyTests.cs'),
    (Join-Path $PSScriptRoot 'BackgroundCompletion.TestHost.cs'),
    (Join-Path $PSScriptRoot 'BackgroundCompletionTests.cs'),
    (Join-Path $PSScriptRoot 'Pf2LatencyTests.cs'),
    (Join-Path $PSScriptRoot 'SessionBindingTests.cs'),
    (Join-Path $PSScriptRoot 'Viewport.TestHost.cs'),
    (Join-Path $PSScriptRoot 'ViewportTests.cs'),
    (Join-Path $PSScriptRoot 'HomeRefresh.TestHost.cs'),
    (Join-Path $PSScriptRoot 'HomeRefreshTests.cs'),
    (Join-Path $PSScriptRoot 'HomeViewRefresh.TestHost.cs'),
    (Join-Path $PSScriptRoot 'HomeViewRefreshTests.cs'),
    (Join-Path $PSScriptRoot 'Program.cs'))
$sources = @($sources | ForEach-Object { Production-Path $_ })
$pagePath = Production-Path (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.xaml.cs')
$builtinPath = Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.BuiltinActions.cs'
$eventPath = Production-Path (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.EventSync.cs')
$extractions = @(@{ Path = $pagePath; Names = @('CreateOperationToken', 'SwitchToSessionAsync', 'NavigateToSessionAsync', 'UpdateEmptyState', 'RefreshMessagesAsync', 'StartAuxiliaryRead', 'PollTimer_Tick', 'CancelSessionOperations', 'OrderMessages', 'ObserveExecutionState', 'MarkWorking', 'RefreshSessionsAsync', 'IsRootSession', 'RebuildSessionList') },
    @{ Path = "$repo/src/FluentCode/Pages/OpenCodePage.Home.cs"; Names = @('CreateHomeStatus', 'HomeEventCurrent', 'OwnsHome', 'RemoteChildDirectory', 'HomeSessionActionTargetCurrent', 'CopyHomeSessionId', 'OpenHomeSessionAsync', 'HomeChromeTimer_Tick', 'CanRefreshHome', 'UpdateHomeRefreshVisibility', 'RequestHomeCatchUp', 'StartHomeRefreshIfDue', 'HomeEventStreamConnected', 'RefreshHomeLiveAsync', 'OnHomeEventStreamConnected', 'OnHomeActiveSessionsAccepted', 'OnHomeSessionBecameInactive', 'MarkHomeMetadataDirty', 'MarkHomeCatalogDirty', 'OnHomeCatalogEvent', 'AcceptHomeCatalogRefresh') },
    @{ Path = "$repo/src/FluentCode/Pages/OpenCodePage.HomeActions.cs"; Names = @('HomeActionOwner', 'CaptureHomeActionRoots', 'CanRequestHomeSessionAction', 'OwnsHomeAction', 'HomeActionRootCurrent', 'CanStartHomeAction', 'RunHomeSessionActionAsync', 'SetHomeActionStatus', 'InvalidateDeletedHomeSession', 'RefreshHomeActionMetadataAsync') },
    @{ Path = "$repo/src/FluentCode/Pages/OpenCodePage.ComposerLayout.cs"; Names = @('ConversationColumn', 'ConversationScrollBarLeft') },
    @{ Path = $builtinPath; Names = @('ResetAcceptedHistoryProjection') },
    @{ Path = "$repo/src/FluentCode/Pages/OpenCodePage.AgentTree.cs"; Names = @('OnAgentTreeEvent', 'RenderAgentTree', 'MergeAgentTreeShell') },
    @{ Path = $eventPath; Names = @('ApplyLiveSessionStatuses', 'CaptureHealthyEventStream', 'HasContinuousEventCoverage', 'PrepareEventSnapshot', 'OnEventStreamConnected', 'OnEventStreamDisconnected', 'OnMessageHistoryWorkflowEnded', 'OnEventMessageHistoryReset', 'StopEventSync', 'DrainEventQueue', 'UpdateLiveSessionRetry', 'IsSelectedBackgroundChildCompletion', 'RefreshListedSessionStatusesAsync', 'RefreshConversationSessionActivity') })
$members = @()
foreach ($extraction in $extractions) {
    $text = [IO.File]::ReadAllText((Production-Path $extraction.Path))
    foreach ($name in $extraction.Names) {
        $matches = [regex]::Matches($text, ('(?m)^    (?:private|internal|partial) [^\r\n]*\b' + $name + '\('))
        if ($BaseRegression -and $matches.Count -eq 0 -and $name -in @('HomeChromeTimer_Tick', 'CanRefreshHome', 'UpdateHomeRefreshVisibility', 'RequestHomeCatchUp',
            'StartHomeRefreshIfDue', 'HomeEventStreamConnected', 'RefreshHomeLiveAsync', 'OnHomeEventStreamConnected', 'OnHomeActiveSessionsAccepted', 'OnHomeSessionBecameInactive', 'MarkHomeMetadataDirty', 'MarkHomeCatalogDirty', 'OnHomeCatalogEvent', 'AcceptHomeCatalogRefresh')) { continue }
        if ($matches.Count -ne 1) { throw "Missing/ambiguous production member: $name" }
        $start = $matches[0].Index
        $opening = $text.IndexOf('{', $start); $arrow = $text.IndexOf('=>', $start)
        $block = if ($opening -ge 0 -and ($arrow -lt 0 -or $opening -lt $arrow)) {
            [regex]::Match($text.Substring($start), '(?ms)\A.*?^    \}\r?$')
        } else { [regex]::Match($text.Substring($start), '(?ms)\A.*?=>[^;]*;') }
        if (!$block.Success) { throw "Extraction failed: $name" }; $members += $block.Value
    }
}
$members += [regex]::Matches([IO.File]::ReadAllText($eventPath), '(?m)^    partial void (?:OnBrowser|OnSessionBackground|OnHome)[^;]+;') | ForEach-Object Value
# The Home scheduler's instance state/limits are production declarations, not copied policy.
foreach ($group in @(
    @{ Path = "$repo/src/FluentCode/Pages/OpenCodePage.Home.cs"; Names = @('_homeLiveCancellation', '_homeRefreshVisible', '_homeRefreshRunning', '_homeRefreshTask', '_homeCatchUpPending', '_homeCatchUpVersion', '_homeRefreshFailures', '_homeRefreshAt', '_homeStatusAt', '_homeDirtySessions', '_homeKnownChildren', '_homeCatalogPending', 'HomeMetadataReadsPerPass') },
    @{ Path = "$repo/src/FluentCode/Pages/OpenCodePage.EventSync.cs"; Names = @('_statusRefreshTask', '_statusRefreshGeneration', '_statusRefreshSource', '_statusAcceptedReads') })) {
    $text = [IO.File]::ReadAllText($group.Path)
    foreach ($name in $group.Names) {
        $field = [regex]::Matches($text, ('(?m)^    private [^\r\n]*\b' + $name + '\b[^\r\n]*;'))
        if ($field.Count -ne 1) { throw "Missing/ambiguous production Home state: $name" }
        $members += $field[0].Value
    }
}
if ($BaseRegression) {
    # Old code has no gate/metadata version fields. These declarations let the same native test
    # entry points compile; no new helper/refresh behavior is injected into the base implementation.
    $catalog = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.SessionPagination.cs")
    foreach ($name in @('_sessionCatalogAcceptedReads')) {
        $field = [regex]::Matches($catalog, ('(?m)^    private [^\r\n]*\b' + $name + '\b[^\r\n]*;'))
        if ($field.Count -ne 1) { throw "Missing baseline observation field: $name" }; $members += $field[0].Value
    }
}
$indicatorSource = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.SessionStateUi.cs")
$indicatorEnum = [regex]::Matches($indicatorSource, '(?ms)^    internal enum SessionIndicatorTone\r?\n    \{.*?^    \}')
$indicatorMethod = [regex]::Matches($indicatorSource, '(?ms)^    internal static SessionIndicatorTone ClassifySessionIndicator\(.*?^    \}')
if ($indicatorEnum.Count -ne 1 -or $indicatorMethod.Count -ne 1) { throw 'Missing/ambiguous production tab classification.' }
$members += @($indicatorEnum[0].Value, $indicatorMethod[0].Value)
$generated = Join-Path $output 'PageMembers.cs'
@'
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;
using OpenCodePageSource = FluentCode.Services.Notifications.OpenCodePageSource;
using SessionControlKind = FluentCode.Services.Notifications.SessionControlKind;
using Windows.ApplicationModel.DataTransfer;
using FluentCode.Pages.OpenCode;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
namespace FluentCode.Pages;
public sealed partial class OpenCodePage {
'@ + "`n" + ($members -join "`n") + "`n}" | Set-Content -Encoding UTF8 $generated
$sources += $generated
# Source-linked native Home policy with inert control ownership/viewport/popup seams.
$homeView = [IO.File]::ReadAllText((Production-Path "$repo/src/FluentCode/Pages/OpenCode/OpenCodeHomeView.cs"))
$viewMembers = foreach ($name in @('SetSnapshot', 'RefreshCalendar', 'RefreshProjectBadges', 'RebuildProjects', 'DistinctProjects', 'ProjectName', 'ProjectItem', 'SelectedItemFor', 'RefreshSessions', 'PreserveSessionViewport', 'IsSessionFocus',
    'RefreshSessionsCore', 'HeaderItem', 'TailItem', 'SessionKey', 'ItemKey', 'SessionItem', 'UpdateSessionItem', 'CreateSessionMenu', 'GroupLabel', 'ToneLabel', 'Dot', 'Label', 'SetIdentity')) {
    $match = [regex]::Matches($homeView, ('(?m)^    (?:private|internal|public) [^\r\n=]*\b' + $name + '\('))
    if ($BaseRegression -and $match.Count -eq 0 -and $name -in @('RefreshCalendar', 'PreserveSessionViewport', 'IsSessionFocus', 'RefreshSessionsCore', 'UpdateSessionItem')) { continue }
    if ($match.Count -ne 1) { throw "Missing/ambiguous production Home view member: $name" }
    $start = $match[0].Index; $opening = $homeView.IndexOf('{', $start); $arrow = $homeView.IndexOf('=>', $start)
    $block = if ($opening -ge 0 -and ($arrow -lt 0 -or $opening -lt $arrow)) { [regex]::Match($homeView.Substring($start), '(?ms)\A.*?^    \}\r?$') }
        else { [regex]::Match($homeView.Substring($start), '(?ms)\A.*?=>[^;]*;') }
    if (!$block.Success) { throw "Unsupported production Home view member: $name" }; $block.Value
}
$viewGenerated = Join-Path $output 'HomeViewMembers.cs'
"using System; using System.Linq; using System.Collections.Generic; using FluentCode.Services.Appearance; using Microsoft.UI.Xaml; using Microsoft.UI.Xaml.Controls; using Microsoft.UI.Xaml.Controls.Primitives; using Microsoft.UI.Xaml.Automation; using Microsoft.UI.Xaml.Automation.Peers; using Microsoft.UI.Xaml.Input; using Microsoft.UI.Xaml.Media; using Windows.System; namespace FluentCode.Pages.OpenCode; public sealed partial class OpenCodeHomeView {`n" +
    ($viewMembers -join "`n") + "`n}" | Set-Content -Encoding UTF8 $viewGenerated
$sources += $viewGenerated
# Actual App background fact publisher/store ownership; unrelated App/ACK/native boundaries stay inert.
$appStateSource = [IO.File]::ReadAllText("$repo/src/FluentCode/App.OpenCode.SessionState.cs")
$appMembers = foreach ($name in @('GetOpenCodeSessionUiSnapshot', 'IsSessionUiOwner', 'BindSessionUiStore', 'PublishSessionUiChanges', 'ApplyOpenCodeSessionBackgroundState', 'ApplySessionUiViewedSession')) {
    $matches = [regex]::Matches($appStateSource, ('(?ms)^    (?:private|internal|public) [^\r\n]*\b' + $name + '\(.*?^    \}\r?$'))
    if ($matches.Count -ne 1) { throw "Missing/ambiguous production App background member: $name" }
    $matches[0].Value
}
$appEventArgs = [regex]::Matches($appStateSource, '(?ms)^public sealed class OpenCodeSessionUiStateChangedEventArgs.*?^\}')
if ($appEventArgs.Count -ne 1) { throw 'Missing/ambiguous production session UI event args.' }
$appGenerated = Join-Path $output 'App.BackgroundMembers.cs'
"using System; using FluentCode.Services.OpenCode; using FluentCode.Services.Notifications; namespace FluentCode;`n" + $appEventArgs[0].Value +
    "`npublic sealed partial class App {`n" + ($appMembers -join "`n") + "`n}" | Set-Content -Encoding UTF8 $appGenerated
$sources += $appGenerated
# The background publisher uses the monitor's production TTL; no monitor transport/native
# behavior belongs in this pagination fixture. Link the exact production field, not a copied TTL.
$executionSource = [IO.File]::ReadAllText("$repo/src/FluentCode/Services/Notifications/OpenCodeNotificationMonitor.Execution.cs")
$lifetime = [regex]::Matches($executionSource, '(?m)^    public static readonly TimeSpan ObservationLifetime[^\r\n]+')
if ($lifetime.Count -ne 1) { throw 'Missing/ambiguous production observation lifetime.' }
$lifetimeGenerated = Join-Path $output 'NotificationMonitor.Lifetime.cs'
"using System; namespace FluentCode.Services.Notifications; public sealed class OpenCodeNotificationMonitor {`n" +
    $lifetime[0].Value + "`n}" | Set-Content -Encoding UTF8 $lifetimeGenerated
$sources += $lifetimeGenerated
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
$runtime = Join-Path $output 'MessagePaginationTests.runtimeconfig.json'
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 $runtime
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
Write-Output 'Headless compilation passed (warnings treated as errors).' | Tee-Object -FilePath (Join-Path $output 'compile.log') -Append
if ($BaseRegression) {
    & $dotnet $assembly '--base-home' 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
    if ($LASTEXITCODE -ne 0) { throw "Base regression evidence failed: $LASTEXITCODE" }
    Write-Output "Evidence directory: $output"
    return
}
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
# Reproduce the counterexamples against the actual wave base, not copied old behavior or a
# compile failure. Only the seven changed Home/catalog sources are overlaid; all native seams
# and HTTP inputs are identical. Git access is read-only and no window is launched.
$baseRoot = Join-Path $output 'base-source'
$baseFiles = @('src/FluentCode/Pages/OpenCodePage.xaml.cs', 'src/FluentCode/Pages/OpenCodePage.Home.cs',
    'src/FluentCode/Pages/OpenCodePage.HomeActions.cs', 'src/FluentCode/Pages/OpenCodePage.EventSync.cs',
    'src/FluentCode/Pages/OpenCodePage.SessionPagination.cs', 'src/FluentCode/Pages/OpenCode/OpenCodeHomeView.cs',
    'src/FluentCode/Pages/OpenCode/OpenCodeHomeProjection.cs')
foreach ($relative in $baseFiles) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command git.exe).Source)
    $start.WorkingDirectory = $repo; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.ArgumentList.Add('show'); $start.ArgumentList.Add("9ef16e2:$relative")
    $process = [Diagnostics.Process]::Start($start)
    $text = $process.StandardOutput.ReadToEnd(); $errorText = $process.StandardError.ReadToEnd(); $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Cannot read wave-base source $relative : $errorText" }
    $destination = Join-Path $baseRoot $relative
    [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
    [IO.File]::WriteAllText($destination, $text, [Text.UTF8Encoding]::new($false))
    $process.Dispose()
}
& $PSCommandPath -EvidenceRoot (Join-Path $output 'base-regression') -SourceRoot $repo -BaseSourceRoot $baseRoot -BaseRegression
if ($LASTEXITCODE -ne 0) { throw "Base regression subprocess failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
