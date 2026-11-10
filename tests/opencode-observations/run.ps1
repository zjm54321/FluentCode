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
# The offline delivery seam does not invoke the WinRT completion builder. Guard that exact
# production method without registering/showing a native Toast or creating a fake builder.
$completionSource = [IO.File]::ReadAllText((Join-Path $repo 'src/FluentCode/Services/Notifications/CompletionNotificationService.Completions.cs'))
$completionMethods = [regex]::Matches($completionSource, '(?ms)^    private static NotificationNativeReceipt ShowCompletion\([^\r\n]+\)\r?\n    \{.*?^    \}')
if ($completionMethods.Count -ne 1) { throw 'Expected the single production completion Toast builder.' }
if ($completionMethods[0].Value.Contains('SetAppLogoOverride')) { throw 'Completion Toast must not add a body app-logo image.' }
$registration = [IO.File]::ReadAllText((Join-Path $repo 'src/FluentCode/Services/Notifications/CompletionNotificationService.cs'))
if (!$registration.Contains('RegistrationIcon => NotificationInteractionXml.AppLogo') -or
    !$registration.Contains('AppNotificationManager.Default.Register();') -or
    !$registration.Contains('AppNotificationManager.Default.Register("FluentCode", RegistrationIcon);')) {
    throw 'Packaged/unpackaged application notification identity registration must remain intact.'
}
Write-Output 'PASS completion Toast body-logo source contract; application identity registration retained.'
# WinAppSDK ships net6 projections and the SDK net9 references; the host is net10 (standard unification).
$args = @('/nologo', '/langversion:latest', '/nullable:enable', '/warnaserror+', '/nowarn:1701,1702', '/target:exe', ('/out:"' + $assembly + '"'))
$args += Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$packages = Join-Path $env:USERPROFILE '.nuget/packages'
$foundation = Get-ChildItem "$packages/microsoft.windowsappsdk.foundation" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$windows = Join-Path $packages 'microsoft.windows.sdk.net.ref/10.0.28000.87'
& $dotnet restore (Join-Path $repo 'tests/opencode-persistent-pty/Dependencies.proj') --nologo --verbosity minimal "-p:BaseIntermediateOutputPath=$output/sdk-restore/"
if ($LASTEXITCODE -ne 0) { throw 'Compile-only Windows SDK reference restore failed.' }
$refs = @("$($foundation.FullName)/lib/net6.0-windows10.0.17763.0/Microsoft.Windows.AppNotifications.Projection.dll",
    "$($foundation.FullName)/lib/net6.0-windows10.0.17763.0/Microsoft.Windows.AppNotifications.Builder.Projection.dll",
    "$windows/lib/net9.0/Microsoft.Windows.SDK.NET.dll", "$windows/lib/net9.0/WinRT.Runtime.dll")
foreach ($ref in $refs) { $args += '/reference:"' + $ref + '"'; Copy-Item $ref $output }
foreach ($area in @('Notifications', 'OpenCode')) {
    $args += Get-ChildItem "$repo/src/FluentCode/Services/$area/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object { '"' + $_.FullName + '"' }
}
foreach ($file in @('TaskbarStatus.cs', 'TaskbarStateAggregator.cs', 'TaskbarStateAggregator.Sessions.cs')) { $args += '"' + "$repo/src/FluentCode/Services/Taskbar/$file" + '"' }
$args += Get-ChildItem "$PSScriptRoot/*.cs" | ForEach-Object { '"' + $_.FullName + '"' }
foreach ($file in @('App.OpenCode.Observations.cs', 'App.OpenCode.Diagnostics.cs', 'App.OpenCode.SessionState.cs', 'App.OpenCode.Foreground.cs')) { $args += '"' + "$repo/src/FluentCode/$file" + '"' }
$rsp = Join-Path $output 'compile.rsp'
$args | Set-Content -Encoding UTF8 $rsp
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Core compile failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'Observations.runtimeconfig.json')
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "Core tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
