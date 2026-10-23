# Offline, real production sources, Windows .NET 10 csc; no restore, UI, configuration or live service.
param([string]$EvidenceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-commands-attachments-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'OpenCodeCommandsAttachmentsTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$sources = @(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs')
# Services/OpenCode now depends on the interaction gate (SessionControlKind) and the execution projection
# (OpenCodeExecutionMessageEvidence); compile the same WinUI-free Notifications closure the composer suites use.
foreach ($name in @('OpenCodeInteractionGate.cs', 'OpenCodePageSource.cs', 'OpenCodeExecutionProjection.cs', 'NotificationInteractions.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs')) {
    $sources += Get-Item "$repo/src/AIFrontend/Services/Notifications/$name"
}
$arguments += $sources | ForEach-Object { '"' + $_.FullName + '"' }
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeCommandsAttachmentsTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
