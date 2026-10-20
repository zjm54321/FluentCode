# Headless only: installed .NET 10 SDK/reference packs, no restore, no new packages, no UI, no live server.
# Fake HttpMessageHandler only. Production sources compile read-only: the full OpenCode client/DTOs (wildcard),
# the real Services.Notifications types they depend on, and the WinUI-free withdrawal flow.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('opencode-queued-withdraw-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'OpenCodeQueuedWithdrawTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" |
    Where-Object { $_.Name -NE 'OpenCodePairingService.cs' } | ForEach-Object { '"' + $_.FullName + '"' }
@('OpenCodeInteractionGate.cs', 'OpenCodeExecutionProjection.cs', 'OpenCodePageSource.cs', 'NotificationInteractions.cs',
    'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Services/Notifications" $_) + '"'
}
$arguments += '"' + (Join-Path "$repo/src/AIFrontend/Pages/OpenCode" 'OpenCodeQueuedPromptWithdrawal.cs') + '"'
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeQueuedWithdrawTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
