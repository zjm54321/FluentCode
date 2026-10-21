# Offline input-evidence seam evidence. (1) Focused WinUI compile CHECK of the composer view (+ the same companion
# sources as the builtin-view check, plus the new evidence helper) against cached WinUI/Windows SDK refs (library only).
# (2) Source-linked WinUI-free tests (real controller/ledger/lifetime/menu/evidence; stand-in editor) + static view
# wiring. No GUI, service, network, restore, install or certificate-store action.
param([string]$EvidenceRoot, [string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
if ($SourceRoot) { $repo = (Resolve-Path $SourceRoot).ProviderPath }
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$base = [IO.Path]::GetTempPath()
if ($EvidenceRoot) { $base = (Resolve-Path $EvidenceRoot).ProviderPath }
$output = Join-Path $base ('opencode-composer-input-evidence-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$env:TEMP = Join-Path $output 'temp'
New-Item -ItemType Directory -Path $env:TEMP | Out-Null
$env:TMP = $env:TEMP
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$netRefs = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll'))
$nuget = Join-Path $env:USERPROFILE '.nuget\packages'
$winRefs = @(Get-Item "$nuget\microsoft.windows.sdk.net.ref\10.0.26100.57\lib\net8.0\Microsoft.Windows.SDK.NET.dll", "$nuget\microsoft.windows.sdk.net.ref\10.0.26100.57\lib\net8.0\WinRT.Runtime.dll")
$winRefs += Get-ChildItem "$nuget\microsoft.windowsappsdk.winui\1.8.260803003\lib\net6.0-windows10.0.17763.0\*.dll"
$winRefs += Get-ChildItem "$nuget\microsoft.windowsappsdk.interactiveexperiences\1.8.260708001\lib\net6.0-windows10.0.18362.0\*.dll"
$winRefs += Get-ChildItem "$nuget\microsoft.windowsappsdk.foundation\1.8.260803002\lib\net6.0-windows10.0.17763.0\*.dll"

$services = @(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs')
$notifications = foreach ($name in @('OpenCodeInteractionGate.cs', 'OpenCodePageSource.cs', 'OpenCodeExecutionProjection.cs', 'NotificationInteractions.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs')) { Get-Item "$repo/src/AIFrontend/Services/Notifications/$name" }
$headless = foreach ($name in @('OpenCodeComposerContracts.cs', 'OpenCodeComposerController.cs', 'OpenCodeComposerController.Continuation.cs', 'OpenCodeComposerController.Slots.cs', 'OpenCodeSessionControlCoordinator.cs', 'OpenCodeComposerAttachmentLoader.cs', 'OpenCodeComposerInputPolicy.cs',
        'OpenCodeComposerEditorSync.cs', 'OpenCodeComposerViewLifetime.cs', 'OpenCodeComposerCommandMenu.cs', 'OpenCodeComposerNativeDialogModels.cs', 'OpenCodeComposerInputEvidence.cs')) { Get-Item "$repo/src/AIFrontend/Pages/OpenCode/$name" }
$headless += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeModelPresentation.cs"
$winui = foreach ($name in @('OpenCodeComposerView.cs', 'OpenCodeComposerNativeDialogs.cs', 'OpenCodeVariantPickerDialog.cs', 'OpenCodeModelLogo.cs', 'OpenCodeComposerFilePicker.cs', 'OpenCodeInteractionUi.cs')) { Get-Item "$repo/src/AIFrontend/Pages/OpenCode/$name" }
# The view/dialogs read the user-chosen monospace font (AppFonts); compile its real settings closure (WinUI side only).
$appearance = @(Get-ChildItem "$repo/src/AIFrontend/Services/Appearance/*.cs") + @(Get-ChildItem "$repo/src/AIFrontend/Services/Configuration/*.cs" | Where-Object Name -NE 'DpapiSecretStore.cs') + @(Get-ChildItem "$repo/src/AIFrontend/Models/*.cs")
$viewSources = @($services) + @($notifications) + @($headless) + @($winui) + @($appearance)
$testSources = @($services) + @($notifications) + @($headless) + @(Get-Item (Join-Path "$repo/tests/opencode-composer-input-evidence" 'Program.cs'))

function Write-Rsp($path, $kind, $out, $refs, $srcs, $extra) {
    $a = @('/nologo', "/target:$kind", '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $out + '"')) + $extra
    $a += $refs | ForEach-Object { '/reference:"' + $_.FullName + '"' }
    $a += $srcs | ForEach-Object { '"' + $_.FullName + '"' }
    $a | Set-Content -Encoding UTF8 $path
}
$viewRsp = Join-Path $output 'view-compile.rsp'
$testRsp = Join-Path $output 'tests-compile.rsp'
$viewDll = Join-Path $output 'InputEvidenceViewCheck.dll'
$testDll = Join-Path $output 'OpenCodeInputEvidenceTests.dll'
Write-Rsp $viewRsp 'library' $viewDll (@($netRefs) + @($winRefs)) $viewSources @('/nowarn:CS1591,CS1701,CS1702')
Write-Rsp $testRsp 'exe' $testDll $netRefs $testSources @()
$runtimeConfig = Join-Path $output 'OpenCodeInputEvidenceTests.runtimeconfig.json'
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 $runtimeConfig

$allSources = @($viewSources + $testSources | ForEach-Object FullName | Sort-Object -Unique)
$inputs = @($allSources) + @($netRefs.FullName) + @($winRefs.FullName) + @($compiler, $dotnet, $viewRsp, $testRsp, $runtimeConfig, $PSCommandPath)
$inputs = @($inputs | Sort-Object -Unique)
$before = @($inputs | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } | Select-Object Path, Hash)
$before | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-before.json')
@{ ViewCompileSources = @($viewSources.FullName); TestSources = @($testSources.FullName);
   NetReferences = $netRefs.Count; WinUiReferences = @($winRefs.FullName); SourceRoot = $repo } | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $output 'input-membership.json')
Write-Output "Evidence directory: $output"
if ($SourceRoot) { Write-Output 'PROVISIONAL: immutable captured sources; shared backend files may still change in the live tree.' }
$status = 0
try {
    & $dotnet exec $compiler "@$viewRsp" 2>&1 | Out-File -Encoding utf8 (Join-Path $output 'view-compile.txt')
    $viewExit = $LASTEXITCODE
    Write-Output "WINUI_FOCUSED_COMPILE exit=$viewExit sources=$($viewSources.Count) refs=$($netRefs.Count + $winRefs.Count)"
    if ($viewExit -ne 0) { Get-Content (Join-Path $output 'view-compile.txt'); $status = 1 }
    & $dotnet exec $compiler "@$testRsp" 2>&1 | Out-File -Encoding utf8 (Join-Path $output 'tests-compile.txt')
    $testExit = $LASTEXITCODE
    Write-Output "TEST_COMPILE exit=$testExit sources=$($testSources.Count) refs=$($netRefs.Count)"
    if ($testExit -ne 0) { Get-Content (Join-Path $output 'tests-compile.txt'); $status = 1 }
    else {
        & $dotnet $testDll $repo 2>&1 | Out-File -Encoding utf8 (Join-Path $output 'results.txt')
        $runExit = $LASTEXITCODE
        Get-Content (Join-Path $output 'results.txt')
        if ($runExit -ne 0) { $status = 1 }
    }
}
finally {
    $after = @($inputs | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } | Select-Object Path, Hash)
    $after | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-after.json')
    if (Compare-Object $before $after -Property Path, Hash) { Write-Output 'INPUTS_CHANGED during validation; evidence invalid.'; $status = 1 }
    else { Write-Output "INPUTS_UNCHANGED count=$($inputs.Count)" }
}
exit $status
