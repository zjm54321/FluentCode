param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
New-Item -ItemType Directory -Path $EvidenceRoot -Force|Out-Null
$sources=@(Get-ChildItem "$repo/src/AIFrontend/Models/*.cs"|ForEach-Object FullName)
@('SettingsService.cs','ConfigurationJson.cs','ConfigurationExceptions.cs','AtomicFile.cs','AppDataPaths.cs')|ForEach-Object{$sources+=Join-Path "$repo/src/AIFrontend/Services/Configuration" $_}
$sources+="$repo/src/AIFrontend/Services/Appearance/AppFontSettings.cs"
$sources+="$repo/src/AIFrontend/Services/OpenCode/OpenCodeAuthenticationMode.cs"
$sources+="$PSScriptRoot/Program.cs"
$dll=Join-Path $EvidenceRoot 'BrowserSettingsTests.dll';$rsp=Join-Path $EvidenceRoot 'compile.rsp'
$refs=@(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object FullName)
$arguments=@('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$arguments+=$refs|ForEach-Object{'/reference:"'+$_+'"'};$arguments+=$sources|ForEach-Object{'"'+$_+'"'};$arguments|Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt');if($LASTEXITCODE -ne 0){throw "Browser settings compile failed: $LASTEXITCODE"}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'BrowserSettingsTests.runtimeconfig.json')
& $dotnet $dll $repo (Join-Path $EvidenceRoot 'isolated-data') 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt');if($LASTEXITCODE -ne 0){throw "Browser settings checks failed: $LASTEXITCODE"}
