# Headless only: installed .NET 10 SDK/reference packs, no restore, csproj changes, UI or native calls.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'Assets.Tests.ps1')
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('taskbar-headless-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$common = @('/nologo', '/langversion:latest', '/nullable:enable', '/warnaserror+', '/platform:x64')
$common += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$source = Join-Path $repo 'src/AIFrontend/Services/Taskbar'

# Compile production native projection plus the standalone App partial, without loading WinUI.
$projection = $common + @('/target:library', ('/out:"' + (Join-Path $output 'TaskbarProjection.dll') + '"'))
$projection += Get-ChildItem (Join-Path $source '*.cs') | ForEach-Object { '"' + $_.FullName + '"' }
$projection += '"' + (Join-Path $repo 'src/AIFrontend/App.Taskbar.cs') + '"'
$projection += '"' + (Join-Path $PSScriptRoot 'App.TestHost.cs') + '"'
$projectionRsp = Join-Path $output 'projection.rsp'
$projection | Set-Content -Encoding UTF8 $projectionRsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$projectionRsp"
if ($LASTEXITCODE -ne 0) { throw "Native projection compilation failed: $LASTEXITCODE" }
Write-Output 'PASS: Windows x64 native projection and App.Taskbar partial compilation (warnings as errors).'

# Pure aggregation tests compile separately: native projection is not even linked to the test executable.
$assembly = Join-Path $output 'TaskbarCoreTests.dll'
$core = $common + @('/target:exe', ('/out:"' + $assembly + '"'))
$core += '"' + (Join-Path $source 'TaskbarStatus.cs') + '"'
$core += '"' + (Join-Path $source 'TaskbarStateAggregator.cs') + '"'
$core += '"' + (Join-Path $source 'TaskbarStateAggregator.Sessions.cs') + '"'
$core += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$coreRsp = Join-Path $output 'core.rsp'
$core | Set-Content -Encoding UTF8 $coreRsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$coreRsp"
if ($LASTEXITCODE -ne 0) { throw "Core test compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'TaskbarCoreTests.runtimeconfig.json')
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "Core tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
