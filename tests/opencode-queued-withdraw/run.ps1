# Headless only: installed .NET 10 SDK/reference packs, no restore, no new packages, no UI, no live server.
# Fake HttpMessageHandler only. Production sources compile read-only: the full OpenCode client/DTOs (wildcard),
# the real Services.Notifications types, withdrawal flow, pending-delivery page handlers and view controls.
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
$arguments += Get-ChildItem "$repo/src/FluentCode/Services/OpenCode/*.cs" |
    Where-Object { $_.Name -NE 'OpenCodePairingService.cs' } | ForEach-Object { '"' + $_.FullName + '"' }
@('OpenCodeInteractionGate.cs', 'OpenCodeExecutionProjection.cs', 'OpenCodePageSource.cs', 'NotificationInteractions.cs',
    'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path "$repo/src/FluentCode/Services/Notifications" $_) + '"'
}
$arguments += '"' + (Join-Path "$repo/src/FluentCode/Pages/OpenCode" 'OpenCodeQueuedPromptWithdrawal.cs') + '"'
$arguments += '"' + (Join-Path "$repo/src/FluentCode/Pages/OpenCode" 'OpenCodeQueuedPromptsView.cs') + '"'
$arguments += '"' + (Join-Path "$repo/src/FluentCode/Pages/OpenCode" 'OpenCodeTouchInput.cs') + '"'
$arguments += '"' + (Join-Path "$repo/src/FluentCode/Pages" 'OpenCodePage.ComposerDelivery.cs') + '"'
$arguments += '"' + (Join-Path "$repo/src/FluentCode/Pages" 'OpenCodePage.SteerChat.cs') + '"'
# Extract complete verbatim members/regions; controls and unrelated page services are explicit inert boundaries.
$overlay = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.ComposerOverlays.cs")
$update = [regex]::Match($overlay, '(?ms)^    private void UpdateQueuedPrompts\(\).*?^    \}\r?$')
$withdraw = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCodePage.QueuedWithdraw.cs")
$ownership = [regex]::Match($withdraw, '(?ms)^    private bool CanWithdrawQueued\(.*?(?=^    private async void QueuedPromptsView_WithdrawRequested)')
if (!$update.Success -or !$ownership.Success) { throw 'Missing actual pending-list/ownership members.' }
$pageMembers = Join-Path $output 'PendingDelivery.PageMembers.cs'
"using System; using System.Linq; using FluentCode.Pages.OpenCode; using FluentCode.Services.OpenCode; using Microsoft.UI.Xaml; namespace FluentCode.Pages; public sealed partial class OpenCodePage {`n" + $update.Value + "`n" + $ownership.Value + "`n}" | Set-Content -Encoding UTF8 $pageMembers
$arguments += '"' + $pageMembers + '"'
$conversation = [IO.File]::ReadAllText("$repo/src/FluentCode/Pages/OpenCode/OpenCodeConversationView.cs")
$steer = [regex]::Match($conversation, '(?ms)^    private sealed class SteerRow.*?(?=^    // Not a Button/detail source:)')
if (!$steer.Success) { throw 'Missing actual pending-steer view region.' }
$steerMembers = Join-Path $output 'PendingDelivery.SteerMembers.cs'
"using System; using System.Collections.Generic; using System.Globalization; using System.Linq; using System.Text.Json; using FluentCode.Services.OpenCode; using Microsoft.UI.Xaml; using Microsoft.UI.Xaml.Automation; using Microsoft.UI.Xaml.Controls; namespace FluentCode.Pages.OpenCode; public sealed partial class OpenCodeConversationView {`n" + $steer.Value + "`n}" | Set-Content -Encoding UTF8 $steerMembers
$arguments += '"' + $steerMembers + '"'
$arguments += '"' + (Join-Path $PSScriptRoot 'PendingDelivery.TestHost.cs') + '"'
$arguments += '"' + (Join-Path $PSScriptRoot 'PendingDeliveryTests.cs') + '"'
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
