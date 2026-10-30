param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot);New-Item -ItemType Directory -Path $EvidenceRoot -Force|Out-Null
if(Test-Path "$EvidenceRoot/profile"){throw 'Use a fresh evidence directory for this isolated native selection fixture.'}
$project=Join-Path $PSScriptRoot 'native-selection/BrowserSelectionFixture.csproj'
$obj=Join-Path $EvidenceRoot 'obj';$bin=Join-Path $EvidenceRoot 'bin';$feed=Join-Path $EvidenceRoot 'empty-feed'
New-Item -ItemType Directory -Path $obj,$bin,$feed -Force|Out-Null
$properties=@("-p:BaseIntermediateOutputPath=$obj\","-p:MSBuildProjectExtensionsPath=$obj\","-p:OutputPath=$bin\")
& dotnet.exe restore $project --source $feed @properties 2>&1|Tee-Object "$EvidenceRoot/restore.txt";if($LASTEXITCODE -ne 0){throw 'Owned fixture offline restore failed.'}
& dotnet.exe build $project --no-restore -nologo -verbosity:minimal @properties 2>&1|Tee-Object "$EvidenceRoot/build.txt";if($LASTEXITCODE -ne 0){throw 'Owned native fixture build failed.'}
$process=Start-Process "$bin/BrowserSelectionFixture.exe" -ArgumentList ('"'+$EvidenceRoot+'"') -WorkingDirectory $bin -PassThru
if(!$process.WaitForExit(90000)){& taskkill.exe /PID $process.Id /T /F|Out-File "$EvidenceRoot/timeout.txt";throw 'Owned native fixture timed out.'}
$process.Refresh()
if(Test-Path "$EvidenceRoot/results.json"){$result=Get-Content "$EvidenceRoot/results.json" -Raw|ConvertFrom-Json; $result|Select-Object Native,Passed,version,error|ConvertTo-Json -Depth 8}
elseif(Test-Path "$EvidenceRoot/unhandled.txt"){Get-Content "$EvidenceRoot/unhandled.txt"}
elseif(Test-Path "$EvidenceRoot/host-error.txt"){Get-Content "$EvidenceRoot/host-error.txt"}
if($process.ExitCode -ne 0){throw "Owned native fixture exited $($process.ExitCode)"}
