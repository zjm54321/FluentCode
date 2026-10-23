# Offline composer VIEW POLICY only (WinUI-free OpenCodeComposerInputPolicy.cs). No restore, build, GUI, service or model.
param([string]$EvidenceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-composer-view-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'OpenCodeComposerViewPolicyTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$sources = @(Get-Item "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerInputPolicy.cs", (Join-Path $PSScriptRoot 'Program.cs'))
$arguments += $sources | ForEach-Object { '"' + $_.FullName + '"' }
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Policy compile failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeComposerViewPolicyTests.runtimeconfig.json')
& $dotnet $assembly | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Policy tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
