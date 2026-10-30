param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
New-Item -ItemType Directory -Path $EvidenceRoot -Force|Out-Null
$obj=Join-Path $EvidenceRoot 'obj';$bin=Join-Path $EvidenceRoot 'bin';$feed=Join-Path $EvidenceRoot 'empty-feed'
New-Item -ItemType Directory -Path $obj,$bin,$feed -Force|Out-Null
$properties=@("-p:BaseIntermediateOutputPath=$obj\","-p:MSBuildProjectExtensionsPath=$obj\","-p:OutputPath=$bin\")
$project=Join-Path $PSScriptRoot 'SidebarVisualFixture.csproj'
$dialogSource=Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeComposerNativeDialogs.cs'
$picker=[regex]::Match([IO.File]::ReadAllText($dialogSource),'(?s)internal static class OpenCodeModelPickerDialog.*?(?=\r?\ninternal static class OpenCodeRollbackConfirmationDialog)')
if(!$picker.Success){throw 'Actual native model picker extraction failed'}
$generated=Join-Path $EvidenceRoot 'ModelPicker.generated.cs'
('#nullable enable'+[Environment]::NewLine+'using System; using System.Linq; using System.Threading; using System.Threading.Tasks; using AIFrontend.Services.OpenCode; using Microsoft.UI.Xaml; using Microsoft.UI.Xaml.Automation; using Microsoft.UI.Xaml.Automation.Peers; using Microsoft.UI.Xaml.Controls; using Microsoft.UI.Xaml.Input; using Windows.System; namespace AIFrontend.Pages.OpenCode;'+[Environment]::NewLine+$picker.Value)|Set-Content -Encoding UTF8 $generated
$properties+="-p:GeneratedModelPicker=$generated"
$stateSource=Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeComposerNativeDialogModels.cs'
$state=[regex]::Match([IO.File]::ReadAllText($stateSource),'(?s)^.*?(?=// -+\r?\n// Undo / redo confirmation)')
if(!$state.Success){throw 'Actual model state extraction failed'}
$generatedState=Join-Path $EvidenceRoot 'ModelState.generated.cs'
('#nullable enable'+[Environment]::NewLine+$state.Value)|Set-Content -Encoding UTF8 $generatedState
$properties+="-p:GeneratedModelState=$generatedState"
$dotnet=(Get-Command dotnet.exe).Source;$dotnetRoot=Split-Path $dotnet
$sdk=Get-ChildItem "$dotnetRoot/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$refPack=Get-ChildItem "$dotnetRoot/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$backendSources=@(Get-ChildItem "$repo/src/AIFrontend/Services/OpenCode/*.cs"|Where-Object Name -NE 'OpenCodePairingService.cs'|ForEach-Object FullName)
foreach($name in @('OpenCodeInteractionGate.cs','OpenCodePageSource.cs','OpenCodeExecutionProjection.cs','NotificationInteractions.cs','NotificationArgumentCodec.cs','NotificationActivationInfo.cs','NotificationSource.cs')){$backendSources+=Join-Path $repo "src/AIFrontend/Services/Notifications/$name"}
$backendSources+=Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeSessionControlCoordinator.cs'
$backendSources+=Join-Path $PSScriptRoot 'BackendAccess.cs'
$backendHashes=@{};foreach($input in $backendSources){$backendHashes[$input]=(Get-FileHash $input -Algorithm SHA256).Hash}
$backend=Join-Path $EvidenceRoot 'NativeModelBackend.dll';$rsp=Join-Path $EvidenceRoot 'backend.rsp'
$arguments=@('/nologo','/target:library','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$backend+'"'))
$arguments+=Get-ChildItem "$($refPack.FullName)/ref/net10.0/*.dll"|ForEach-Object {'/reference:"'+$_.FullName+'"'}
$arguments+=$backendSources|ForEach-Object {'"'+$_+'"'}
$arguments|Set-Content -Encoding UTF8 $rsp
& $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp" 2>&1|Tee-Object (Join-Path $EvidenceRoot 'backend-compile.txt')
if($LASTEXITCODE -ne 0){throw 'Focused native model backend closure failed'}
foreach($input in $backendSources){if((Get-FileHash $input -Algorithm SHA256).Hash -ne $backendHashes[$input]){throw "Backend source changed during native candidate compilation: $input"}}
('PASS compiler exit 0; actual production client/Source/coordinator closure')|Add-Content (Join-Path $EvidenceRoot 'backend-compile.txt')
@{sources=$backendHashes;dll=(Get-FileHash $backend -Algorithm SHA256).Hash;response=(Get-FileHash $rsp -Algorithm SHA256).Hash;compilerExit=0}|ConvertTo-Json -Depth 4|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'backend-provenance.json')
$properties+="-p:NativeModelBackend=$backend"
$pageSource=Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.BuiltinActions.cs'
$pageText=[IO.File]::ReadAllText($pageSource)
function ExtractModelPage([string]$name){
  $match=[regex]::Match($pageText,'(?m)^    private (?:static |async )*[^\r\n]+ '+$name+'(?:<[^>]+>)?\(')
  if(!$match.Success){throw "Missing actual Page member $name"}
  $start=$match.Index;$brace=$pageText.IndexOf('{',$start);$arrow=$pageText.IndexOf('=>',$start)
  if($arrow -ge 0 -and $arrow -lt $brace){return $pageText.Substring($start,$pageText.IndexOf(';',$arrow)-$start+1)}
  $depth=0;for($index=$brace;$index -lt $pageText.Length;$index++){
    if($pageText[$index] -eq '{'){$depth++};if($pageText[$index] -eq '}'){$depth--;if($depth -eq 0){return $pageText.Substring($start,$index-$start+1)}}
  };throw "Unclosed actual Page member $name"
}
$pageMembers=@('ReadNativeAsync','ReadNativeModelsAsync','PresentNativeModels','MatchesDisplayedModel','CommitNativeModelAsync')|ForEach-Object {
  ExtractModelPage $_
}
$statusSource=Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.xaml.cs'
$waiting=[regex]::Match([IO.File]::ReadAllText($statusSource),'(?ms)^    private void UpdateWaitingText\(\).*?^    \}')
if(!$waiting.Success){throw 'Actual delivery-status render extraction failed'}
$pageMembers+=$waiting.Value
$generatedPage=Join-Path $EvidenceRoot 'ModelPage.generated.cs'
('#nullable enable'+[Environment]::NewLine+'using System;using System.Collections.Immutable;using System.Linq;using System.Threading;using System.Threading.Tasks;using AIFrontend.Pages.OpenCode;using AIFrontend.Services.OpenCode;using AIFrontend.Services.Notifications;using Microsoft.UI.Xaml;using Microsoft.UI.Xaml.Controls;namespace AIFrontend.Pages;public sealed partial class OpenCodePage {'+[Environment]::NewLine+($pageMembers -join [Environment]::NewLine)+[Environment]::NewLine+'}')|Set-Content -Encoding UTF8 $generatedPage
$properties+="-p:GeneratedModelPage=$generatedPage"
$commandSource=Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeComposerView.cs'
$savedPageText=$pageText;$pageText=[IO.File]::ReadAllText($commandSource)
$commandMembers=@('MenuItem','OriginLabel','OriginBadge')|ForEach-Object {ExtractModelPage $_}
$pageText=$savedPageText
$generatedCommands=Join-Path $EvidenceRoot 'CommandRows.generated.cs'
('#nullable enable'+[Environment]::NewLine+'using System;using Microsoft.UI.Text;using Microsoft.UI.Xaml;using Microsoft.UI.Xaml.Controls;using Microsoft.UI.Xaml.Media;using Microsoft.UI.Xaml.Automation;using Microsoft.UI.Xaml.Automation.Peers;namespace AIFrontend.Pages.OpenCode;internal sealed partial class OpenCodeComposerView {'+[Environment]::NewLine+($commandMembers -join [Environment]::NewLine)+[Environment]::NewLine+'}')|Set-Content -Encoding UTF8 $generatedCommands
$properties+="-p:GeneratedCommandRows=$generatedCommands"
$inputs=@(Get-ChildItem "$PSScriptRoot/*" -File|ForEach-Object FullName)
$inputs+=@(([xml](Get-Content $project -Raw)).Project.ItemGroup.Compile.Include|Where-Object {$_ -and $_ -notlike '$(*'}|ForEach-Object {[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $_))})
$inputs+=$generated
$inputs+=$generatedState
$inputs+=$stateSource
$inputs+=$pageSource
$inputs+=$statusSource
$inputs+=$generatedPage
$inputs+=$backendSources
$inputs+=$commandSource
$inputs+=$generatedCommands
$inputs+=Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.xaml'
$inputs+=$dialogSource
$hashes=@{};foreach($input in $inputs){$hashes[$input]=(Get-FileHash $input -Algorithm SHA256).Hash}
& dotnet.exe restore $project --source $feed @properties 2>&1|Tee-Object (Join-Path $EvidenceRoot 'restore.txt')
if($LASTEXITCODE -ne 0){throw 'Controlled native fixture offline restore failed'}
& dotnet.exe build $project --no-restore -nologo -verbosity:minimal @properties 2>&1|Tee-Object (Join-Path $EvidenceRoot 'build.txt')
if($LASTEXITCODE -ne 0){throw 'Controlled native fixture build failed'}
$process=Start-Process (Join-Path $bin 'SidebarVisualFixture.exe') -ArgumentList ('"'+$EvidenceRoot+'" "'+$repo+'"') -WorkingDirectory $bin -PassThru
if(!$process.WaitForExit(45000)){& taskkill.exe /PID $process.Id /T /F;throw 'Owned fixture exceeded 45 seconds'}
$process.Refresh()
if($process.ExitCode -ne 0){throw "Controlled native fixture failed: $($process.ExitCode); see $EvidenceRoot"}
Get-Content (Join-Path $EvidenceRoot 'result.json')
foreach($input in $inputs){if((Get-FileHash $input -Algorithm SHA256).Hash -ne $hashes[$input]){throw "Visual input changed: $input"}}
$hashes|ConvertTo-Json|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'input-hashes.json')
