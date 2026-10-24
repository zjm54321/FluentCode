param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
if(!$sdk -or !$pack){throw '.NET 10 required.'}
New-Item -ItemType Directory -Path $EvidenceRoot|Out-Null
$sources=@('OpenCodeBrowserContracts.cs','OpenCodeBrowserPolicy.cs','OpenCodeBrowserInput.cs','OpenCodeBrowserSnapshot.cs','OpenCodeBrowserText.cs','OpenCodeBrowserGeometry.cs','OpenCodeBrowserProfileAnalysis.cs','OpenCodeBrowserSelectionPolicy.cs')|ForEach-Object{"$repo/src/AIFrontend/Services/OpenCode/Browser/$_"}
$sources+="$PSScriptRoot/SelectionTests.cs"
$sources+="$PSScriptRoot/Program.cs"
$dll=Join-Path $EvidenceRoot 'BrowserEngineTests.dll';$rsp=Join-Path $EvidenceRoot 'compile.rsp'
$refs=@(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object FullName)
$args2=@('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$args2+=$refs|ForEach-Object{'/reference:"'+$_+'"'};$args2+=$sources|ForEach-Object{'"'+$_+'"'};$args2|Set-Content -Encoding UTF8 $rsp
$compiler=Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
& $dotnet exec $compiler "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt');if($LASTEXITCODE -ne 0){throw "Browser engine compile failed: $LASTEXITCODE"}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'BrowserEngineTests.runtimeconfig.json')
& $dotnet $dll $repo 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt');if($LASTEXITCODE -ne 0){throw "Browser engine tests failed: $LASTEXITCODE"}
Write-Output "PASS source-linked browser policy/input/snapshot/capture analysis; evidence $EvidenceRoot"
