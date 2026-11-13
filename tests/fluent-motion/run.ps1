# Offline Fluent motion tests: tokens, reduce-motion short-circuit and generation gate (pure C#, no WinUI), plus static
# guards over the WinUI motion layer. Explicit production links only; no NuGet, client, WinUI or source edits.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('fluent-motion-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$production = @(
    (Join-Path $repo 'src/FluentCode/Motion/FluentMotionPolicy.cs')
    (Join-Path $repo 'src/FluentCode/Motion/FluentMotionBindings.cs')
    (Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.Notices.Policy.cs')
)
$testSource = Join-Path $PSScriptRoot 'Program.cs'
Write-Output "Evidence directory: $output"

$assembly = Join-Path $output 'FluentMotionTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += $production | ForEach-Object { '"' + $_ + '"' }
$arguments += '"' + $testSource + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
$runtime = Join-Path $output 'FluentMotionTests.runtimeconfig.json'
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 $runtime
$inputs = @($production + @($testSource, $PSCommandPath, $responseFile, $runtime, $dotnet, (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'))) | Sort-Object -Unique
Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes-before.json')
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
Write-Output "Headless compilation passed (SDK $($sdk.Name); warnings treated as errors)."
& $dotnet $assembly $repo 2>&1 | Tee-Object -FilePath (Join-Path $output 'test.log')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes.json')
if ([IO.File]::ReadAllText((Join-Path $output 'source-hashes-before.json')) -ne [IO.File]::ReadAllText((Join-Path $output 'source-hashes.json'))) { throw 'State inputs changed during validation.' }
