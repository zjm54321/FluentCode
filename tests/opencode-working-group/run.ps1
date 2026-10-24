param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source; $root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
if (Test-Path $EvidenceRoot) { throw 'Evidence directory already exists.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$sources = @(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object FullName)
$sources += @('OpenCodeInteractionGate.cs','OpenCodeExecutionProjection.cs','NotificationInteractions.cs','NotificationArgumentCodec.cs','NotificationActivationInfo.cs','NotificationSource.cs') | ForEach-Object { "$repo/src/AIFrontend/Services/Notifications/$_" }
$sources += @('OpenCodeToolRowPresentation.cs','OpenCodeLineDiff.cs','OpenCodeStepRunPresentation.cs','OpenCodeModelPresentation.cs','OpenCodeComposerNativeDialogModels.cs','OpenCodeWorkingClock.cs','OpenCodeWorkingPhrases.cs','OpenCodeWorkingIndicator.cs','OpenCodeToolGroupView.cs') | ForEach-Object { "$repo/src/AIFrontend/Pages/OpenCode/$_" }
$sources += @("$PSScriptRoot/TestHost.cs", "$PSScriptRoot/Program.cs")
$sources += @('OpenCodeBrowserToolPresentation.cs','OpenCodeToolDetailBuiltinData.cs','OpenCodeExecuteInnerCatalog.cs','OpenCodeSubagentPresentation.cs','OpenCodeContextStatusParser.cs') | ForEach-Object { "$repo/src/AIFrontend/Pages/OpenCode/$_" }
$sources += "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerInputPolicy.cs"
$dll = Join-Path $EvidenceRoot 'WorkingGroupTests.dll'; $rsp = Join-Path $EvidenceRoot 'compile.rsp'
$refs = @(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object FullName)
$args2 = @('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$args2 += $refs | ForEach-Object { '/reference:"'+$_+'"' }; $args2 += $sources | ForEach-Object { '"'+$_+'"' }
$args2 | Set-Content -Encoding UTF8 $rsp
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
& $dotnet exec $compiler "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt')
if ($LASTEXITCODE -ne 0) { throw "Working group compile failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'WorkingGroupTests.runtimeconfig.json')
& $dotnet $dll 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Working group tests failed: $LASTEXITCODE" }
