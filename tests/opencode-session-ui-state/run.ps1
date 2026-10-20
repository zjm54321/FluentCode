# Offline public-API tests. Explicit production links only; no NuGet, client, WinUI or source edits.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('opencode-session-ui-state-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$production = @('OpenCodeSessionUiState.cs', 'OpenCodeSessionUiStateStore.cs', 'OpenCodeConstants.cs') | ForEach-Object {
    Join-Path "$repo/src/AIFrontend/Services/OpenCode" $_
}
$testSource = Join-Path $PSScriptRoot 'Program.cs'
Get-FileHash -LiteralPath ($production + @($testSource, $PSCommandPath)) -Algorithm SHA256 |
    Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes.json')
Write-Output "Evidence directory: $output"

$assembly = Join-Path $output 'OpenCodeSessionUiStateTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += $production | ForEach-Object { '"' + $_ + '"' }
$arguments += '"' + $testSource + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeSessionUiStateTests.runtimeconfig.json')
Write-Output "Headless compilation passed (SDK $($sdk.Name); warnings treated as errors)."
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'test.log')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
