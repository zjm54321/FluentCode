# Headless only (.NET 10 SDK/reference pack, no restore, no UI): B14 recovered-transient-error projection
# and unrecovered tool-failure whole-row red chrome. Compiles the REAL OpenCodeErrorRecoveryProjection and
# OpenCodeToolRowPresentation (pure data, no WinUI) and source-scans the consuming views.
param([string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-error-recovery-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path $output) { throw "Evidence root already exists: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
Write-Output "Evidence directory: $output"
$assembly = Join-Path $output 'OpenCodeErrorRecoveryTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" |
    Where-Object { $_.Name -NE 'OpenCodePairingService.cs' } | ForEach-Object { '"' + $_.FullName + '"' }
@('OpenCodeInteractionGate.cs', 'OpenCodeExecutionProjection.cs', 'NotificationInteractions.cs',
    'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Services/Notifications" $_) + '"'
}
@('OpenCodeToolRowPresentation.cs', 'OpenCodeLineDiff.cs', 'OpenCodeStepRunPresentation.cs', 'OpenCodeDetailPopupLayout.cs', 'OpenCodeChildChatReadSession.cs', 'OpenCodeErrorRecoveryProjection.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Pages/OpenCode" $_) + '"'
}
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeErrorRecoveryTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
