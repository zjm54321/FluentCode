param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet = (Get-Command dotnet.exe).Source; $root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$sources = @('OpenCodeDtos.Common.cs','OpenCodeDtos.Messages.cs','OpenCodeDtos.Sessions.cs','OpenCodeDtos.Location.cs','OpenCodeDtos.Models.cs','OpenCodeDtos.Inbox.cs','OpenCodeInstructionNotice.cs','OpenCodeManualBackgroundIndex.cs','OpenCodeConstants.cs','OpenCodeJson.cs','OpenCodeExceptions.cs','OpenCodeNoticeDismissals.cs') | ForEach-Object { "$repo/src/AIFrontend/Services/OpenCode/$_" }
$sources += @('OpenCodeConversationView.cs','OpenCodeErrorRecoveryProjection.cs','OpenCodeSubagentPresentation.cs','OpenCodeSubagentTaskTitles.cs','OpenCodeToolRowPresentation.cs','OpenCodeLineDiff.cs','OpenCodeStepRunPresentation.cs','OpenCodeQueuedPromptsView.cs','OpenCodeInteractionUi.cs','OpenCodeComposerActivityPolicy.cs') | ForEach-Object { "$repo/src/AIFrontend/Pages/OpenCode/$_" }
$sources += @("$repo/tests/opencode-instruction-notice/ConversationView.TestHost.cs", "$PSScriptRoot/Program.cs")
$refs = @(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object FullName)
$dll = Join-Path $EvidenceRoot 'VisibleIdleTests.dll'; $rsp = Join-Path $EvidenceRoot 'compile.rsp'
$arguments = @('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$arguments += $refs | ForEach-Object { '/reference:"'+$_+'"' }; $arguments += $sources | ForEach-Object { '"'+$_+'"' }
$arguments | Set-Content -Encoding UTF8 $rsp
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$inputs = @($sources + $refs + $compiler + $dotnet + $PSCommandPath + $rsp)
$before = @(Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash)
$before | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'inputs-before.json')
try {
 & $dotnet exec $compiler "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt')
 if ($LASTEXITCODE -ne 0) { throw "Focused compilation failed: $LASTEXITCODE" }
 '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'VisibleIdleTests.runtimeconfig.json')
 & $dotnet $dll 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
 if ($LASTEXITCODE -ne 0) { throw "Focused tests failed: $LASTEXITCODE" }
} finally {
 $after = @(Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash)
 $after | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'inputs-after.json')
 if (Compare-Object $before $after -Property Path, Hash) { throw 'Focused inputs changed during validation.' }
}
Write-Output "PASS immutable inputs ($($sources.Count) fixed sources; no state/monitor/client wildcard); evidence $EvidenceRoot"
