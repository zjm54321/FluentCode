# Offline production-linked MRU + enhanced and verbatim legacy admission tests. No app build/backend/UI.
param([string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-model-recents-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'OpenCodeModelRecentsTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$references = @(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll")
$arguments += $references | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$sources = @(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs')
foreach ($name in @('OpenCodeComposerContracts.cs', 'OpenCodeComposerController.cs', 'OpenCodeComposerController.Continuation.cs', 'OpenCodeComposerAttachmentLoader.cs', 'OpenCodeComposerInputPolicy.cs', 'OpenCodeSessionControlCoordinator.cs')) {
    $sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/$name"
}
foreach ($name in @('OpenCodeInteractionGate.cs', 'OpenCodePageSource.cs', 'OpenCodeExecutionProjection.cs', 'NotificationInteractions.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs')) {
    $sources += Get-Item "$repo/src/AIFrontend/Services/Notifications/$name"
}
$pagePath = "$repo/src/AIFrontend/Pages/OpenCodePage.Composer.cs"
$page = [IO.File]::ReadAllText($pagePath)
$method = [regex]::Match($page, '(?ms)^    private async Task<OpenCodeComposerSubmitOutcome> SendLegacyAttemptAsync\(.*?^    \}')
if (!$method.Success) { throw 'Missing production legacy method.' }
$generated = Join-Path $output 'LegacySend.cs'
"using System; using System.Threading; using System.Threading.Tasks; using AIFrontend.Services.OpenCode; using AIFrontend.Services.Notifications; using AIFrontend.Pages.OpenCode; namespace AIFrontend.Pages; public sealed partial class OpenCodePage {`n" + $method.Value + "`n}" | Set-Content -Encoding UTF8 $generated
$sources += Get-Item $generated
$sources += Get-Item "$PSScriptRoot/Program.cs"
$arguments += $sources | ForEach-Object { '"' + $_.FullName + '"' }
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeModelRecentsTests.runtimeconfig.json')
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$inputs = @($sources.FullName) + @($references.FullName) + @($compiler, $dotnet, $rsp, $pagePath, $PSCommandPath)
$before = @($inputs | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } | Select-Object Path, Hash)
$before | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-before.json')
Write-Output "Evidence directory: $output"
try {
    & $dotnet exec $compiler "@$rsp" | Tee-Object -FilePath (Join-Path $output 'compile.txt')
    if ($LASTEXITCODE -ne 0) { throw "Compile failed: $LASTEXITCODE" }
    & $dotnet $assembly | Tee-Object -FilePath (Join-Path $output 'results.txt')
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
}
finally {
    $after = @($inputs | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } | Select-Object Path, Hash)
    $after | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-after.json')
    if (Compare-Object $before $after -Property Path, Hash) { throw 'Compiled inputs changed during validation; evidence invalid.' }
}
