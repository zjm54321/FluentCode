[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$LogRoot,
    [switch]$Manual,
    [switch]$BuildOnly,
    [switch]$Adapter
)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild missing; nothing will be installed.' }
$cache = Join-Path $env:USERPROFILE '.nuget\packages'
$prefix = if ($Adapter) { 'rte-adapter' } else { 'rep1' }
# An existing global package cache is the ONLY source. No network restore, product build or solution build.
& $msbuild (Join-Path $PSScriptRoot 'RichEditPrototype.csproj') /restore /t:Build /m:1 /nologo /verbosity:minimal /warnaserror /p:TreatWarningsAsErrors=true /p:Configuration=Debug /p:Platform=x64 "/p:RestoreSources=$cache" /p:RestoreLockedMode=true /p:NuGetAudit=false "/flp:logfile=$LogRoot\$prefix-msbuild.log;verbosity=minimal;encoding=UTF-8"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($BuildOnly) { exit 0 }
$exe = Join-Path $PSScriptRoot 'bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\RichEditPrototype.exe'
$arguments = '--log "' + (Join-Path $LogRoot "$prefix-runtime.log") + '"'
if ($Adapter) { $arguments += ' --adapter' }
if ($Manual) { $arguments += ' --manual' }
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
Write-Output "Prototype PID=$($process.Id)"
if ($Manual) { exit 0 }
if (-not $process.WaitForExit(90000)) {
    # Only the prototype process started above is terminated.
    $process.Kill()
    throw 'Prototype timed out after 90 seconds; inspect rep1-runtime.log.'
}
Write-Output "Prototype exit=$($process.ExitCode)"
exit $process.ExitCode
