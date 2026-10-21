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
foreach ($name in @('OpenCodePage.ViewVisibility.cs', 'OpenCodePage.MessageBinding.cs', 'OpenCodePage.MessagePagination.cs')) {
    $arguments += '"' + (Join-Path $repo "src/AIFrontend/Pages/$name") + '"'
}
$pagePath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.xaml.cs'
$builtinPath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.BuiltinActions.cs'
$members = foreach ($pair in @(@($pagePath, 'CreateOperationToken'), @($pagePath, 'OrderMessages'), @($pagePath, 'IsScrollContextCurrent'), @($builtinPath, 'ResetAcceptedHistoryProjection'))) {
    $text = [IO.File]::ReadAllText($pair[0])
    $matches = [regex]::Matches($text, ('(?m)^    private [^\r\n]*\b' + $pair[1] + '\('))
    if ($matches.Count -ne 1) { throw "Missing/ambiguous member $($pair[1])" }
    $start = $matches[0].Index
    $opening = $text.IndexOf('{', $start); $arrow = $text.IndexOf('=>', $start)
    $block = if ($opening -ge 0 -and ($arrow -lt 0 -or $opening -lt $arrow)) {
        [regex]::Match($text.Substring($start), '(?ms)\A.*?^    \}\r?$')
    } else { [regex]::Match($text.Substring($start), '(?ms)\A.*?=>[^;]*;') }
    if (!$block.Success) { throw "Missing member body $($pair[1])" }
    $block.Value
}
$generated = Join-Path $output 'PageMembers.cs'
'using System; using System.Collections.Generic; using System.Threading; using AIFrontend.Services.OpenCode; using AIFrontend.Services.Notifications; namespace AIFrontend.Pages; public sealed partial class OpenCodePage {' + "`n" + ($members -join "`n") + "`n}" | Set-Content -Encoding UTF8 $generated
$arguments += '"' + $generated + '"'
$arguments += Get-ChildItem (Join-Path $PSScriptRoot '*.cs') | ForEach-Object { '"' + $_.FullName + '"' }
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
$runtime = Join-Path $output 'ViewAckTests.runtimeconfig.json'
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 $runtime
$sources = @($arguments | Where-Object { $_ -match '^".*\.cs"$' } | ForEach-Object { $_.Trim('"') })
$inputs = @($sources + @($pagePath, $builtinPath, $PSCommandPath, $responseFile, $runtime, $dotnet, (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll')) + @(Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll')).FullName) | Sort-Object -Unique
Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'inputs-before.json')
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'inputs-after.json')
if ([IO.File]::ReadAllText((Join-Path $output 'inputs-before.json')) -ne [IO.File]::ReadAllText((Join-Path $output 'inputs-after.json'))) { throw 'ACK inputs changed during validation.' }
Write-Output "Evidence directory: $output"
