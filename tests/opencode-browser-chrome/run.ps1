param([Parameter(Mandatory=$true)][string]$EvidenceRoot,[switch]$DownloadProvenanceOnly,[switch]$DownloadChildProvenanceOnly,[switch]$DownloadRoutingOnly)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
New-Item -ItemType Directory -Force -Path $EvidenceRoot|Out-Null
$services="$repo/src/AIFrontend/Services/OpenCode/Browser"
$sources=@(Get-ChildItem "$services/OpenCodeBrowserChrome*.cs"|ForEach-Object FullName)
$sources+=@(Get-ChildItem "$services/OpenCodeBrowserCdpWebSocket*.cs"|ForEach-Object FullName)
$sources+="$services/OpenCodeBrowserDriverContracts.cs","$services/OpenCodeBrowserExecutionContracts.cs"
$refs=@(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object FullName)
$winui=Get-ChildItem "$env:USERPROFILE/.nuget/packages/microsoft.windowsappsdk.winui" -Directory|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$winrt=Get-ChildItem "$env:USERPROFILE/.nuget/packages/microsoft.windows.sdk.net.ref" -Directory|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$interactive=Get-ChildItem "$env:USERPROFILE/.nuget/packages/microsoft.windowsappsdk.interactiveexperiences" -Directory|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$nativeRefs=@("$($winui.FullName)/lib/net6.0-windows10.0.17763.0/Microsoft.WinUI.dll","$($winrt.FullName)/lib/net8.0/WinRT.Runtime.dll","$($winrt.FullName)/lib/net8.0/Microsoft.Windows.SDK.NET.dll")
$nativeRefs+="$($interactive.FullName)/lib/net6.0-windows10.0.17763.0/Microsoft.InteractiveExperiences.Projection.dll"
$production=@('/nologo','/target:library','/langversion:latest','/nullable:enable','/warnaserror+','/nowarn:1701',('/out:"'+(Join-Path $EvidenceRoot 'ChromeProduction.dll')+'"'))
$production+=($refs+$nativeRefs)|ForEach-Object{'/reference:"'+$_+'"'}
$production+=($sources+"$PSScriptRoot/AttachmentHost.cs")|ForEach-Object{'"'+$_+'"'}
$productionRsp=Join-Path $EvidenceRoot 'production-compile.rsp';$production|Set-Content -Encoding UTF8 $productionRsp
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$productionRsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'production-compile.txt')
if($LASTEXITCODE -ne 0){throw "Chrome production/WinUI compile failed: $LASTEXITCODE"}
Write-Output 'PASS production Chrome source compile with real WinUI references'
$dll=Join-Path $EvidenceRoot 'ChromeTests.dll'
$args2=@('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$args2+=$refs|ForEach-Object{'/reference:"'+$_+'"'}
$args2+=($sources+@(Get-ChildItem "$PSScriptRoot/*.cs"|ForEach-Object FullName))|ForEach-Object{'"'+$_+'"'}
$rsp=Join-Path $EvidenceRoot 'compile.rsp';$args2|Set-Content -Encoding UTF8 $rsp
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt')
if($LASTEXITCODE -ne 0){throw "Chrome compile failed: $LASTEXITCODE"}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'ChromeTests.runtimeconfig.json')
$testArgs=@($EvidenceRoot);if($DownloadRoutingOnly){$testArgs+='download-routing'}elseif($DownloadChildProvenanceOnly){$testArgs+='download-provenance-child'}elseif($DownloadProvenanceOnly){$testArgs+='download-provenance'}
& $dotnet $dll @testArgs 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
if($LASTEXITCODE -ne 0){throw "Chrome tests failed: $LASTEXITCODE"}
