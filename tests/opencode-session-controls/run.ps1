# Offline production-linked session-control foundation only. No restore, app build, UI or live requests.
param([Parameter(Mandatory = $true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
if (Test-Path $EvidenceRoot) { throw 'Use a NEW evidence directory; previous journals are immutable.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$EvidenceRoot = (Resolve-Path $EvidenceRoot).ProviderPath
$scratch = Join-Path $EvidenceRoot 'temp'
New-Item -ItemType Directory -Path $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $root 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw 'Cached .NET 10 SDK/reference pack required; no installation attempted.' }
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$refs = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | Sort-Object FullName)
$sources = @((Get-ChildItem "$repo/src/FluentCode/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs' | Sort-Object FullName).FullName)
foreach ($name in @('OpenCodeInteractionGate.cs', 'OpenCodePageSource.cs', 'OpenCodeExecutionProjection.cs', 'NotificationInteractions.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs')) {
    $sources += Join-Path $repo "src/FluentCode/Services/Notifications/$name"
}
$sources += Join-Path $repo 'src/FluentCode/Pages/OpenCode/OpenCodeSessionControlCoordinator.cs'
$sources += Join-Path $repo 'src/FluentCode/Services/Notifications/AgentSwitchCoordinator.cs'
# Source-link the selector's actual state/transport methods; native layout and picker UI are boundaries.
function Extract([string]$text, [string]$name) {
    $match = [regex]::Match($text, '(?m)^    (?:private|internal) (?:static |async )*[\w<>?]+ ' + $name + '\(')
    if (!$match.Success) { throw "Missing selector method $name" }
    $start = $match.Index; $brace = $text.IndexOf('{', $start); $arrow = $text.IndexOf('=>', $start)
    if ($arrow -ge 0 -and $arrow -lt $brace) { return $text.Substring($start, $text.IndexOf(';', $arrow) - $start + 1) }
    $depth = 0
    for ($i = $brace; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }; if ($text[$i] -eq '}') { $depth--; if ($depth -eq 0) { return $text.Substring($start, $i - $start + 1) } }
    }
    throw "Unclosed selector method $name"
}
$selector = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCode/OpenCodeAgentSelector.cs")
$members = @('SelectPendingAgent','SelectUserAgent','SelectPendingModel','ApplyPendingSelectionAsync','ApplyPendingSelectionCoreAsync','ModelsMatch','CompletePendingSelection','SavePendingSelection','SwitchBlockedReason','RefreshDisplay','ReloadAsync','ApplyAgents','ApplyCurrentAgentPresentation','ApplyModel','RunSwitchAsync','ApplySwitchResultAsync','IsOriginCurrent','UpdateControls','DisplayName') | ForEach-Object { Extract $selector $_ }
$selectorSource = Join-Path $EvidenceRoot 'ActualSelectorState.cs'
"using System;using System.Collections.Generic;using System.Net.Http;using System.Threading;using System.Threading.Tasks;using FluentCode.Services.Notifications;using FluentCode.Services.OpenCode;namespace FluentCode.Pages.OpenCode;internal sealed partial class OpenCodeAgentSelector {`n" + ($members -join "`n") + "`n}" | Set-Content -Encoding UTF8 $selectorSource
$sources += $selectorSource
$sources += Join-Path $PSScriptRoot 'SelectorState.cs'
$sources += Join-Path $PSScriptRoot 'Program.cs'
$assembly = Join-Path $EvidenceRoot 'SessionControlTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += $refs | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$rsp = Join-Path $EvidenceRoot 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
Write-Output "Compiler: $compiler; refs: $($pack.FullName)"
& $dotnet exec $compiler "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt')
if ($LASTEXITCODE -ne 0) { throw "Compile failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'SessionControlTests.runtimeconfig.json')
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Session-control tests failed: $LASTEXITCODE" }
Write-Output "PASS $($sources.Count) sources, $($refs.Count) references."
Write-Output "Evidence directory: $EvidenceRoot"
