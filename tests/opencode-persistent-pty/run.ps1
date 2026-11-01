param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
New-Item -ItemType Directory -Force $EvidenceRoot | Out-Null
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$refs = @(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll")
$folder = "$repo/src/FluentCode/Pages/OpenCode"
$models = @("$folder/OpenCodePersistentPtyPresentation.cs")
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
Compile 'PtyPresentationTests' 'exe' $refs ($models + @("$folder/OpenCodePersistentPtyVtStream.cs", "$PSScriptRoot/Program.cs"))
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'PtyPresentationTests.runtimeconfig.json')
& $dotnet (Join-Path $EvidenceRoot 'PtyPresentationTests.dll') $repo | Tee-Object (Join-Path $EvidenceRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Presentation tests failed: $LASTEXITCODE" }
$nuget = Join-Path $env:USERPROFILE '.nuget/packages'
$native = @(Get-Item "$nuget/microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/Microsoft.Windows.SDK.NET.dll", "$nuget/microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/WinRT.Runtime.dll")
$native += Get-ChildItem "$nuget/microsoft.windowsappsdk.winui/1.8.260803003/lib/net6.0-windows10.0.17763.0/*.dll"
$native += Get-ChildItem "$nuget/microsoft.windowsappsdk.foundation/1.8.260803002/lib/net6.0-windows10.0.17763.0/*.dll"
$native += Get-ChildItem "$nuget/microsoft.windowsappsdk.interactiveexperiences/1.8.260708001/lib/net6.0-windows10.0.18362.0/*.dll"
$library = Join-Path $EvidenceRoot 'native-library'
& (Join-Path $PSScriptRoot 'BuildNativeTerminal.ps1') -EvidenceRoot $library
if ($LASTEXITCODE -ne 0) { throw 'Native terminal dependency build failed' }
$native += Get-Item "$library/bin/WindowsTerminalControl.WinUI.dll", "$library/bin/WindowsTerminalControl.Core.dll"
$native += Get-Item "$nuget/ci.microsoft.terminal.winui3.unofficial/1.0.20-beta.1/lib/net8.0-windows10.0.19041/Microsoft.Terminal.WinUI3.dll"
$native += Get-ChildItem "$nuget/winuiex/2.5.1/lib" -Filter WinUIEx.dll -Recurse | Select-Object -First 1
$native = @($native | Where-Object Name -NE 'WinRT.Runtime.dll')
$native += Get-Item "$nuget/microsoft.windows.cswinrt/2.3.1/lib/net9.0/WinRT.Runtime.dll"
$rendererSources = @("$folder/OpenCodePersistentPtySession.cs", "$folder/OpenCodePersistentPtyVtStream.cs", "$folder/OpenCodePersistentPtyAirspace.cs")
# Shared Fluent motion layer used by the card / ☰ list / chips (production files, compiled as-is).
$rendererSources += @("$repo/src/FluentCode/Motion/FluentMotion.cs", "$repo/src/FluentCode/Motion/FluentMotionPolicy.cs", "$repo/src/FluentCode/Motion/ActionFeedback.cs", "$repo/src/FluentCode/Motion/OverlayCardMotion.cs", "$repo/src/FluentCode/Services/Appearance/ThemeResources.cs")
Compile 'PtyNativeViewCheck' 'library' ($refs + $native) ($models + @("$folder/OpenCodePersistentPtySurface.cs", "$folder/OpenCodePersistentPtyRenderer.cs", "$folder/OpenCodePersistentPtyView.cs",
    "$folder/OpenCodePersistentPtyInventoryPresentation.cs", "$folder/OpenCodePersistentPtyInventoryView.cs", "$folder/OpenCodeQuickAccessStyle.cs", "$folder/OpenCodePersistentPtyOverlay.cs", "$folder/OpenCodeBrowserOverlayLayout.cs",
    "$repo/src/FluentCode/Services/OpenCode/PersistentPtyModels.cs", "$PSScriptRoot/NativeHost.cs") + $rendererSources)
Write-Output 'PASS actual WinUI/native terminal view API reference compile; no native runtime window executed.'
Compile 'PtyOverlayGlueNativeCheck' 'library' ($refs + $native) ($models + @("$folder/OpenCodePersistentPtySurface.cs", "$folder/OpenCodePersistentPtyRenderer.cs", "$folder/OpenCodePersistentPtyView.cs",
    "$folder/OpenCodePersistentPtyInventoryPresentation.cs", "$folder/OpenCodePersistentPtyInventoryView.cs", "$folder/OpenCodeQuickAccessStyle.cs", "$folder/OpenCodePersistentPtyOverlay.cs", "$folder/OpenCodeBrowserOverlayLayout.cs",
    "$repo/src/FluentCode/Services/OpenCode/PersistentPtyModels.cs", "$repo/src/FluentCode/Pages/OpenCodePage.PersistentPtyOverlay.cs", "$PSScriptRoot/PtyOverlayGlue.TestHost.cs", "$PSScriptRoot/NativeHost.cs") + $rendererSources)
Write-Output 'PASS actual right-card/floating-card page glue reference compile against WinUI refs (page members shimmed).'

# Production Billion/PTY bindings; the retired preset/clear action UI is intentionally absent.
# Native view/dialog/OS-launch boundaries are inert, explicitly defined by BillionPtyPage.TestHost.cs.
$serviceSources = @(Get-ChildItem "$repo/src/FluentCode/Services/OpenCode/*.cs" | Where-Object { $_.Name -ne 'OpenCodePairingService.cs' } | ForEach-Object FullName)
$serviceSources += Get-ChildItem "$repo/src/FluentCode/Services/OpenCode/PluginMutations/*.cs" | ForEach-Object FullName
$serviceSources += Get-ChildItem "$repo/src/FluentCode/Services/OpenCode/PersistentPty/*.cs" | ForEach-Object FullName
$serviceSources += Get-ChildItem "$repo/src/FluentCode/Services/Billion/*.cs" | ForEach-Object FullName
foreach ($name in @('OpenCodeInteractionGate.cs','OpenCodeExecutionProjection.cs','OpenCodePageSource.cs','NotificationInteractions.cs','NotificationArgumentCodec.cs','NotificationActivationInfo.cs','NotificationSource.cs')) {
    $serviceSources += "$repo/src/FluentCode/Services/Notifications/$name"
}
$bindingSources = $serviceSources + @("$folder/OpenCodeBillionPresentation.cs",
    "$folder/OpenCodeSessionInsights.cs", "$repo/src/FluentCode/Pages/OpenCodePage.BillionDashboard.cs",
    "$repo/src/FluentCode/Pages/OpenCodePage.PersistentPty.cs", "$folder/OpenCodePersistentPtyPresentation.cs", "$folder/OpenCodePersistentPtySurface.cs",
    "$folder/OpenCodePersistentPtyRendererAdapter.cs", "$folder/OpenCodePersistentPtyInventory.cs", "$folder/OpenCodePersistentPtyInventoryPresentation.cs",
    "$folder/OpenCodeShellTerminalText.cs", "$folder/OpenCodeShellTerminalAdapter.cs", "$folder/OpenCodeShellTerminalState.cs", "$folder/OpenCodeShellTerminalReader.cs", "$folder/OpenCodeAgentTreeProjection.cs",
    "$PSScriptRoot/ShellTerminalTests.cs",
    "$PSScriptRoot/PersistentPtyPage.TestHost.cs", "$PSScriptRoot/PersistentPtyBindingTests.cs",
    "$PSScriptRoot/BillionPtyPage.TestHost.cs", "$PSScriptRoot/BillionBindingTests.cs")
$recordedInputs = @($bindingSources + $models + @("$folder/OpenCodeSessionInsightsView.cs",
    "$repo/src/FluentCode/Pages/OpenCodePage.SessionInsights.cs", "$repo/src/FluentCode/Pages/OpenCodePage.RightPanelMetadata.cs",
    "$repo/src/FluentCode/Pages/OpenCodePage.BuiltinActions.cs",
    "$repo/src/FluentCode/Pages/OpenCodePage.AgentTree.cs", "$repo/src/FluentCode/Pages/OpenCodePage.TerminalView.cs",
    "$repo/src/FluentCode/Pages/OpenCodePage.PersistentPtyOverlay.cs", "$repo/src/FluentCode/Pages/OpenCodePage.xaml", "$folder/OpenCodeBrowserOverlayLayout.cs",
    "$repo/src/FluentCode/Pages/OpenCodePage.xaml.cs", "$PSScriptRoot/NativeHost.cs", "$PSScriptRoot/run.ps1"))
$recordedInputs += @(Get-ChildItem "$folder/OpenCodePersistentPty*.cs" | ForEach-Object FullName)
$recordedInputs = $recordedInputs | Sort-Object -Unique
$inputHashes = @{}
foreach ($input in $recordedInputs) { $inputHashes[$input] = (Get-FileHash -Algorithm SHA256 $input).Hash }
Compile 'BillionPtyBindingTests' 'exe' $refs $bindingSources
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'BillionPtyBindingTests.runtimeconfig.json')
& $dotnet (Join-Path $EvidenceRoot 'BillionPtyBindingTests.dll') | Tee-Object (Join-Path $EvidenceRoot 'binding-results.txt')
if ($LASTEXITCODE -ne 0) { throw "Billion/PTY Page binding tests failed: $LASTEXITCODE" }
$bindingNative = @($bindingSources | Where-Object { $_ -notlike '*BillionBindingTests.cs' -and $_ -notlike '*PersistentPtyBindingTests.cs' -and $_ -notlike '*ShellTerminalTests.cs' }) + @(
    "$folder/OpenCodePersistentPtyRenderer.cs", "$folder/OpenCodePersistentPtyView.cs", "$folder/OpenCodePersistentPtyInventoryView.cs", "$folder/OpenCodeQuickAccessStyle.cs", "$PSScriptRoot/NativeHost.cs") + $rendererSources
Compile 'BillionPtyBindingNativeCheck' 'library' ($refs + $native) $bindingNative 'NATIVE_UI'
foreach ($input in $recordedInputs) {
    if ((Get-FileHash -Algorithm SHA256 $input).Hash -ne $inputHashes[$input]) { throw "Compiled/binding input changed during focused validation: $input" }
}
$inputHashes | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'binding-input-hashes.json')
Write-Output 'PASS actual Page/Billion/dialog/PTY binding native reference compile; unrelated page fields shimmed, no native window executed.'
Write-Output "Evidence directory: $EvidenceRoot"
