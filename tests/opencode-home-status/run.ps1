param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
if(!$sdk -or !$pack){throw '.NET 10 required.'}
New-Item -ItemType Directory -Path $EvidenceRoot|Out-Null
$themePath="$repo/src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailContent.cs"
$theme=[IO.File]::ReadAllText($themePath);$boundary=$theme.IndexOf('internal sealed class OpenCodeToolDetailOptions')
if($boundary -ge 0){$boundary=$theme.LastIndexOf('///',$boundary)}
if($boundary -lt 0){throw 'Missing theme extraction boundary.'}
$themeGenerated=Join-Path $EvidenceRoot 'ProductionTheme.cs';$theme.Substring(0,$boundary)|Set-Content -Encoding UTF8 $themeGenerated
$sources=@('OpenCodeDtos.Common.cs','OpenCodeDtos.Sessions.cs','OpenCodeDtos.Location.cs','OpenCodeDtos.Models.cs')|ForEach-Object{"$repo/src/AIFrontend/Services/OpenCode/$_"}
$sources+=@('OpenCodeHomeProjection.cs','OpenCodeHomeView.cs','OpenCodeInteractionUi.cs')|ForEach-Object{"$repo/src/AIFrontend/Pages/OpenCode/$_"}
$sources+=@($themeGenerated,"$PSScriptRoot/Home.TestHost.cs","$PSScriptRoot/Program.cs")
$refs=@(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object FullName)
$dll=Join-Path $EvidenceRoot 'HomeTests.dll';$rsp=Join-Path $EvidenceRoot 'compile.rsp'
$arguments=@('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$arguments+=$refs|ForEach-Object{'/reference:"'+$_+'"'};$arguments+=$sources|ForEach-Object{'"'+$_+'"'}
$arguments|Set-Content -Encoding UTF8 $rsp
$compiler=Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$inputs=@($sources+$refs+$compiler+$dotnet+$PSCommandPath+$rsp+$themePath)
$before=@(Get-FileHash -LiteralPath $inputs -Algorithm SHA256|Select-Object Path,Hash)
$before|ConvertTo-Json|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'inputs-before.json')
try{
 & $dotnet exec $compiler "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt')
 if($LASTEXITCODE -ne 0){throw "Home compile failed: $LASTEXITCODE"}
 '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'HomeTests.runtimeconfig.json')
 & $dotnet $dll 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
 if($LASTEXITCODE -ne 0){throw "Home tests failed: $LASTEXITCODE"}
}finally{
 $after=@(Get-FileHash -LiteralPath $inputs -Algorithm SHA256|Select-Object Path,Hash)
 $after|ConvertTo-Json|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'inputs-after.json')
 if(Compare-Object $before $after -Property Path,Hash){throw 'Home inputs changed during validation.'}
}
Write-Output "PASS Home immutable fixed inputs; evidence $EvidenceRoot"
