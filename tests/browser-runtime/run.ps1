param([string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
if (-not $EvidenceRoot) { $EvidenceRoot = Join-Path $repo 'artifacts/browser-runtime-tests' }
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk -or -not $pack) { throw '.NET 10 is required.' }
$browser = Join-Path $repo 'src/AIFrontend/Services/OpenCode/Browser'
$sources = @('OpenCodeBrowserAttachmentLease.cs', 'OpenCodeBrowserExecutionContracts.cs', 'OpenCodeBrowserRuntimeProtocol.cs', 'OpenCodeBrowserTunnelTransport.cs', 'OpenCodeBrowserRuntimeFactory.cs', 'OpenCodeBrowserRuntime.cs') | ForEach-Object { Join-Path $browser $_ }
$sources += Join-Path $PSScriptRoot 'Program.cs'
$sources += Join-Path $PSScriptRoot 'ChromeCdpHost.cs'
$dll = Join-Path $EvidenceRoot 'BrowserRuntimeTests.dll'
$rsp = Join-Path $EvidenceRoot 'compile.rsp'
$refs = @(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object FullName)
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $dll + '"'))
$arguments += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$arguments | Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Focused C# runtime compile failed ($LASTEXITCODE)." }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'BrowserRuntimeTests.runtimeconfig.json')
& $dotnet $dll $repo
if ($LASTEXITCODE -ne 0) { throw "Focused C# runtime tests failed ($LASTEXITCODE)." }
