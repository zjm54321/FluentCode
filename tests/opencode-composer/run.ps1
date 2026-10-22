# Offline production-linked CORE only. No restore, build, GUI, service or model.
param([string]$EvidenceRoot, [string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if ($SourceRoot) { $repo = (Resolve-Path $SourceRoot).Path }
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$base = [IO.Path]::GetTempPath()
if ($EvidenceRoot) { $base = $EvidenceRoot }
$output = Join-Path $base ('opencode-composer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$env:TEMP = Join-Path $output 'temp'
New-Item -ItemType Directory -Path $env:TEMP | Out-Null
$env:TMP = $env:TEMP
$assembly = Join-Path $output 'OpenCodeComposerTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$references = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll'))
$arguments += $references | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$sources = @(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs')
foreach ($name in @('OpenCodeComposerContracts.cs', 'OpenCodeComposerController.cs', 'OpenCodeComposerController.Continuation.cs', 'OpenCodeComposerController.Slots.cs', 'OpenCodeComposerEditorSync.cs', 'OpenCodeComposerViewLifetime.cs', 'OpenCodeComposerAttachmentLoader.cs', 'OpenCodeComposerInputPolicy.cs', 'OpenCodeComposerSplash.cs', 'OpenCodeSessionControlCoordinator.cs')) {
    $sources += Get-Item "$repo/src/AIFrontend/Pages/OpenCode/$name"
}
foreach ($name in @('OpenCodeInteractionGate.cs', 'OpenCodePageSource.cs', 'OpenCodeExecutionProjection.cs', 'NotificationInteractions.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs', 'NotificationSource.cs')) {
    $sources += Get-Item "$repo/src/AIFrontend/Services/Notifications/$name"
}
foreach ($name in @('Program.cs', 'SyncProtocol.cs', 'BuiltinFoundation.cs', 'ContinuationAdapter.cs', 'DraftSlots.cs', 'Splash.cs')) {
    $sources += Get-Item (Join-Path "$repo/tests/opencode-composer" $name)
}
$arguments += $sources | ForEach-Object { '"' + $_.FullName + '"' }
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeComposerTests.runtimeconfig.json')
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$inputs = @($sources.FullName) + @($references.FullName) + @($compiler, $dotnet, $rsp, (Join-Path $output 'OpenCodeComposerTests.runtimeconfig.json'), $PSCommandPath)
$before = @($inputs | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } | Select-Object Path, Hash)
$before | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-before.json')
$sources | Get-FileHash -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes.json')
Write-Output "Evidence directory: $output"
if ($SourceRoot) { Write-Output 'PROVISIONAL: immutable captured sources, not authoritative combined current-tree evidence.' }
try {
    & $dotnet exec $compiler "@$rsp" | Tee-Object -FilePath (Join-Path $output 'compile.txt')
    if ($LASTEXITCODE -ne 0) { throw "Core compile failed: $LASTEXITCODE" }
    $env:AIFRONTEND_SPLASH_REPO = $repo # Splash honesty/wiring checks read the same production sources compiled above.
    & $dotnet $assembly | Tee-Object -FilePath (Join-Path $output 'results.txt')
    if ($LASTEXITCODE -ne 0) { throw "Core tests failed: $LASTEXITCODE" }
}
finally {
    $after = @($inputs | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } | Select-Object Path, Hash)
    $after | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-after.json')
    if (Compare-Object $before $after -Property Path, Hash) { throw 'Compiled inputs changed during validation; evidence invalid.' }
}
