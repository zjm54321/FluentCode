# Focused compile only: actual R1 native AgentSelector/Picker/Presentation against cached WinUI refs.
# No XAML application build, restore, native execution, service or user settings.
param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
New-Item -ItemType Directory -Path $EvidenceRoot|Out-Null
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
if(!$sdk -or !$pack){throw '.NET10 required'}
$refs=@(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object FullName)
$nuget=Join-Path $env:USERPROFILE '.nuget/packages'
$refs+=@(Get-Item "$nuget/microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/Microsoft.Windows.SDK.NET.dll","$nuget/microsoft.windows.sdk.net.ref/10.0.26100.57/lib/net8.0/WinRT.Runtime.dll"|ForEach-Object FullName)
foreach($spec in @('microsoft.windowsappsdk.winui/1.8.260803003/lib/net6.0-windows10.0.17763.0','microsoft.windowsappsdk.interactiveexperiences/1.8.260708001/lib/net6.0-windows10.0.18362.0','microsoft.windowsappsdk.foundation/1.8.260803002/lib/net6.0-windows10.0.17763.0')){$refs+=@(Get-ChildItem "$nuget/$spec/*.dll"|ForEach-Object FullName)}
$sources=@(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs"|Where-Object Name -NE 'OpenCodePairingService.cs'|ForEach-Object FullName)
$sources+=@('OpenCodeInteractionGate.cs','OpenCodePageSource.cs','OpenCodeExecutionProjection.cs','NotificationInteractions.cs','NotificationArgumentCodec.cs','NotificationActivationInfo.cs','NotificationSource.cs','AgentSwitchCoordinator.cs')|ForEach-Object{"$repo/src/AIFrontend/Services/Notifications/$_"}
$sources+=@('OpenCodeAgentSelector.cs','OpenCodeAgentPickerDialog.cs','OpenCodeAgentPresentation.cs','OpenCodeAgentIcons.cs','OpenCodePickerFlyout.cs','OpenCodeInteractionUi.cs','OpenCodeSessionControlCoordinator.cs')|ForEach-Object{"$repo/src/AIFrontend/Pages/OpenCode/$_"}
$sources+="$repo/src/AIFrontend/Pages/OpenCode/OpenCodeAgentIconTheme.cs"
$themePath="$repo/src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailContent.cs"
$theme=[regex]::Match([IO.File]::ReadAllText($themePath),'(?ms)^internal static class OpenCodeToolTheme\r?\n\{.*?^\}').Value
if(!$theme){throw 'Missing actual theme style helper'}
$generated=Join-Path $EvidenceRoot 'ActualAgentTheme.cs'
"using System;using System.Collections.Generic;using System.Text;using Microsoft.UI.Xaml;using Microsoft.UI.Xaml.Markup;namespace AIFrontend.Pages.OpenCode;`n"+$theme|Set-Content -Encoding UTF8 $generated
$sources+=$generated
$rsp=Join-Path $EvidenceRoot 'compile.rsp';$assembly=Join-Path $EvidenceRoot 'AgentNativeLibrary.dll';$compiler=Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$arguments=@('/nologo','/target:library','/langversion:latest','/nullable:enable','/warnaserror+','/nowarn:CS1591,CS1701,CS1702',('/out:"'+$assembly+'"'))
$arguments+=$refs|ForEach-Object{'/reference:"'+$_+'"'};$arguments+=$sources|ForEach-Object{'"'+$_+'"'};$arguments|Set-Content -Encoding UTF8 $rsp
& $dotnet exec $compiler "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.log');if($LASTEXITCODE -ne 0){throw "R1 focused native library compile failed: $LASTEXITCODE"}
Write-Output "PASS R1_NATIVE_LIBRARY exit=0 sources=$($sources.Count) refs=$($refs.Count) (no native execution/application build)"
