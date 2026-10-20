[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
try {
    if ($env:OS -ne 'Windows_NT') { throw 'Run requires Windows.' }
    $root = Split-Path -Parent $PSScriptRoot
    $exe = Join-Path $root "src\AIFrontend\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\AIFrontend.exe"
    if (-not (Test-Path $exe)) { throw "Executable not found. Run scripts\build.ps1 -Configuration $Configuration first." }
    $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "AIFrontend exited during startup (exit code $($process.ExitCode))." }
    } while ($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw "Process $($process.Id) is running, but no main window appeared within 20 seconds." }
    [pscustomobject]@{
        Executable = $exe
        ProcessId = $process.Id
        MainWindowHandle = $process.MainWindowHandle.ToInt64()
        MainWindowTitle = $process.MainWindowTitle
    } | ConvertTo-Json
    exit 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
