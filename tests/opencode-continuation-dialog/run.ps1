# Offline evidence for the continuation confirmation dialog: (1) focused WinUI compile CHECK (library; no XAML compiler,
# no run) of the new dialog + model against cached WinUI/Windows SDK refs; (2) headless model tests. No restore/build/GUI.
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
$output = Join-Path $base ('opencode-continuation-dialog-' + [guid]::NewGuid().ToString('N'))
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
$ui = "$repo/src/AIFrontend/Pages/OpenCode"
$model = Get-Item "$ui/OpenCodeComposerContinuationModels.cs"
$viewSources = @($model, (Get-Item "$ui/OpenCodeComposerContinuationDialog.cs"), (Get-Item "$ui/OpenCodeInteractionUi.cs"))
$testSources = @($model, (Get-Item "$repo/tests/opencode-continuation-dialog/Program.cs"))
function Write-Rsp($path, $kind, $out, $refs, $srcs, $extra) {
    $a = @('/nologo', "/target:$kind", '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $out + '"')) + $extra
    $a += $refs | ForEach-Object { '/reference:"' + $_.FullName + '"' }
    $a += $srcs | ForEach-Object { '"' + $_.FullName + '"' }
    $a | Set-Content -Encoding UTF8 $path
}
$viewRsp = Join-Path $output 'view-compile.rsp'
$testRsp = Join-Path $output 'tests-compile.rsp'
$testDll = Join-Path $output 'OpenCodeContinuationDialogTests.dll'
Write-Rsp $viewRsp 'library' (Join-Path $output 'ContinuationDialogCheck.dll') (@($netRefs) + @($winRefs)) $viewSources @('/nowarn:CS1591,CS1701,CS1702')
Write-Rsp $testRsp 'exe' $testDll $netRefs $testSources @()
$runtimeConfig = Join-Path $output 'OpenCodeContinuationDialogTests.runtimeconfig.json'
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 $runtimeConfig
@{ ViewCompileSources = @($viewSources.FullName); TestSources = @($testSources.FullName); NetReferences = $netRefs.Count; WinUiReferences = @($winRefs.FullName) } |
    ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $output 'input-membership.json')
Write-Output "Evidence directory: $output"
$status = 0
    & $dotnet exec $compiler "@$viewRsp" 2>&1 | Out-File -Encoding utf8 (Join-Path $output 'view-compile.txt')
    $e1 = $LASTEXITCODE
    Write-Output "WINUI_FOCUSED_COMPILE exit=$e1 sources=$($viewSources.Count) refs=$($netRefs.Count + $winRefs.Count)"
    if ($e1 -ne 0) { Get-Content (Join-Path $output 'view-compile.txt'); $status = 1 }
    & $dotnet exec $compiler "@$testRsp" 2>&1 | Out-File -Encoding utf8 (Join-Path $output 'tests-compile.txt')
    $e2 = $LASTEXITCODE
    Write-Output "TEST_COMPILE exit=$e2 sources=$($testSources.Count) refs=$($netRefs.Count)"
    if ($e2 -ne 0) { Get-Content (Join-Path $output 'tests-compile.txt'); $status = 1 }
    else {
        & $dotnet $testDll 2>&1 | Out-File -Encoding utf8 (Join-Path $output 'results.txt')
        if ($LASTEXITCODE -ne 0) { $status = 1 }
        Get-Content (Join-Path $output 'results.txt')
    }
exit $status
