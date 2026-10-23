# Real production core, injected HTTP/native facade only; installed references, no restore/new packages.
param([string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-observations-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory $output | Out-Null
$assembly = Join-Path $output 'Observations.dll'
# WinAppSDK ships net6 projections and the SDK net8 references; the host is net10 (standard unification).
$args = @('/nologo', '/langversion:latest', '/nullable:enable', '/warnaserror+', '/nowarn:1701,1702', '/target:exe', ('/out:"' + $assembly + '"'))
$args += Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$packages = Join-Path $env:USERPROFILE '.nuget/packages'
$foundation = Get-ChildItem "$packages/microsoft.windowsappsdk.foundation" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$windows = Get-ChildItem "$packages/microsoft.windows.sdk.net.ref" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$refs = @("$($foundation.FullName)/lib/net6.0-windows10.0.17763.0/Microsoft.Windows.AppNotifications.Projection.dll",
    "$($foundation.FullName)/lib/net6.0-windows10.0.17763.0/Microsoft.Windows.AppNotifications.Builder.Projection.dll",
    "$($windows.FullName)/lib/net8.0/Microsoft.Windows.SDK.NET.dll", "$($windows.FullName)/lib/net8.0/WinRT.Runtime.dll")
foreach ($ref in $refs) { $args += '/reference:"' + $ref + '"'; Copy-Item $ref $output }
foreach ($area in @('Notifications', 'OpenCode')) {
    $args += Get-ChildItem "$repo/src/AIFrontend/Services/$area/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object { '"' + $_.FullName + '"' }
}
foreach ($file in @('TaskbarStatus.cs', 'TaskbarStateAggregator.cs', 'TaskbarStateAggregator.Sessions.cs')) { $args += '"' + "$repo/src/AIFrontend/Services/Taskbar/$file" + '"' }
$args += Get-ChildItem "$PSScriptRoot/*.cs" | ForEach-Object { '"' + $_.FullName + '"' }
foreach ($file in @('App.OpenCode.Observations.cs', 'App.OpenCode.Diagnostics.cs', 'App.OpenCode.SessionState.cs')) { $args += '"' + "$repo/src/AIFrontend/$file" + '"' }
$rsp = Join-Path $output 'compile.rsp'
$args | Set-Content -Encoding UTF8 $rsp
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Core compile failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'Observations.runtimeconfig.json')
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "Core tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
