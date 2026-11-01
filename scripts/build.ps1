[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
try {
    if ($env:OS -ne 'Windows_NT') { throw 'Build requires Windows and Visual Studio MSBuild.' }
    $root = Split-Path -Parent $PSScriptRoot
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = $null
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
    }
    if (-not $msbuild) {
        $msbuild = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
    }
    if (-not (Test-Path $msbuild)) { throw 'Visual Studio MSBuild was not found. No software will be installed.' }
    $logs = Join-Path $root 'artifacts\logs'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    $log = Join-Path $logs "build-$Configuration-x64.log"
    Push-Location $root
    try {
        Write-Host "MSBuild: $msbuild"
        & $msbuild (Join-Path $root 'FluentCode.sln') /restore /t:Build /m /nologo /verbosity:minimal "/p:Configuration=$Configuration" /p:Platform=x64 "/flp:logfile=$log;verbosity=normal;encoding=UTF-8"
        $buildExitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    Write-Host "Build exit code: $buildExitCode"
    Write-Host "Build log: $log"
    if ($buildExitCode -ne 0) { exit $buildExitCode }
    $exe = Join-Path $root "src\FluentCode\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\FluentCode.exe"
    if (-not (Test-Path $exe)) { throw "Build completed but expected executable is missing: $exe" }
    Write-Host "Executable: $exe"
    exit 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
