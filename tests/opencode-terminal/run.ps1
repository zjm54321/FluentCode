param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$dll = Join-Path $EvidenceRoot 'TerminalTests.dll'
$sources = @(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object FullName)
@('OpenCodeInteractionGate.cs','OpenCodeExecutionProjection.cs','OpenCodePageSource.cs','NotificationInteractions.cs','NotificationArgumentCodec.cs','NotificationActivationInfo.cs','NotificationSource.cs') | ForEach-Object {
    $sources += Join-Path "$repo/src/AIFrontend/Services/Notifications" $_
}
$sources += "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeAgentTreeProjection.cs", "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeTerminalState.cs", "$PSScriptRoot/Program.cs"
$arguments = @('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"' + $dll + '"'))
$arguments += Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$rsp = Join-Path $EvidenceRoot 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp" 2>&1 | Tee-Object (Join-Path $EvidenceRoot 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Terminal compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'TerminalTests.runtimeconfig.json')
& $dotnet $dll $repo 2>&1 | Tee-Object (Join-Path $EvidenceRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Terminal tests failed: $LASTEXITCODE" }
