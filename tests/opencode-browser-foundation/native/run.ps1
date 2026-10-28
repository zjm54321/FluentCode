param([Parameter(Mandatory=$true)][string]$EvidenceRoot, [string]$AuditRuntimeDirectory)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).ProviderPath
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
if(Test-Path (Join-Path $EvidenceRoot 'profile')){throw 'Use a fresh evidence directory: cached browser authentication invalidates this fixture.'}
if($AuditRuntimeDirectory){
  $AuditRuntimeDirectory=[IO.Path]::GetFullPath($AuditRuntimeDirectory)
  foreach($file in @('node.exe','sidecar.mjs')){if(!(Test-Path (Join-Path $AuditRuntimeDirectory $file))){throw "Prepared runtime is missing $file"}}
}
New-Item -ItemType Directory -Path $EvidenceRoot -Force|Out-Null
$lock=Get-Content (Join-Path $repo 'src/AIFrontend/packages.lock.json') -Raw|ConvertFrom-Json
$expected=@{'Microsoft.Web.WebView2'='1.0.3179.45';'Microsoft.WindowsAppSDK'='1.8.260921001';'Microsoft.Windows.SDK.BuildTools'='10.0.26100.9169'}
foreach($package in $expected.Keys){
  $versions=@($lock.dependencies.PSObject.Properties|ForEach-Object{$_.Value.PSObject.Properties[$package].Value.resolved}|Where-Object{$_}|Select-Object -Unique)
  if($versions.Count -ne 1 -or $versions[0] -ne $expected[$package]){throw "Fixture package pin differs from project lock: $package"}
}
$project=Join-Path $PSScriptRoot 'BrowserNativeFixture.csproj'
$obj=Join-Path $EvidenceRoot 'obj';$bin=Join-Path $EvidenceRoot 'bin';$feed=Join-Path $EvidenceRoot 'empty-feed'
New-Item -ItemType Directory -Path $obj,$bin,$feed -Force|Out-Null
$properties=@("-p:BaseIntermediateOutputPath=$obj\","-p:MSBuildProjectExtensionsPath=$obj\","-p:OutputPath=$bin\")
# Existing local NuGet cache only: this fixture never downloads/installs SDKs, browser runtimes, or global software.
& dotnet.exe restore $project --source $feed @properties 2>&1|Tee-Object (Join-Path $EvidenceRoot 'restore.txt')
if($LASTEXITCODE -ne 0){throw "Native fixture offline restore failed: $LASTEXITCODE"}
$assets=Get-Content (Join-Path $obj 'project.assets.json') -Raw|ConvertFrom-Json
foreach($package in $expected.Keys){if(!$assets.libraries.PSObject.Properties["$package/$($expected[$package])"]){throw "Resolved native package does not match lock: $package"}}
$expected|ConvertTo-Json|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'package-pins.json')
& dotnet.exe build $project --no-restore -nologo -verbosity:minimal @properties 2>&1|Tee-Object (Join-Path $EvidenceRoot 'build.txt')
if($LASTEXITCODE -ne 0){throw "Native fixture build failed: $LASTEXITCODE"}
$exe=Join-Path $bin 'BrowserNativeFixture.exe'
$hostArguments='"'+$EvidenceRoot+'"'
if($AuditRuntimeDirectory){$hostArguments+=' "'+$AuditRuntimeDirectory+'"'}
$process=Start-Process -FilePath $exe -ArgumentList $hostArguments -WorkingDirectory $bin -PassThru
if(!$process.WaitForExit(120000)){
  # Target only this fixture's process tree, never existing user Edge/WebView2 processes.
  & taskkill.exe /PID $process.Id /T /F|Out-File (Join-Path $EvidenceRoot 'timeout.txt')
  throw 'Native WinUI host exceeded its 120-second bound.'
}
$process.Refresh()
if(Test-Path (Join-Path $EvidenceRoot 'audit-results.json')){Get-Content (Join-Path $EvidenceRoot 'audit-results.json')}
elseif(Test-Path (Join-Path $EvidenceRoot 'results.json')){Get-Content (Join-Path $EvidenceRoot 'results.json')}
elseif(Test-Path (Join-Path $EvidenceRoot 'host-error.txt')){Get-Content (Join-Path $EvidenceRoot 'host-error.txt')}
if($process.ExitCode -ne 0){throw "Native fixture exited $($process.ExitCode); evidence: $EvidenceRoot"}
