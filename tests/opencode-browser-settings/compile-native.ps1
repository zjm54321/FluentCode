param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$foundation=Join-Path $repo 'artifacts/browser-foundation'
$policy=Join-Path $EvidenceRoot '../policy/BrowserSettingsTests.dll'
if(!(Test-Path "$foundation/BrowserFoundationNative.dll") -or !(Test-Path $policy)){throw 'Run foundation native compile and browser-settings/run.ps1 first.'}
New-Item -ItemType Directory -Path $EvidenceRoot -Force|Out-Null
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$args2=@('/nologo','/target:library','/langversion:latest','/nullable:enable','/warnaserror+','/nowarn:1701,1702',('/out:"'+(Join-Path $EvidenceRoot 'BrowserUiNative.dll')+'"'))
$args2+=Get-Content "$foundation/native-compile.rsp"|Where-Object {$_ -like '/reference:*'}
$args2+='/reference:"'+(Join-Path $foundation 'BrowserFoundationNative.dll')+'"'
$args2+='/reference:"'+(Resolve-Path $policy).ProviderPath+'"'
@('OpenCodeBrowserExternalView.cs','OpenCodeBrowserOverlay.cs','OpenCodeBrowserOverlayLayout.cs','OpenCodeBrowserPreviewView.cs','OpenCodeBrowserPreviewWebContent.cs','OpenCodeBrowserStrip.cs','OpenCodeBrowserButton.cs','OpenCodeBrowserPanelState.cs')|ForEach-Object{$args2+='"'+(Join-Path "$repo/src/AIFrontend/Pages/OpenCode" $_)+'"'}
$args2+='"'+(Join-Path $repo 'src/AIFrontend/Pages/Settings/SettingsView.Browser.cs')+'"'
$args2+='"'+(Join-Path $PSScriptRoot 'NativeCompileHost.cs')+'"'
$rsp=Join-Path $EvidenceRoot 'native-compile.rsp';$args2|Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'native-compile.txt');if($LASTEXITCODE -ne 0){throw "Browser UI source compile failed: $LASTEXITCODE"}
Write-Output 'PASS real browser overlay/external preview/local preview/strip/settings partial source compile; unrelated UI helpers are compile-only stand-ins, no native runtime smoke'
