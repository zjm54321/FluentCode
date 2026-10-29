param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
New-Item -ItemType Directory -Force $EvidenceRoot | Out-Null
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$refs = @(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll")
$folder = "$repo/src/AIFrontend/Pages/OpenCode"
$models = @("$folder/OpenCodePersistentPtyPresentation.cs", "$folder/OpenCodePluginActionsPresentation.cs")
function Compile([string]$name, [string]$kind, $references, $sources, [string]$defines = '') {
    $dll = Join-Path $EvidenceRoot "$name.dll"
    $arguments = @('/nologo', "/target:$kind", '/langversion:latest', '/nullable:enable', '/warnaserror+', '/nowarn:CS1701,CS1702', "/out:`"$dll`"")
    if ($defines) { $arguments += "/define:$defines" }
    $arguments += $references | ForEach-Object { '/reference:"' + $_.FullName + '"' }
    $arguments += $sources | ForEach-Object { '"' + $_ + '"' }
    $rsp = Join-Path $EvidenceRoot "$name.rsp"; $arguments | Set-Content -Encoding UTF8 $rsp
    & $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp" 2>&1 | Tee-Object (Join-Path $EvidenceRoot "$name-compile.log")
    if ($LASTEXITCODE -ne 0) { throw "$name compile failed: $LASTEXITCODE" }
}
Compile 'PtyPresentationTests' 'exe' $refs ($models + @("$PSScriptRoot/Program.cs"))
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'PtyPresentationTests.runtimeconfig.json')
& $dotnet (Join-Path $EvidenceRoot 'PtyPresentationTests.dll') $repo | Tee-Object (Join-Path $EvidenceRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Presentation tests failed: $LASTEXITCODE" }
$nuget = Join-Path $env:USERPROFILE '.nuget/packages'
$native = @(Get-Item "$nuget/microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/Microsoft.Windows.SDK.NET.dll", "$nuget/microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/WinRT.Runtime.dll")
$native += Get-ChildItem "$nuget/microsoft.windowsappsdk.winui/1.8.260803003/lib/net6.0-windows10.0.17763.0/*.dll"
$native += Get-ChildItem "$nuget/microsoft.windowsappsdk.foundation/1.8.260803002/lib/net6.0-windows10.0.17763.0/*.dll"
$native += Get-ChildItem "$nuget/microsoft.windowsappsdk.interactiveexperiences/1.8.260708001/lib/net6.0-windows10.0.18362.0/*.dll"
$native += Get-Item "$nuget/microsoft.web.webview2/1.0.3179.45/lib_manual/net8.0-windows10.0.17763.0/Microsoft.Web.WebView2.Core.Projection.dll"
Compile 'PtyNativeViewCheck' 'library' ($refs + $native) ($models + @("$folder/OpenCodePersistentPtySurface.cs", "$folder/OpenCodePersistentPtyRenderer.cs", "$folder/OpenCodePersistentPtyView.cs",
    "$folder/OpenCodePersistentPtyInventoryPresentation.cs", "$folder/OpenCodePersistentPtyInventoryView.cs", "$repo/src/AIFrontend/Services/OpenCode/PersistentPtyModels.cs", "$folder/OpenCodePluginActionsView.cs", "$PSScriptRoot/NativeHost.cs"))
Write-Output 'PASS actual WinUI/WebView2 view API reference compile; not a native window/rendering test.'

# Production Page binding + real mutation transport/coordinator/gate + actual insights read.
# UI/history/OS-launch boundaries are inert, explicitly defined by PluginPage.TestHost.cs.
$serviceSources = @(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" | Where-Object { $_.Name -ne 'OpenCodePairingService.cs' } | ForEach-Object FullName)
$serviceSources += Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/PluginMutations/*.cs" | ForEach-Object FullName
$serviceSources += Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/PersistentPty/*.cs" | ForEach-Object FullName
$serviceSources += Get-ChildItem "$repo/src/AIFrontend/Services/Billion/*.cs" | ForEach-Object FullName
foreach ($name in @('OpenCodeInteractionGate.cs','OpenCodeExecutionProjection.cs','OpenCodePageSource.cs','NotificationInteractions.cs','NotificationArgumentCodec.cs','NotificationActivationInfo.cs','NotificationSource.cs')) {
    $serviceSources += "$repo/src/AIFrontend/Services/Notifications/$name"
}
$historySource = [IO.File]::ReadAllText("$repo/src/AIFrontend/Pages/OpenCodePage.BuiltinActions.cs")
$historyMethod = [regex]::Match($historySource, '(?ms)^    private void ResetAcceptedHistoryProjection\(.*?^    \}')
if (!$historyMethod.Success) { throw 'Missing actual accepted-history reset boundary.' }
$historyGenerated = Join-Path $EvidenceRoot 'PluginHistoryBoundary.cs'
$statusSource = [IO.File]::ReadAllText("$repo/src/AIFrontend/Pages/OpenCodePage.EventSync.cs")
$statusMethod = [regex]::Match($statusSource, '(?ms)^    private async Task RefreshListedSessionStatusesAsync\(.*?^    \}')
if (!$statusMethod.Success) { throw 'Missing actual execution-status refresh boundary.' }
"using System; using System.Linq; using System.Threading; using System.Threading.Tasks; using AIFrontend.Services.OpenCode; namespace AIFrontend.Pages; public sealed partial class OpenCodePage {`n" + $historyMethod.Value + "`n" + $statusMethod.Value + "`n}" | Set-Content -Encoding UTF8 $historyGenerated
$conversationSource = [IO.File]::ReadAllText("$folder/OpenCodeConversationView.cs")
$readingRecord = [regex]::Match($conversationSource, '(?m)^    internal sealed record ReadingPosition[^\r\n]*').Value
$readingCapture = [regex]::Match($conversationSource, '(?ms)^    internal ReadingPosition CaptureReadingPosition\(\).*?;').Value
$readingRestore = [regex]::Match($conversationSource, '(?ms)^    internal bool RestoreReadingPosition\(.*?^    \}').Value
$readingInput = [regex]::Match($conversationSource, '(?m)^    private void WindowPointerInput[^\r\n]*').Value
if (!$readingRecord -or !$readingCapture -or !$readingRestore -or !$readingInput) { throw 'Missing production reading-position ownership members.' }
$readingGenerated = Join-Path $EvidenceRoot 'PluginReadingBoundary.cs'
"using System; using Microsoft.UI.Xaml.Controls; using Microsoft.UI.Xaml.Input; namespace AIFrontend.Pages.OpenCode; internal sealed partial class OpenCodeConversationView {`n" + $readingRecord + "`n" + $readingCapture + "`n" + $readingRestore + "`n" + $readingInput + "`n}" | Set-Content -Encoding UTF8 $readingGenerated
$bindingSources = $serviceSources + @("$folder/OpenCodePluginActionsPresentation.cs", "$folder/OpenCodePluginMutationPresentation.cs", "$folder/OpenCodeBillionPresentation.cs",
    "$folder/OpenCodeSessionInsights.cs", "$repo/src/AIFrontend/Pages/OpenCodePage.PluginActions.cs", "$repo/src/AIFrontend/Pages/OpenCodePage.BillionDashboard.cs",
    "$repo/src/AIFrontend/Pages/OpenCodePage.PersistentPty.cs", "$folder/OpenCodePersistentPtyPresentation.cs", "$folder/OpenCodePersistentPtySurface.cs",
    "$folder/OpenCodePersistentPtyRendererAdapter.cs", "$folder/OpenCodePersistentPtyInventory.cs", "$folder/OpenCodePersistentPtyInventoryPresentation.cs",
    "$PSScriptRoot/PersistentPtyPage.TestHost.cs", "$PSScriptRoot/PersistentPtyBindingTests.cs",
    "$PSScriptRoot/PluginPage.TestHost.cs", "$PSScriptRoot/PluginBindingTests.cs", "$folder/OpenCodeConversationWindow.cs", $historyGenerated, $readingGenerated)
$recordedInputs = @($bindingSources + $models + @("$folder/OpenCodePluginActionsView.cs", "$folder/OpenCodeSessionInsightsView.cs",
    "$repo/src/AIFrontend/Pages/OpenCodePage.SessionInsights.cs", "$repo/src/AIFrontend/Pages/OpenCodePage.RightPanelMetadata.cs",
    "$repo/src/AIFrontend/Pages/OpenCodePage.BuiltinActions.cs", "$repo/src/AIFrontend/Pages/OpenCodePage.EventSync.cs",
    "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeConversationView.cs", "$repo/src/AIFrontend/Pages/OpenCodePage.AgentTree.cs", "$repo/src/AIFrontend/Pages/OpenCodePage.TerminalView.cs",
    "$repo/src/AIFrontend/Pages/OpenCodePage.xaml.cs", "$PSScriptRoot/NativeHost.cs", "$PSScriptRoot/run.ps1"))
$recordedInputs += @(Get-ChildItem "$folder/OpenCodePersistentPty*.cs" | ForEach-Object FullName)
$recordedInputs += @(Get-ChildItem "$repo/src/AIFrontend/Assets/OpenCode/PersistentPty/*" -File | ForEach-Object FullName)
$recordedInputs = $recordedInputs | Sort-Object -Unique
$inputHashes = @{}
foreach ($input in $recordedInputs) { $inputHashes[$input] = (Get-FileHash -Algorithm SHA256 $input).Hash }
Compile 'PluginBindingTests' 'exe' $refs $bindingSources
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'PluginBindingTests.runtimeconfig.json')
& $dotnet (Join-Path $EvidenceRoot 'PluginBindingTests.dll') | Tee-Object (Join-Path $EvidenceRoot 'binding-results.txt')
if ($LASTEXITCODE -ne 0) { throw "Plugin/Billion Page binding tests failed: $LASTEXITCODE" }
$bindingNative = @($bindingSources | Where-Object { $_ -notlike '*PluginBindingTests.cs' -and $_ -notlike '*PersistentPtyBindingTests.cs' }) + @("$folder/OpenCodePluginActionsView.cs",
    "$folder/OpenCodePersistentPtyRenderer.cs", "$folder/OpenCodePersistentPtyView.cs", "$folder/OpenCodePersistentPtyInventoryView.cs", "$PSScriptRoot/NativeHost.cs")
Compile 'PluginBindingNativeCheck' 'library' ($refs + $native) $bindingNative 'NATIVE_UI'
foreach ($input in $recordedInputs) {
    if ((Get-FileHash -Algorithm SHA256 $input).Hash -ne $inputHashes[$input]) { throw "Compiled/binding input changed during focused validation: $input" }
}
$inputHashes | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'binding-input-hashes.json')
Write-Output 'PASS actual Page/plugin-action/dialog/OS-launch binding native reference compile; unrelated page/history fields shimmed, no native window executed.'
Write-Output "Evidence directory: $EvidenceRoot"
