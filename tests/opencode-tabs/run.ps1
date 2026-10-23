param([Parameter(Mandatory=$true)][string]$EvidenceRoot,[string]$BaselineBlankFile='', [switch]$RegressionProbe)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
if(!$sdk -or !$pack){throw '.NET 10 required.'}
New-Item -ItemType Directory -Path $EvidenceRoot|Out-Null
function Extract([string]$source,[string]$name){
 $starts=[regex]::Matches($source,'(?m)^    (?:private|partial|internal) (?:(?:static|async) )*[\w<>?]+ '+[regex]::Escape($name)+'(?:<[^>]+>)?\(')
 if($starts.Count -ne 1){throw "Expected one actual member: $name"}
 $text=$source.Substring($starts[0].Index);$opening=$text.IndexOf('{');$arrow=$text.IndexOf('=>')
 $pattern=if($arrow -ge 0 -and ($opening -lt 0 -or $arrow -lt $opening)){'(?ms)\A.*?;[ \t]*\r?$'}else{'(?ms)\A.*?^    \}\r?$'}
 $m=[regex]::Match($text,$pattern);if(!$m.Success){throw "Missing member boundary: $name"};return $m.Value
}
$tabsPath="$repo/src/AIFrontend/Pages/OpenCodePage.Tabs.cs";$tabs=[IO.File]::ReadAllText($tabsPath)
$blankPath="$repo/src/AIFrontend/Pages/OpenCodePage.NewSessionTab.cs";$blank=[IO.File]::ReadAllText($blankPath)
$homePath="$repo/src/AIFrontend/Pages/OpenCodePage.Home.cs";$homeSource=[IO.File]::ReadAllText($homePath)
$pagePath="$repo/src/AIFrontend/Pages/OpenCodePage.xaml.cs";$page=[IO.File]::ReadAllText($pagePath)
$bindingPath="$repo/src/AIFrontend/Pages/OpenCodePage.MessageBinding.cs";$binding=[IO.File]::ReadAllText($bindingPath)
$members=@([regex]::Match($tabs,'(?ms)^    private sealed class SessionTabEntry\(.*?^    \}').Value)
$members+=@('InitializePinnedHomeTab','HomeTab_DragStarting','SessionTabs_TabItemsChanged','QueuePinnedHomeNormalization','SessionTabs_Loaded','TagSessionTabAddButton','FindDescendant','MirrorSessionSelection','CreateSessionTab','CreateSessionTabMenu','SelectSessionTabSilently','SessionTabs_SelectionChanged','ActivateSessionTab','SessionTabs_TabCloseRequested','CloseSessionTabActor','CloseSessionTab','CloseOtherSessionTabs','RemoveSessionTab','UpdateSessionTabsEmptyState','ActivateAdjacentTab','LocateSessionInPane')|ForEach-Object{Extract $tabs $_}
$members+=@('OpenNewSessionTab','TryGetBlankTab','EnterNewSessionLayout','ExitNewSessionLayout','OnSessionContextSelected','SelectNewSessionTabSilently','RemoveNewSessionTab','CloseNewSessionTab','DeselectSessionForNewTab','OnNativeSessionCreating','OnNativeSessionCreated','CreateSessionAndSubmitAsync')|ForEach-Object{Extract $blank $_}
$members+=Extract $homeSource 'ShowHomePage'
$members+=Extract $homeSource 'OnSessionCatalogChanged'
$members+=Extract ([IO.File]::ReadAllText("$repo/src/AIFrontend/Pages/OpenCodePage.Sidebars.cs")) 'DismissSidebarDrawers'
$members+=Extract $homeSource 'OwnsHome'
$blankChoicePath="$repo/src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs";$choices=[IO.File]::ReadAllText($blankChoicePath)
$baseline=if($BaselineBlankFile){[IO.File]::ReadAllText($BaselineBlankFile)}else{''}
$members+=@('EnsureBlankPreferences','HasBlankMetadata','OwnBlankMetadata','AcceptBlankAgentPresentation','HasBlankAgentPresentation','OwnsBlankMetadata','LoadBlankMetadataAsync','BlankSelectedAgent','ApplyBlankDefaults','ReadBlankAgentModel','IsBlankAgentSelectable','BlankStrengthText','CaptureBlankPreferences','OwnsBlankPreferences','SetBlankVariantAsync','ApplyBlankAgentColor','RefreshBlankPreferenceLabels','BlankAgentButton_Click','BlankProjectButton_Click')|ForEach-Object{if($baseline -and $_ -in @('OwnBlankMetadata','LoadBlankMetadataAsync','ApplyBlankAgentColor','RefreshBlankPreferenceLabels','BlankAgentButton_Click')){Extract $baseline $_}else{Extract $choices $_}}
$members+=@('SwitchToSessionAsync','CancelSessionOperations','IsCurrentBindingFields','IsCurrentBinding','SelectSessionItem','CreateOperationToken','OnPageUnloaded','ApplyCurrentSource','InvalidateForSourceChange','DisposeOwnClient')|ForEach-Object{Extract $page $_}
$members+=@('RaiseSessionTracked','RaiseSessionRetired')|ForEach-Object{Extract $page $_}
$builtinPath="$repo/src/AIFrontend/Pages/OpenCodePage.BuiltinActions.cs";$builtin=[IO.File]::ReadAllText($builtinPath)
$members+=Extract $builtin 'CreateNativeSessionAsync'
$ready=[regex]::Match($page,'(?m)^    private bool SourceReady =>[^;]*;');if(!$ready.Success){throw 'Missing SourceReady'};$members+=$ready.Value
$members+=[regex]::Match($binding,'(?ms)^    private sealed record MessageBinding\(.*?;').Value
$members+=@('KnownSessionDirectory','IsSameMessageBinding','BeginSessionMessageBinding')|ForEach-Object{Extract $binding $_}
$composerPath="$repo/src/AIFrontend/Pages/OpenCodePage.Composer.cs";$composer=[IO.File]::ReadAllText($composerPath)
$members+=@('OwnsComposerBinding','BindComposer','GetComposerSnapshot','OnComposerSnapshotChanged')|ForEach-Object{Extract $composer $_}
$members+=[regex]::Match($composer,'(?ms)^    private string\? ComposerBindingSessionId\r?\n.*?;').Value
$generated=Join-Path $EvidenceRoot 'ActualPageMembers.cs'
"using System; using System.Linq; using System.Globalization; using System.Text.Json; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using AIFrontend.Pages.OpenCode; using AIFrontend.Services.OpenCode; using AIFrontend.Services.Notifications; using Microsoft.UI.Dispatching; using Microsoft.UI.Xaml; using Microsoft.UI.Xaml.Controls; using Microsoft.UI.Xaml.Automation; using Microsoft.UI.Xaml.Media; using Microsoft.UI.Xaml.Shapes; using Windows.Foundation.Collections; namespace AIFrontend.Pages; public sealed partial class OpenCodePage {`n"+($members -join "`n")+"`n}"|Set-Content -Encoding UTF8 $generated
$viewPath="$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerView.cs";$view=[IO.File]::ReadAllText($viewPath)
$viewMembers=@('TryActivateDraftSlot','SuspendDraftSlotForRecovery','ResolveDraftSlotRecovery','TryPresentDraftSlotRecovery','PrepareDraftSlotPageUnload','ShowUnavailableDraftSlot','AttachmentChip')|ForEach-Object{Extract $view $_}
$removedArgs=[regex]::Match($view,'(?ms)^public sealed class OpenCodeComposerAttachmentRemovedEventArgs\(.*?^\}').Value
$viewGenerated=Join-Path $EvidenceRoot 'ActualViewBridge.cs'
"using System; using Microsoft.UI.Xaml; using Microsoft.UI.Xaml.Controls; using Microsoft.UI.Xaml.Automation; namespace AIFrontend.Pages.OpenCode;`n"+$removedArgs+"`ninternal sealed partial class OpenCodeComposerView {`n"+($viewMembers -join "`n")+"`n}"|Set-Content -Encoding UTF8 $viewGenerated
$sources=@($generated,$viewGenerated,"$PSScriptRoot/Tab.TestHost.cs","$PSScriptRoot/PageFixture.cs","$PSScriptRoot/ViewFixture.cs","$PSScriptRoot/Program.cs","$PSScriptRoot/WorkingClockTests.cs","$repo/src/AIFrontend/Pages/OpenCode/OpenCodeWorkingClock.cs","$repo/src/AIFrontend/Pages/OpenCodePage.DraftSlots.cs","$repo/src/AIFrontend/Pages/OpenCode/OpenCodeSessionTracking.cs","$repo/src/AIFrontend/Pages/OpenCode/OpenCodeHomeProjection.cs")
$sources+="$PSScriptRoot/PageSlots.cs"
$sources+="$PSScriptRoot/RetirementTests.cs"
$sources+="$repo/src/AIFrontend/Pages/OpenCode/OpenCodeSidebarLayout.cs"
$sources+="$repo/src/AIFrontend/Pages/OpenCode/OpenCodeAgentPresentation.cs"
$sources+="$repo/src/AIFrontend/Pages/OpenCode/OpenCodeAgentIcons.cs"
$sources+=@("$repo/src/AIFrontend/Pages/OpenCode/OpenCodeDirectoryPresentation.cs","$PSScriptRoot/DirectorySelectorTests.cs")
$sources+="$PSScriptRoot/AgentPresentation.cs"
$selectorPath="$repo/src/AIFrontend/Pages/OpenCode/OpenCodeAgentSelector.cs";$selector=[IO.File]::ReadAllText($selectorPath)
$pickerPath="$repo/src/AIFrontend/Pages/OpenCode/OpenCodeAgentPickerDialog.cs";$picker=[IO.File]::ReadAllText($pickerPath)
$normal=Join-Path $EvidenceRoot 'ActualAgentPresentation.cs'
$selectorMembers=@('ApplyAgents','ApplyCurrentAgentPresentation','DisplayName','Configure','Clear','InvalidateContext','IsOriginCurrent','ResetUi','SelectPendingAgent','SwitchBlockedReason')|ForEach-Object{Extract $selector $_}
$rowRecord=[regex]::Match($picker,'(?m)^internal sealed record OpenCodeAgentPickerRow.*;').Value
"using System;using System.Linq;using System.Collections.Generic;using System.Threading;using AIFrontend.Services.OpenCode;using AIFrontend.Services.Notifications;using Microsoft.UI.Xaml;using Microsoft.UI.Xaml.Controls;using Microsoft.UI.Xaml.Automation;namespace AIFrontend.Pages.OpenCode;`n"+$rowRecord+"`ninternal sealed partial class OpenCodeAgentSelector {`n"+($selectorMembers -join "`n")+"`n} internal static partial class OpenCodeAgentPickerDialog {`n"+(Extract $picker 'RowItem')+"`n}"|Set-Content -Encoding UTF8 $normal
$sources+=@($normal,"$PSScriptRoot/NormalAgentPresentation.cs","$repo/src/AIFrontend/Services/Notifications/AgentSwitchCoordinator.cs")
$sources+=@(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs"|Where-Object Name -NE 'OpenCodePairingService.cs'|ForEach-Object FullName)
$sources+=@('OpenCodeComposerContracts.cs','OpenCodeComposerController.cs','OpenCodeComposerController.Continuation.cs','OpenCodeComposerController.Slots.cs','OpenCodeComposerEditorSync.cs','OpenCodeComposerInputEvidence.cs','OpenCodeComposerViewLifetime.cs','OpenCodeComposerAttachmentLoader.cs','OpenCodeComposerInputPolicy.cs','OpenCodeSessionControlCoordinator.cs','OpenCodeModelPresentation.cs','OpenCodeComposerNativeDialogModels.cs')|ForEach-Object{"$repo/src/AIFrontend/Pages/OpenCode/$_"}
$sources+=@('OpenCodePageSource.cs','OpenCodeInteractionGate.cs','NotificationInteractions.cs','NotificationArgumentCodec.cs','NotificationActivationInfo.cs','NotificationSource.cs','OpenCodeExecutionProjection.cs')|ForEach-Object{"$repo/src/AIFrontend/Services/Notifications/$_"}
$dll=Join-Path $EvidenceRoot 'TabsTests.dll';$rsp=Join-Path $EvidenceRoot 'compile.rsp'
$refs=@(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object FullName)
$args2=@('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$args2+=$refs|ForEach-Object{'/reference:"'+$_+'"'};$args2+=$sources|ForEach-Object{'"'+$_+'"'};$args2|Set-Content -Encoding UTF8 $rsp
$compiler=Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$env:AIF_R1_BASELINE_PROBE=if($RegressionProbe){'1'}else{''}
& $dotnet exec $compiler "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt');if($LASTEXITCODE -ne 0){throw "Tabs focused compile failed: $LASTEXITCODE"}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'TabsTests.runtimeconfig.json')
& $dotnet $dll $repo 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt');if($LASTEXITCODE -ne 0){throw "Tabs focused tests failed: $LASTEXITCODE"}
Write-Output "PASS source-linked tabs/Switch/binding; evidence $EvidenceRoot"
