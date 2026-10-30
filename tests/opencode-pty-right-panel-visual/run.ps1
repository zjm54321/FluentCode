param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
New-Item -ItemType Directory -Path $EvidenceRoot -Force|Out-Null
$obj=Join-Path $EvidenceRoot 'obj';$bin=Join-Path $EvidenceRoot 'bin';$feed=Join-Path $EvidenceRoot 'empty-feed'
New-Item -ItemType Directory -Path $obj,$bin,$feed -Force|Out-Null
$properties=@("-p:BaseIntermediateOutputPath=$obj\","-p:MSBuildProjectExtensionsPath=$obj\","-p:OutputPath=$bin\")
$project=Join-Path $PSScriptRoot 'PtyRightPanelVisualFixture.csproj'
$inputs=@(Get-ChildItem "$PSScriptRoot/*" -File|ForEach-Object FullName)
$inputs+=@(([xml](Get-Content $project -Raw)).Project.ItemGroup.Compile.Include|Where-Object {$_}|ForEach-Object {[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $_))})
$hashes=@{};foreach($input in $inputs){$hashes[$input]=(Get-FileHash $input -Algorithm SHA256).Hash}
& dotnet.exe restore $project --source $feed @properties 2>&1|Tee-Object (Join-Path $EvidenceRoot 'restore.txt')
if($LASTEXITCODE -ne 0){throw 'Offline restore failed'}
& dotnet.exe build $project --no-restore -nologo -verbosity:minimal @properties 2>&1|Tee-Object (Join-Path $EvidenceRoot 'build.txt')
if($LASTEXITCODE -ne 0){throw 'Fixture build failed'}
foreach($theme in @('dark','light')){
  $process=Start-Process (Join-Path $bin 'PtyRightPanelVisualFixture.exe') -ArgumentList ('"'+$EvidenceRoot+'" '+$theme) -WorkingDirectory $bin -PassThru
  if(!$process.WaitForExit(60000)){& taskkill.exe /PID $process.Id /T /F;throw "Fixture ($theme) exceeded 60 seconds"}
  $process.Refresh()
  Get-Content (Join-Path $EvidenceRoot "result-$theme.json")
  if($process.ExitCode -ne 0){throw "Visual fixture ($theme) failed: $($process.ExitCode); see $EvidenceRoot"}
}
foreach($input in $inputs){if((Get-FileHash $input -Algorithm SHA256).Hash -ne $hashes[$input]){throw "Visual input changed: $input"}}
$hashes|ConvertTo-Json|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'input-hashes.json')
Write-Output "PASS PTY right-panel visual fixture; evidence: $EvidenceRoot"
