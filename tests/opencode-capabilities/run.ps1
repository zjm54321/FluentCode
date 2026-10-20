# Offline production-linked .NET 10 csc harness. No restore, UI, packages, live services or models.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('opencode-capabilities-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'OpenCodeCapabilitiesTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
# Explicit source list: real transport + its required partials/DTOs, no parallel usage lane or WinUI pairing.
@(
    'OpenCodeAuthenticationMode.cs', 'OpenCodeClientOptions.cs', 'OpenCodeExceptions.cs', 'OpenCodeJson.cs',
    'OpenCodeConstants.cs', 'OpenCodeClient.cs', 'OpenCodeClient.Interactions.cs', 'OpenCodeClient.Agents.cs',
    'OpenCodeDtos.Common.cs', 'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Sessions.cs', 'OpenCodeDtos.Messages.cs',
    'OpenCodeDtos.Inbox.cs', 'OpenCodeDtos.Permissions.cs', 'OpenCodeDtos.Forms.cs', 'OpenCodeDtos.Agents.cs',
    'OpenCodeInstructionNotice.cs', 'OpenCodeClient.Capabilities.cs', 'OpenCodeClient.McpControl.cs', 'OpenCodeClient.ServerInfo.cs', 'OpenCodeDtos.Capabilities.cs',
    'OpenCodeCapabilityStatus.cs', 'OpenCodeTodoReplay.cs', 'OpenCodeSubagentStatus.cs'
) | ForEach-Object { $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Services/OpenCode" $_) + '"' }
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE (evidence: $output)" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeCapabilitiesTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE (evidence: $output)" }
Write-Output "Evidence directory: $output"
