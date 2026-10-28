param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
New-Item -ItemType Directory -Path $EvidenceRoot|Out-Null
$base="$repo/src/AIFrontend/Services/OpenCode/Browser"
$sources=@('OpenCodeBrowserContracts.cs','OpenCodeBrowserPeerBinding.cs','OpenCodeBrowserPolicy.cs','OpenCodeBrowserNavigation.cs','OpenCodeBrowserInput.cs','OpenCodeBrowserSnapshot.cs','OpenCodeBrowserText.cs','OpenCodeBrowserGeometry.cs','OpenCodeBrowserFrames.cs','OpenCodeBrowserFrameGeometry.cs','OpenCodeBrowserInspection.cs','OpenCodeBrowserProfileAnalysis.cs')|ForEach-Object{"$base/$_"}
$sources+=Get-ChildItem "$base/OpenCodeBrowserSessionBackend*.cs"|ForEach-Object FullName
$sources+="$PSScriptRoot/InspectHost.cs";$sources+="$PSScriptRoot/InspectTests.cs"
$dll=Join-Path $EvidenceRoot 'BrowserInspectTests.dll';$rsp=Join-Path $EvidenceRoot 'compile.rsp'
$refs=@(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object FullName)
$arguments=@('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$arguments+=$refs|ForEach-Object{'/reference:"'+$_+'"'};$arguments+=$sources|ForEach-Object{'"'+$_+'"'};$arguments|Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt');if($LASTEXITCODE -ne 0){throw "Inspect compile failed: $LASTEXITCODE"}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'BrowserInspectTests.runtimeconfig.json')
& $dotnet $dll 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt');if($LASTEXITCODE -ne 0){throw "Inspect tests failed: $LASTEXITCODE"}
