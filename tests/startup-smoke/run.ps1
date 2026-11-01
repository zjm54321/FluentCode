param(
    [string]$EvidenceRoot,
    [string]$PublishDirectory,
    [ValidateRange(15, 120)][int]$Seconds = 15
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$EvidenceRoot) { $EvidenceRoot = Join-Path ([IO.Path]::GetTempPath()) ('fc-startup-' + [guid]::NewGuid().ToString('N')) }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path $EvidenceRoot) { throw 'Startup smoke requires a new evidence directory.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
if (!$PublishDirectory) {
    $native = Join-Path $EvidenceRoot 'native'
    & (Join-Path $repo 'scripts/publish-release.ps1') -EvidenceRoot $native
    if ($LASTEXITCODE) { throw "Release build/publish failed: $LASTEXITCODE" }
    $PublishDirectory = Join-Path $native 'publish'
}
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$exe = (Resolve-Path (Join-Path $PublishDirectory 'FluentCode.exe')).Path
$profile = Join-Path $EvidenceRoot 'profile'
New-Item -ItemType Directory -Path $profile | Out-Null
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class SmokeWindow { [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process); [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam); }'
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.WorkingDirectory = Split-Path $exe
$start.EnvironmentVariables['FC_STARTUP_SMOKE_ROOT'] = $profile
$process = $null
$result = @{ Executable = $exe; Profile = $profile; Passed = $false; RequiredSeconds = $Seconds }
try {
    $process = [Diagnostics.Process]::Start($start)
    $result.ProcessId = $process.Id
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        if ($process.WaitForExit(250)) { throw "Published process exited early: $($process.ExitCode)" }
        if (Test-Path (Join-Path $profile 'failed.txt')) { throw ([IO.File]::ReadAllText((Join-Path $profile 'failed.txt'))) }
    }
    $ready = [IO.File]::ReadAllText((Join-Path $profile 'ready.txt'))
    if ($ready -notmatch "PID=$($process.Id); HWND=(\d+);") { throw "Ready marker belongs to a different process: $ready" }
    $handle = [IntPtr]::new([long]$Matches[1])
    [uint32]$owner = 0
    [void][SmokeWindow]::GetWindowThreadProcessId($handle, [ref]$owner)
    if (![SmokeWindow]::IsWindow($handle) -or $owner -ne $process.Id) { throw 'No live window owned by the launched process.' }
    $result.AliveSeconds = $watch.Elapsed.TotalSeconds
    $result.WindowHandle = $handle.ToInt64()
    $result.Passed = $true
    Write-Output "PASS Release startup: PID $($process.Id), live HWND $handle, alive $($result.AliveSeconds)s"
} catch {
    $result.Error = $_.Exception.Message
    throw
} finally {
    if ($process -and !$process.HasExited) {
        $process.Refresh()
        [void][SmokeWindow]::PostMessage($process.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
        if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    $result | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'results.json')
    Get-FileHash -Algorithm SHA256 -LiteralPath $exe, (Join-Path $PublishDirectory 'FluentCode.dll'), (Join-Path $PublishDirectory 'FluentCode.pri') |
        Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'publish-hashes.json')
}
