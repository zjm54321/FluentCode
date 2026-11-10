param([Parameter(Mandatory=$true)][string]$EvidenceRoot, [switch]$RealWsl)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$assembly = Join-Path $EvidenceRoot 'OpenCodeConnectionTests.dll'
$dpapi = Join-Path $env:USERPROFILE '.nuget/packages/system.security.cryptography.protecteddata/10.0.14/lib/net10.0/System.Security.Cryptography.ProtectedData.dll'
if (!(Test-Path $dpapi)) { throw 'Prepare tools/browser-runtime/prepare.ps1, then restore project dependencies with scripts/build-debug.ps1 first.' }
$sources = @(Get-ChildItem (Join-Path $repo 'src/FluentCode/Models/*.cs') | ForEach-Object FullName)
$sources += Get-ChildItem (Join-Path $repo 'src/FluentCode/Services/OpenCode/*.cs') | ForEach-Object FullName
$sources += Get-ChildItem (Join-Path $repo 'src/FluentCode/Services/OpenCode/Connection/*.cs') | ForEach-Object FullName
foreach ($name in @('SettingsService.cs', 'ConfigurationJson.cs', 'ConfigurationExceptions.cs', 'AtomicFile.cs', 'AppDataPaths.cs', 'DpapiSecretStore.cs')) {
    $sources += Join-Path $repo "src/FluentCode/Services/Configuration/$name"
}
foreach ($name in @('OpenCodeInteractionGate.cs', 'OpenCodePageSource.cs', 'OpenCodeExecutionProjection.cs', 'NotificationInteractions.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs')) {
    $sources += Join-Path $repo "src/FluentCode/Services/Notifications/$name"
}
$sources += Join-Path $repo 'src/FluentCode/Services/Appearance/AppFontSettings.cs'
$sources += Join-Path $PSScriptRoot 'Program.cs'
# ContentDialog reparents its content (Unloaded then Loaded) while opening; the owner closes the view after ShowAsync.
$viewSource = Get-Content -Raw (Join-Path $repo 'src/FluentCode/Pages/Account/OpenCodeConnectionView.cs')
if ($viewSource -match 'Unloaded\s*\+=') { throw 'Connection view must not close itself on Unloaded (dialog reparenting froze WSL enumeration).' }
$refs = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object FullName) + @($dpapi)
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$responseFile = Join-Path $EvidenceRoot 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$inputs = @($sources + $refs + $compiler + $PSCommandPath)
& $dotnet exec $compiler "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt')
if ($LASTEXITCODE -ne 0) { throw "Connection compilation failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $dpapi -Destination $EvidenceRoot
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'OpenCodeConnectionTests.runtimeconfig.json')
$testArgs = @((Join-Path $EvidenceRoot 'isolated-data'))
if ($RealWsl) { $testArgs += '--real-wsl' }
& $dotnet $assembly @testArgs 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Connection tests failed: $LASTEXITCODE" }
Write-Output "PASS connection; evidence: $EvidenceRoot"
