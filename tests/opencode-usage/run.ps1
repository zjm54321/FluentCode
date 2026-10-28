# Offline production-linked .NET 10 csc harness: no restore, WinUI, live services or model calls.
param([string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-usage-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'OpenCodeUsageTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
# Explicit production links avoid accidentally including a concurrently edited unrelated lane.
$files = @(
    'OpenCodeClient.cs', 'OpenCodeClient.Interactions.cs', 'OpenCodeClient.Agents.cs',
    'OpenCodeClient.SessionControls.cs', 'OpenCodeDtos.SessionControls.cs',
    'OpenCodeClient.SessionInfo.cs', 'OpenCodeClient.View.cs', 'OpenCodeClient.Usage.cs',
    'OpenCodeAuthenticationMode.cs', 'OpenCodeClientOptions.cs', 'OpenCodeConstants.cs',
    'OpenCodeExceptions.cs', 'OpenCodeJson.cs', 'OpenCodeInstructionNotice.cs', 'OpenCodeInstructionSource.cs', 'OpenCodeClient.Projects.cs', 'OpenCodeDtos.Projects.cs',
    'OpenCodeDtos.Common.cs', 'OpenCodeDtos.Sessions.cs', 'OpenCodeDtos.Messages.cs',
    'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Inbox.cs', 'OpenCodeDtos.Permissions.cs',
    'OpenCodeDtos.Forms.cs', 'OpenCodeDtos.Agents.cs', 'OpenCodeDtos.Models.cs',
    'OpenCodeUsageAggregator.cs', 'OpenCodeUsageService.cs', 'OpenCodeContextEstimate.cs'
)
$arguments += $files | ForEach-Object { '"' + (Join-Path "$repo/src/AIFrontend/Services/OpenCode" $_) + '"' }
@('OpenCodeInteractionGate.cs', 'NotificationArgumentCodec.cs', 'NotificationInteractions.cs', 'NotificationSource.cs', 'OpenCodeExecutionProjection.cs') |
    ForEach-Object { $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Services/Notifications" $_) + '"' }
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeUsageTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
