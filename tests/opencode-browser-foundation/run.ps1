param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
New-Item -ItemType Directory -Path $EvidenceRoot -Force|Out-Null
$sources=@('OpenCodeBrowserContracts.cs','OpenCodeBrowserExecutionContracts.cs','OpenCodeBrowserAttachmentLease.cs','OpenCodeBrowserDriverContracts.cs','OpenCodeBrowserWorkspaceOptions.cs','OpenCodeBrowserUiContext.cs','OpenCodeBrowserDriverPolicy.cs','OpenCodeBrowserPage.cs','OpenCodeBrowserWorkspace.cs','OpenCodeBrowserPolicy.cs','OpenCodeBrowserNavigation.cs','OpenCodeBrowserPreviewPolicy.cs')|ForEach-Object{"$repo/src/AIFrontend/Services/OpenCode/Browser/$_"}
$sources+="$PSScriptRoot/NativeHost.cs";$sources+="$PSScriptRoot/Program.cs"
$dll=Join-Path $EvidenceRoot 'BrowserFoundationTests.dll';$rsp=Join-Path $EvidenceRoot 'compile.rsp'
$arguments=@('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$arguments+=Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object{'/reference:"'+$_.FullName+'"'}
$arguments+=$sources|ForEach-Object{'"'+$_+'"'};$arguments|Set-Content -Encoding UTF8 $rsp
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp"
if($LASTEXITCODE -ne 0){throw "Foundation compile failed: $LASTEXITCODE"}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'BrowserFoundationTests.runtimeconfig.json')
& $dotnet $dll 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
if($LASTEXITCODE -ne 0){throw "Foundation tests failed: $LASTEXITCODE"}
