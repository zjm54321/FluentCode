$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('opencode-session-state-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
Write-Output "Evidence directory: $output"
$sources = @((Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.SessionStateUi.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/OpenCode/OpenCodeSessionUiState.cs'),
    (Join-Path $PSScriptRoot 'TestHost.cs'), (Join-Path $PSScriptRoot 'Program.cs'))
Get-FileHash -LiteralPath ($sources + @($PSCommandPath)) -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes.json')
$assembly = Join-Path $output 'SessionStateUiTests.dll'
$arguments = @('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'SessionStateUiTests.runtimeconfig.json')
Write-Output "Compilation passed (SDK $($sdk.Name); warnings as errors)."
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'test.log')
if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
