param(
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [string]$FixtureRoot,
    [int]$FixturePid = 0
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
if ($FixtureRoot -and $FixturePid -le 1) { throw 'Temporary fixture sshd PidFile PID required.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$assembly = Join-Path $EvidenceRoot 'OpenCodeSshTests.dll'
$dpapi = Join-Path $env:USERPROFILE '.nuget/packages/system.security.cryptography.protecteddata/10.0.12/lib/net10.0/System.Security.Cryptography.ProtectedData.dll'
if (!(Test-Path $dpapi)) { throw 'Restore Debug project dependencies first.' }
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
foreach ($name in @('OpenCodeSshQuickAccess.cs', 'OpenCodeWslQuickAccess.cs', 'OpenCodeWslQuickAccess.PathMapping.cs', 'WslQuickAccessHome.cs')) {
    $sources += Join-Path $repo "src/FluentCode/Pages/OpenCode/$name"
}
$sources += Join-Path $PSScriptRoot 'Program.cs'
$refs = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object FullName) + @($dpapi)
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$inputs = @($sources + $refs + $compiler + $PSCommandPath)
Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'source-hashes-before.json')
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$responseFile = Join-Path $EvidenceRoot 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec $compiler "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt')
if ($LASTEXITCODE -ne 0) { throw "SSH compilation failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $dpapi -Destination $EvidenceRoot
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'OpenCodeSshTests.runtimeconfig.json')
$testArgs = @((Join-Path $EvidenceRoot 'isolated-data'))
if ($FixtureRoot) { $testArgs = @('--fixture', (Resolve-Path $FixtureRoot).ProviderPath, [string]$FixturePid, (Resolve-Path $EvidenceRoot).ProviderPath) }
& $dotnet $assembly @testArgs 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "SSH tests failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'source-hashes.json')
if ([IO.File]::ReadAllText((Join-Path $EvidenceRoot 'source-hashes-before.json')) -ne [IO.File]::ReadAllText((Join-Path $EvidenceRoot 'source-hashes.json'))) { throw 'SSH inputs changed during validation.' }
Write-Output "PASS SSH; evidence: $EvidenceRoot"
