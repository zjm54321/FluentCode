param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$output = (Resolve-Path $EvidenceRoot).ProviderPath
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$desktop = Get-ChildItem "$root/packs/Microsoft.WindowsDesktop.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$web = Get-ChildItem "$env:USERPROFILE/.nuget/packages/microsoft.web.webview2" -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
if (!$sdk -or !$pack -or !$desktop -or !$web) { throw 'Prepared .NET 10 desktop and WebView2 reference assemblies required; no installation is performed.' }
$refs = @(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object FullName)
$refs += Get-ChildItem "$($desktop.FullName)/ref/net10.0/*.dll" | Where-Object { $_.Name -notin @('WindowsBase.dll') } | ForEach-Object FullName
foreach ($name in @('Core','WinForms')) {
    $dll = Join-Path $web.FullName "lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.$name.dll"
    $refs += $dll
    Copy-Item $dll $output -Force
}
Copy-Item (Join-Path $web.FullName 'runtimes/win-x64/native/WebView2Loader.dll') $output -Force
$sources = @("$PSScriptRoot/PreviewWebView2.cs", "$repo/src/FluentCode/Services/OpenCode/Browser/OpenCodeBrowserPreviewPolicy.cs", "$repo/src/FluentCode/Services/OpenCode/Browser/OpenCodeBrowserPreviewResources.cs")
$assembly = Join-Path $output 'PreviewWebView2.dll'
# WebView2's supported netcoreapp3.0 adapter binds to the installed .NET desktop framework.
$arguments = @('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+','/nowarn:1701',('/out:"' + $assembly + '"'))
$arguments += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Live WebView2 harness compile failed ($LASTEXITCODE)." }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.WindowsDesktop.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'PreviewWebView2.runtimeconfig.json')
& $dotnet $assembly (Join-Path $output 'profile')
if ($LASTEXITCODE -ne 0) { throw "Live WebView2 preview tests failed ($LASTEXITCODE)." }
