# Offline source-linked return-navigation tests. No restore, packages, live UI/service or production writes.
param([string]$EvidenceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-session-return-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
Write-Output "Evidence directory: $output"

# Fail closed on missing/duplicate names, changed indentation or unsupported method form.
# Capture complete current bodies, including multiline expression-bodied helpers, with no fallback.
function Extract-Method([string]$Source, [string]$Name) {
    $lines = $Source -split '\r?\n'
    $starts = @(for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match ('^    private [^\r\n]*\b' + [regex]::Escape($Name) + '(?:<[^>]+>)?\(')) { $i }
    })
    if ($starts.Count -ne 1) { throw "Expected one production method: $Name" }
    $start = $starts[0]
    $body = $false; $expression = $false
    for ($i = $start; $i -lt $lines.Length; $i++) {
        if ($i -gt $start -and $lines[$i] -match '^    (private|public|internal) ') { throw "Missing method boundary: $Name" }
        if (!$body -and $lines[$i] -match '=>') { $expression = $true }
        if (!$expression -and $lines[$i] -eq '    {') { $body = $true }
        if (($body -and $lines[$i] -eq '    }') -or ($expression -and $lines[$i].TrimEnd().EndsWith(';'))) {
            return ($lines[$start..$i] -join "`r`n")
        }
    }
    throw "Unsupported production method form: $Name"
}
$pagePath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.xaml.cs'
$page = [IO.File]::ReadAllText($pagePath)
$names = @('SwitchToSessionAsync', 'NavigateToSessionAsync', 'CancelSessionOperations',
    'IsRootSession', 'RebuildSessionList', 'InsertSessionItemFirst', 'SelectSessionItem',
    'IsCurrentBindingFields', 'IsCurrentBinding', 'BindingLost',
    'CreateOperationToken', 'TryStartRead', 'ProcessNavigationTargetAsync', 'LocateSessionAsync',
    'DescribeNavigationReadFailure', 'ApplyNavigationResult', 'OnPageUnloaded', 'PausePage', 'InvalidateForSourceChange', 'DisposeOwnClient')
$methods = @($names | ForEach-Object { Extract-Method $page $_ })
$builtinPath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.BuiltinActions.cs'
$methods += Extract-Method ([IO.File]::ReadAllText($builtinPath)) 'ResetAcceptedHistoryProjection'
$methods += Extract-Method $page 'OrderMessages'
$methods += Extract-Method $page 'IsScrollContextCurrent'
$sourceReady = [regex]::Match($page, '(?m)^    private bool SourceReady =>[^;]*;')
if (!$sourceReady.Success) { throw 'Missing production SourceReady property.' }
$methods += $sourceReady.Value
$header = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Notifications;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using TextBlock = AIFrontend.Pages.ReturnHostTextBlock;
namespace AIFrontend.Pages;
public sealed partial class OpenCodePage
{
'@
$extracted = Join-Path $output 'OpenCodePage.Extracted.cs'
($header + "`r`n" + ($methods -join "`r`n`r`n") + "`r`n}`r`n") | Set-Content -Encoding UTF8 $extracted

$production = @()
@('OpenCodeClient.cs', 'OpenCodeClient.Interactions.cs', 'OpenCodeClient.Agents.cs', 'OpenCodeClient.View.cs',
    'OpenCodeDesktopBridge.cs', 'OpenCodeClient.PluginRpc.cs', 'OpenCodeModelFavoritesRpc.cs',
    'OpenCodeClient.SessionInfo.cs', 'OpenCodeClient.SessionControls.cs', 'OpenCodeDtos.SessionControls.cs', 'OpenCodeClient.ImageFiles.cs', 'OpenCodeImageFile.cs', 'OpenCodeAttachment.cs', 'OpenCodeDtos.Commands.cs', 'OpenCodeClientOptions.cs', 'OpenCodeAuthenticationMode.cs', 'OpenCodeConstants.cs',
    'OpenCodeJson.cs', 'OpenCodeExceptions.cs', 'OpenCodeInstructionNotice.cs', 'OpenCodeInstructionSource.cs', 'OpenCodeClient.Projects.cs', 'OpenCodeDtos.Projects.cs', 'OpenCodeToolImageResolver.cs', 'OpenCodeManualBackgroundIndex.cs', 'OpenCodeNoticeDismissals.cs', 'OpenCodeSessionAncestry.cs',
    'OpenCodeDtos.Agents.cs', 'OpenCodeDtos.Common.cs', 'OpenCodeDtos.Forms.cs', 'OpenCodeDtos.Inbox.cs',
    'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Messages.cs', 'OpenCodeDtos.Permissions.cs', 'OpenCodeDtos.Sessions.cs', 'OpenCodeTodoReplay.cs', 'OpenCodeCapabilityStatus.cs', 'OpenCodeDtos.Capabilities.cs') | ForEach-Object {
    $production += Join-Path "$repo/src/AIFrontend/Services/OpenCode" $_
}
@('OpenCodePageSource.cs', 'OpenCodeInteractionGate.cs', 'NotificationInteractions.cs', 'NotificationArgumentCodec.cs',
    'NotificationActivationInfo.cs', 'NotificationSource.cs', 'OpenCodeExecutionProjection.cs') | ForEach-Object {
    $production += Join-Path "$repo/src/AIFrontend/Services/Notifications" $_
}
@('OpenCodePage.SessionReturn.cs', 'OpenCodePage.SubagentNavigation.cs',
    'OpenCodePage.ViewAcknowledgement.cs', 'OpenCodePage.ViewVisibility.cs', 'OpenCodePage.MarkdownImages.cs', 'OpenCodePage.MessageBinding.cs', 'OpenCodePage.MessagePagination.cs') | ForEach-Object {
    $production += Join-Path "$repo/src/AIFrontend/Pages" $_
}
@('OpenCodeConversationView.cs', 'OpenCodeConversationWindow.cs', 'OpenCodeScrollBarProjection.cs', 'OpenCodeErrorRecoveryProjection.cs', 'OpenCodeSubagentPresentation.cs', 'OpenCodeSubagentTaskTitles.cs',
    'OpenCodeBrowserToolPresentation.cs', 'OpenCodeToolRowPresentation.cs', 'OpenCodeLineDiff.cs', 'OpenCodeStepRunPresentation.cs', 'OpenCodeToolDetailBuiltinData.cs', 'OpenCodeExecuteInnerCatalog.cs', 'OpenCodeContextStatusParser.cs',
    'OpenCodeSessionTracking.cs', 'OpenCodeNotificationIntegration.cs', 'OpenCodeChildChatReadSession.cs', 'OpenCodeMarkdownImageScope.cs', 'OpenCodeInteractionUi.cs', 'OpenCodeWorkingClock.cs') | ForEach-Object {
    $production += Join-Path "$repo/src/AIFrontend/Pages/OpenCode" $_
}
# Reuse only inert WinUI/presenter definitions; neither old test runner nor old scenarios are included.
$commonHost = Join-Path $repo 'tests/opencode-instruction-notice/ConversationView.TestHost.cs'
$commonHostInput = $commonHost
$commonText = [IO.File]::ReadAllText($commonHostInput)
$anchor = '    public class FrameworkElement : UIElement'
if ([regex]::Matches($commonText, [regex]::Escape($anchor)).Count -ne 1) { throw 'Missing/ambiguous inert FrameworkElement shape.' }
$commonText = $commonText.Replace($anchor + "`n    {", $anchor + "`n    {`n        public event Action<object, SizeChangedEventArgs>? SizeChanged { add { } remove { } }")
if (!$commonText.Contains('event Action<object, SizeChangedEventArgs>')) { throw 'FrameworkElement shim failed.' }
$commonHost = Join-Path $output 'ConversationView.NativeSurface.cs'
[IO.File]::WriteAllText($commonHost, $commonText)
$tests = @($commonHost, (Join-Path $PSScriptRoot 'OpenCodePage.TestHost.cs'), (Join-Path $PSScriptRoot 'ChildChat.TestHost.cs'), (Join-Path $PSScriptRoot 'Program.cs'))
$assembly = Join-Path $output 'OpenCodeSessionReturnTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$references = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll'))
$arguments += $references | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += ($production + $tests + @($extracted)) | ForEach-Object { '"' + $_ + '"' }
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
New-Item -ItemType File -Path (Join-Path $output 'compile.log') -Force | Out-Null
& $dotnet exec $compiler "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeSessionReturnTests.runtimeconfig.json')
Write-Output "Headless compilation passed (SDK $($sdk.Name); $($methods.Count) extracted members; warnings treated as errors)."
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'test.log')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
