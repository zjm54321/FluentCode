param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$EvidenceRoot
)
# SSH visual evidence: ONE isolated launch renders every SSH state in Light and Dark from inside the app
# (FC_SSH_CAPTURE_TEST, RenderTargetBitmap). No UI Automation lookup, no desktop input, no screen grab, no connect.
# Only the process started here is ever closed (WM_CLOSE, then Kill of that exact PID if it does not exit).
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$publish = if ([IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory } else { Join-Path $repo $PublishDirectory }
$output = if ([IO.Path]::IsPathRooted($EvidenceRoot)) { $EvidenceRoot } else { Join-Path $repo $EvidenceRoot }
$output = [IO.Path]::GetFullPath($output)
if (Test-Path -LiteralPath $output) { throw 'Capture requires a new evidence directory.' }
$exe = (Resolve-Path -LiteralPath (Join-Path $publish 'FluentCode.exe')).ProviderPath

# Preflight gates (fail closed, before any launch).
$rich = @(Get-Process -Name RichEditPrototype -ErrorAction SilentlyContinue).Count
if ($rich -ne 0) { throw "RichEditPrototype is running (count=$rich); capture not launched." }
$running = -join (@(0x8FD0, 0x884C, 0x4E2D) | ForEach-Object { [char]$_ })
$session = @(quser 2>$null) | Where-Object { $_ -match [regex]::Escape($env:USERNAME) } | Select-Object -First 1
if (!$session -or ($session -notmatch 'Active' -and !$session.Contains($running))) { throw "No active session for $($env:USERNAME); capture not launched. quser: $session" }
New-Item -ItemType Directory -Path $output | Out-Null
Write-Output "Preflight: RichEditPrototype count=0; quser: $($session.Trim())"

Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class SshCaptureWindow { [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam); }'
$profile = Join-Path $output 'profile'
New-Item -ItemType Directory -Path $profile | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.WorkingDirectory = Split-Path $exe
$start.EnvironmentVariables['FC_STARTUP_SMOKE_ROOT'] = $profile
$start.EnvironmentVariables['FC_SHELL_CAPTURE_TEST'] = '1'
$start.EnvironmentVariables['FC_SSH_CAPTURE_TEST'] = '1'
$start.EnvironmentVariables['FC_SHELL_CAPTURE_OUT'] = $output
$states = @('idle', 'progress', 'error-changed-key', 'error-auth', 'warning-missing-ssh', 'success', 'fingerprint')
$expected = @($states | ForEach-Object { "connection-ssh-$_" }) + @('main-ssh-changed-key')
$process = $null
$result = [ordered]@{ Executable = $exe; Passed = $false }
try {
    $process = [Diagnostics.Process]::Start($start)
    $result.ProcessId = $process.Id
    Write-Output "Launched PID $($process.Id)"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath (Join-Path $profile 'ready.txt'))) {
        if ($process.WaitForExit(200)) { throw "Capture process exited early: $($process.ExitCode)" }
        if ($watch.Elapsed.TotalSeconds -gt 240) { throw 'SSH capture timed out.' }
    }
    $ready = [IO.File]::ReadAllText((Join-Path $profile 'ready.txt'))
    if ($ready -notmatch "PID=$($process.Id); HWND=(\d+);") { throw 'Capture marker belongs to another instance.' }
    $result.WindowHandle = [long]$Matches[1]
    if (Test-Path -LiteralPath (Join-Path $profile 'failed.txt')) { throw ('In-app capture failed: ' + [IO.File]::ReadAllText((Join-Path $profile 'failed.txt'))) }
    $missing = @()
    foreach ($name in $expected) {
        foreach ($theme in @('light', 'dark')) {
            $file = Join-Path $output "$name-$theme.png"
            if (Test-Path -LiteralPath $file) { Write-Output "Captured $file" } else { $missing += "$name-$theme.png" }
        }
    }
    if ($missing.Count) { throw ('Missing captures: ' + ($missing -join ', ')) }
    $result.Passed = $true
} catch {
    $result.Error = $_.Exception.Message
    throw
} finally {
    if ($process -and !$process.HasExited) {
        $process.Refresh()
        [void][SshCaptureWindow]::PostMessage($process.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
        if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    if ($process) { $result.ExitCode = $process.ExitCode }
    if (Test-Path -LiteralPath (Join-Path $profile 'capture-log.txt')) { Copy-Item -LiteralPath (Join-Path $profile 'capture-log.txt') -Destination (Join-Path $output 'capture-log.txt') }
    $result | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'results.json')
}
