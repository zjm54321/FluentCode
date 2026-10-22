# Offline page-partial integration: real composer orchestration/core/transport/gate/picker and
# verbatim selected page handlers; WinUI + preexisting page services are explicit test doubles.
# No project build/restore, native UI, service or model calls.
param([string]$EvidenceRoot = '', [switch]$NativeOnly, [string]$SourceRoot = '')
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
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-composer-hookup-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
$env:TEMP = Join-Path $output 'temp'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP | Out-Null
$assembly = Join-Path $output 'OpenCodeComposerHookupTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$sources = @(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs')
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerCharTriggers.cs"
foreach ($name in @('OpenCodeComposerContracts.cs', 'OpenCodeComposerController.cs', 'OpenCodeComposerController.Continuation.cs', 'OpenCodeComposerController.Slots.cs', 'OpenCodeComposerInputEvidence.cs', 'OpenCodeComposerContinuationModels.cs', 'OpenCodeComposerAttachmentLoader.cs', 'OpenCodeComposerInputPolicy.cs', 'OpenCodeComposerFilePicker.cs', 'OpenCodeComposerEditorSync.cs', 'OpenCodeComposerViewLifetime.cs', 'OpenCodeComposerCommandMenu.cs', 'OpenCodeComposerNativeDialogModels.cs', 'OpenCodeSessionControlCoordinator.cs')) {
    $sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/$name"
}
foreach ($name in @('OpenCodeInteractionGate.cs', 'OpenCodePageSource.cs', 'NotificationInteractions.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs', 'OpenCodeExecutionProjection.cs')) {
    $sources += Get-Item "$repo/src/AIFrontend/Services/Notifications/$name"
}
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCodePage.Composer.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCodePage.SideQuestion.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeSideQuestion.cs"
$sources += Get-Item (Join-Path $PSScriptRoot 'SideQuestions.cs')
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCodePage.DraftSlots.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCodePage.SteerChat.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCodePage.SessionActivityIntegration.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerActivityPolicy.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeHomeProjection.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeModelPresentation.cs"
$sources += Get-Item (Join-Path $PSScriptRoot 'ComposerActivity.cs')
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCodePage.AgentIntegration.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCodePage.BuiltinActions.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCodePage.MessagePagination.cs"
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCodePage.MessageBinding.cs"
$sources += Get-Item (Join-Path $PSScriptRoot 'Program.cs')
$sources += Get-Item (Join-Path $PSScriptRoot 'NativeActions.cs')
$sources += Get-Item (Join-Path $PSScriptRoot 'ContinuationActions.cs')

# Extract complete production members, never a copied implementation. The fixed four-space class
# indentation makes the end of a block/arrow member explicit; fail if any member is missing/ambiguous.
$pagePath = "$repo/src/AIFrontend/Pages/OpenCodePage.xaml.cs"
$page = [IO.File]::ReadAllText($pagePath)
$names = @('SourceReady', 'IsCurrentBindingFields', 'IsCurrentBinding', 'CreateOperationToken', 'TryStartRead', 'EnterGateAsync',
    'SendStopButton_Click', 'ShouldStopSelectedSession', 'TryHandleComposerEnter', 'Composer_KeyDown',
    'Composer_PreviewKeyDown', 'Composer_KeyUp', 'Composer_LostFocus', 'Composer_TextChanged', 'UpdateComposerButtons',
    'InterruptSelectedSessionAsync', 'NewSessionButton_Click', 'OrderMessages', 'ObserveExecutionState',
    'SettleInteraction', 'HandleProtectedPermissionAtSubmit', 'HandleProtectedFormAtSubmit', 'ApplyNavigationResult')
$members = @()
foreach ($name in $names) {
    $matches = [regex]::Matches($page, ('(?m)^    private [^\r\n]*\b' + [regex]::Escape($name) + '(?:<T>)?(?:\(| =>)'))
    if ($matches.Count -ne 1) { throw "Expected one production member: $name" }
    $start = $matches[0].Index
    $block = [regex]::Match($page.Substring($start), '(?ms)\A.*?(?:^    \}\r?$|=>[^;]*;)')
    # A lambda inside a block is not a member end. Braced methods terminate at class indentation.
    $opening = $page.IndexOf('{', $start)
    $arrow = $page.IndexOf('=>', $start)
    if ($opening -ge 0 -and ($arrow -lt 0 -or $opening -lt $arrow)) {
        $block = [regex]::Match($page.Substring($start), '(?ms)\A.*?^    \}\r?$')
    }
    if (!$block.Success) { throw "Could not extract production member: $name" }
    $members += $block.Value
}
$generated = Join-Path $output 'PageMembers.cs'
@'
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
namespace AIFrontend.Pages;
public sealed partial class OpenCodePage {
'@ + "`n" + ($members -join "`n") + "`n}" | Set-Content -Encoding UTF8 $generated
$sources += Get-Item $generated
# Actual blank-tab creation/handoff methods; UI list/layout members remain boundaries.
$blankPath = "$repo/src/AIFrontend/Pages/OpenCodePage.NewSessionTab.cs"
$blank = [IO.File]::ReadAllText($blankPath)
$blankMembers = foreach ($name in @('OpenNewSessionTab', 'TryGetBlankTab', 'RemoveNewSessionTab', 'CreateSessionAndSubmitAsync', 'OnNativeSessionCreating', 'OnNativeSessionCreated')) {
    $m = [regex]::Matches($blank, ('(?m)^    (?:private|partial) [^\r\n]*\b' + $name + '\('))
    if ($m.Count -ne 1) { throw "Expected one production blank-tab member: $name" }
    $block = [regex]::Match($blank.Substring($m[0].Index), '(?ms)\A.*?^    \}\r?$')
    if (!$block.Success) { throw "Cannot extract blank-tab member: $name" }
    $block.Value
}
$blankGenerated = Join-Path $output 'BlankSessionMembers.cs'
$blankChoicePath = "$repo/src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs"
$blankChoice = [IO.File]::ReadAllText($blankChoicePath)
foreach ($name in @('EnsureBlankPreferences', 'HasBlankMetadata', 'CaptureBlankPreferences', 'OwnsBlankPreferences', 'BlankSelectedAgent', 'IsBlankAgentSelectable', 'ApplyBlankDefaults', 'ReadBlankAgentModel', 'BlankAgentName', 'BlankStrengthText')) {
    $m = [regex]::Match($blankChoice, ('(?m)^    private [^\r\n]*\b' + $name + '\('))
    if (!$m.Success) { throw "Missing production blank preference member: $name" }
    $block = if ($name -in @('HasBlankMetadata', 'OwnsBlankPreferences', 'IsBlankAgentSelectable', 'BlankAgentName', 'BlankStrengthText')) { [regex]::Match($blankChoice.Substring($m.Index), '(?ms)\A.*?;') }
        else { [regex]::Match($blankChoice.Substring($m.Index), '(?ms)\A.*?^    \}\r?$') }
    $blankMembers += $block.Value
}
"using System; using System.Linq; using System.Globalization; using System.Text.Json; using System.Threading.Tasks; using AIFrontend.Services.OpenCode; using AIFrontend.Pages.OpenCode; using Microsoft.UI.Xaml.Controls; using Microsoft.UI.Xaml.Automation; namespace AIFrontend.Pages; public sealed partial class OpenCodePage {`n" + ($blankMembers -join "`n") + "`n}" | Set-Content -Encoding UTF8 $blankGenerated
$sources += Get-Item $blankGenerated
$overlayPath = "$repo/src/AIFrontend/Pages/OpenCodePage.ComposerOverlays.cs"
$overlay = [IO.File]::ReadAllText($overlayPath)
$activityMembers = foreach ($name in @('IsComposerSessionWorking', 'RefreshComposerSessionActivity')) {
    $pattern = '(?ms)^    private [^\r\n]*\b' + $name + '\([^\r\n]*\).*?;'
    $m = [regex]::Match($overlay, $pattern)
    if (!$m.Success) { throw "Missing production composer activity member: $name" }
    $m.Value
}
$activityGenerated = Join-Path $output 'ComposerActivityMembers.cs'
"using System; using AIFrontend.Pages.OpenCode; namespace AIFrontend.Pages; public sealed partial class OpenCodePage {`n" + ($activityMembers -join "`n") + "`n}" | Set-Content -Encoding UTF8 $activityGenerated
$sources += Get-Item $activityGenerated
$layoutPath = "$repo/src/AIFrontend/Pages/OpenCodePage.ComposerLayout.cs"
$layout = [IO.File]::ReadAllText($layoutPath)
$layoutMembers = foreach ($name in @('UpdateComposerSecondaryActions', 'ComposerQueueButton_Click', 'ComposerExpandButton_Click')) {
    $m = [regex]::Match($layout, ('(?ms)^    private [^\r\n]*\b' + $name + '\([^\r\n]*\).*?^    \}\r?$'))
    if (!$m.Success) { throw "Missing production composer layout action: $name" }
    $m.Value
}
$deliveryPath = "$repo/src/AIFrontend/Pages/OpenCodePage.ComposerDelivery.cs"
$delivery = [IO.File]::ReadAllText($deliveryPath)
$deliveryMembers = @([regex]::Match($delivery, '(?m)^    private bool _composerDeliverySupported;').Value,
    [regex]::Match($delivery, '(?m)^    internal string\? RequestedComposerDelivery =>[^;]*;').Value,
    [regex]::Match($delivery, '(?ms)^    internal void ConfigureComposerDelivery\(bool supported\).*?^    \}\r?$').Value)
if ($deliveryMembers | Where-Object { !$_ }) { throw 'Missing actual composer delivery configuration member.' }
$layoutGenerated = Join-Path $output 'ComposerLayoutActions.cs'
"using AIFrontend.Pages.OpenCode; using AIFrontend.Services.OpenCode; using Microsoft.UI.Xaml; namespace AIFrontend.Pages; public sealed partial class OpenCodePage {`n" + (($layoutMembers + $deliveryMembers) -join "`n") + "`n}" | Set-Content -Encoding UTF8 $layoutGenerated
$sources += Get-Item $layoutGenerated
$focusMember = [regex]::Match($overlay, '(?ms)^    private int SyncPermissionOverlay\(\).*?^    \}\r?$')
if (!$focusMember.Success) { throw 'Missing production SyncPermissionOverlay member.' }
$focusGenerated = Join-Path $output 'PermissionFocusMember.cs'
"using System.Linq; using AIFrontend.Pages.OpenCode; using Microsoft.UI.Xaml; namespace AIFrontend.Pages; public sealed partial class OpenCodePage {`n" + $focusMember.Value + "`n}" | Set-Content -Encoding UTF8 $focusGenerated
$sources += Get-Item $focusGenerated
$sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeSessionTracking.cs"
$arguments += $sources | ForEach-Object { '"' + $_.FullName + '"' }
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
$runtime = Join-Path $output 'OpenCodeComposerHookupTests.runtimeconfig.json'
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 $runtime
# Preserve actual compile/extraction membership and source hashes before compilation, including untracked inputs.
$guardSources = @("$repo/src/AIFrontend/Pages/OpenCodePage.SessionInsights.cs", "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerView.cs", $layoutPath, $deliveryPath, "$repo/src/AIFrontend/Pages/OpenCodePage.xaml", "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerNativeDialogs.cs")
$references = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll')).FullName
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$inputs = @($sources.FullName + $pagePath + $blankPath + $overlayPath + $PSCommandPath + $guardSources + $references + @($dotnet, $compiler, $rsp, $runtime)) | Sort-Object -Unique
$inputs | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes-before.json')
@{ CompiledSources = @($sources.FullName); ExtractedFrom = $pagePath; ExtractedMembers = $names; StructuralOnly = $guardSources; References = $references; Compiler = $compiler; Host = $dotnet; Rsp = $rsp; RuntimeConfig = $runtime; NativeOnly = [bool]$NativeOnly } | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $output 'input-membership.json')
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
# Tee-Object creates no file for a silent successful compiler. Establish an empty capture first.
New-Item -ItemType File -Path (Join-Path $output 'compile.log') -Force | Out-Null
& $dotnet exec $compiler "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Page-partial compile failed: $LASTEXITCODE" }
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& $dotnet $assembly $repo ([string]$NativeOnly) | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Page-partial tests failed: $LASTEXITCODE" }
$inputs | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes.json')
if ([IO.File]::ReadAllText((Join-Path $output 'source-hashes-before.json')) -ne [IO.File]::ReadAllText((Join-Path $output 'source-hashes.json'))) { throw 'Page-partial inputs changed during validation.' }
Get-FileHash -LiteralPath $assembly, $rsp, (Join-Path $output 'compile.log'), (Join-Path $output 'results.txt'), (Join-Path $output 'input-membership.json') -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'artifact-hashes.json')
Write-Output "Evidence directory: $output"
