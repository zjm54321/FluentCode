# Real production OpenCodePage.ViewAcknowledgement.cs + real OpenCodePageSource / message DTOs /
# OpenCodeExecutionStateReader; only the WinUI/XAML page surface and the App glue are stubbed by the
# test host. Same headless .NET 10 pattern as tests/message-pagination; no restore, UI, or new
# dependencies, no live HTTP.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('opencode-view-ack-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'ViewAckTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
# Full production OpenCode transport (all partials) minus the unrelated pairing service; real source/DTOs/reader.
$arguments += Get-ChildItem (Join-Path $repo 'src/AIFrontend/Services/OpenCode/*.cs') | Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object { '"' + $_.FullName + '"' }
foreach ($name in @('OpenCodeInteractionGate.cs', 'OpenCodePageSource.cs', 'OpenCodeExecutionProjection.cs', 'NotificationInteractions.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs')) {
    $arguments += '"' + (Join-Path $repo "src/AIFrontend/Services/Notifications/$name") + '"'
}
$arguments += '"' + (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeSessionTracking.cs') + '"'
$arguments += '"' + (Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.ViewAcknowledgement.cs') + '"'
$arguments += Get-ChildItem (Join-Path $PSScriptRoot '*.cs') | ForEach-Object { '"' + $_.FullName + '"' }
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'ViewAckTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
