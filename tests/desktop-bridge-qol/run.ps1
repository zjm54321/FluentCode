# Offline QOL contract boundary using the real production HTTP transport; no backend or persisted favorites.
param([Parameter(Mandatory = $true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
if (Test-Path $EvidenceRoot) { throw 'EvidenceRoot must be fresh.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$output = (Resolve-Path $EvidenceRoot).ProviderPath
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $root 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$sources = @(
    'OpenCodeClient.cs', 'OpenCodeClient.Interactions.cs', 'OpenCodeClient.Agents.cs',
    'OpenCodeClient.SessionControls.cs', 'OpenCodeDtos.SessionControls.cs',
    'OpenCodeDesktopBridge.cs', 'OpenCodeClient.PluginRpc.cs', 'OpenCodeModelFavoritesRpc.cs',
    'OpenCodeClient.BillionDiscovery.cs', 'OpenCodeDtos.BillionDiscovery.cs',
    'OpenCodeAuthenticationMode.cs', 'OpenCodeClientOptions.cs', 'OpenCodeConstants.cs', 'OpenCodeExceptions.cs',
    'OpenCodeJson.cs', 'OpenCodeInstructionNotice.cs', 'OpenCodeInstructionSource.cs',
    'OpenCodeClient.Projects.cs', 'OpenCodeDtos.Projects.cs', 'OpenCodeClient.Browser.cs', 'OpenCodeBrowserTransportErrors.cs',
    'OpenCodeDtos.Common.cs', 'OpenCodeDtos.Sessions.cs', 'OpenCodeDtos.Messages.cs',
    'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Inbox.cs', 'OpenCodeDtos.Permissions.cs',
    'OpenCodeDtos.Forms.cs', 'OpenCodeDtos.Agents.cs', 'OpenCodeDtos.Models.cs'
) | ForEach-Object { Join-Path "$repo/src/FluentCode/Services/OpenCode" $_ }
$sources += @(
    'OpenCodeInteractionGate.cs', 'NotificationArgumentCodec.cs', 'NotificationInteractions.cs',
    'NotificationSource.cs', 'OpenCodeExecutionProjection.cs', 'OpenCodePageSource.cs'
) | ForEach-Object { Join-Path "$repo/src/FluentCode/Services/Notifications" $_ }
$sources += Join-Path $PSScriptRoot 'Program.cs'
$sources += Join-Path $PSScriptRoot 'PreviewReadTests.cs'
$sources += Join-Path $PSScriptRoot 'TunnelErrorTests.cs'
@('OpenCodeBrowserPreviewRead.cs','OpenCodeBrowserPreviewPolicy.cs','OpenCodeBrowserPreviewResources.cs') | ForEach-Object { $sources += Join-Path "$repo/src/FluentCode/Services/OpenCode/Browser" $_ }
$assembly = Join-Path $output 'DesktopBridgeQolTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | Sort-Object FullName | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'DesktopBridgeQolTests.runtimeconfig.json')
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "QOL bridge focused compile failed: $LASTEXITCODE" }
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "QOL bridge focused tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
