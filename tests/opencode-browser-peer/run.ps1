param([string]$EvidenceRoot = (Join-Path ([IO.Path]::GetTempPath()) ('opencode-browser-peer-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$assembly = Join-Path $EvidenceRoot 'OpenCodeBrowserPeerTests.dll'
$arguments = @('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object { '"' + $_.FullName + '"' }
$arguments += Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/Browser/*.cs" | ForEach-Object { '"' + $_.FullName + '"' }
@('OpenCodeInteractionGate.cs','OpenCodeExecutionProjection.cs','OpenCodePageSource.cs','NotificationInteractions.cs','NotificationArgumentCodec.cs','NotificationActivationInfo.cs','NotificationSource.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Services/Notifications" $_) + '"'
}
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$rsp = Join-Path $EvidenceRoot 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'OpenCodeBrowserPeerTests.runtimeconfig.json')
& $dotnet $assembly $repo | Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $EvidenceRoot"
