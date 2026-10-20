# Real production AgentSwitchCoordinator + gate/projection/page-source/codec and the real OpenCode
# transport; fake HTTP only. Same headless .NET 10 pattern as the existing tests; no restore, UI,
# no live server, no native notification registration.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('agent-switch-session-lookup-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'AgentSwitchSessionLookupTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
# Full production OpenCode client (all partials) minus the unrelated pairing service; fake HTTP only.
$arguments += Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" |
    Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object { '"' + $_.FullName + '"' }
# Notification sources under test: the coordinator, its gate/projection/page-source/codec and enums.
$notifications = @('AgentSwitchCoordinator.cs', 'OpenCodeInteractionGate.cs', 'OpenCodeExecutionProjection.cs',
    'OpenCodePageSource.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs',
    'NotificationInteractions.cs', 'NotificationSource.cs')
foreach ($file in $notifications) { $arguments += '"' + (Join-Path $repo "src/AIFrontend/Services/Notifications/$file") + '"' }
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'AgentSwitchSessionLookupTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
