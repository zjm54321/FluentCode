param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
# Focused source compilation against already-installed WinUI/WebView2 reference assemblies.
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$nuget=Join-Path $env:USERPROFILE '.nuget/packages'
function Latest([string]$name) { (Get-ChildItem (Join-Path $nuget $name) -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1).FullName }
$winui=Latest 'microsoft.windowsappsdk.winui';$webview=Latest 'microsoft.web.webview2';$windows=Latest 'microsoft.windows.sdk.net.ref'
New-Item -ItemType Directory -Path $EvidenceRoot -Force|Out-Null
$refs=@(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object FullName)
$refs+=@(Get-ChildItem "$winui/lib/net6.0-windows10.0.17763.0/*.dll"|ForEach-Object FullName)
$refs+=@(Get-ChildItem "$windows/lib/net8.0/*.dll"|ForEach-Object FullName)
$refs+=Join-Path $webview 'lib_manual/net8.0-windows10.0.17763.0/Microsoft.Web.WebView2.Core.Projection.dll'
# SDK builds unify net6/net8 WinRT projections with net10; suppress only those assembly-unification diagnostics.
$arguments=@('/nologo','/target:library','/langversion:latest','/nullable:enable','/warnaserror+','/nowarn:1701,1702',('/out:"'+(Join-Path $EvidenceRoot 'MermaidView.dll')+'"'))
$arguments+=$refs|ForEach-Object{'/reference:"'+$_+'"'}
@('OpenCodeMermaidPolicy.cs','OpenCodeMermaidView.cs')|ForEach-Object{$arguments+='"'+(Join-Path "$repo/src/AIFrontend/Pages/OpenCode" $_)+'"'}
$rsp=Join-Path $EvidenceRoot 'compile-view.rsp';$arguments|Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile-view.txt')
if($LASTEXITCODE -ne 0){throw "Mermaid view compile failed: $LASTEXITCODE"}
Write-Output 'PASS Mermaid native view source compilation (no WinUI runtime executed)'
