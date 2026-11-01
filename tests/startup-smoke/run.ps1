param(
    [string]$EvidenceRoot,
    [string]$PublishDirectory,
    [ValidateRange(15, 120)][int]$Seconds = 15,
    [switch]$PanelFeedback,
    [switch]$Wsl
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$EvidenceRoot) { $EvidenceRoot = Join-Path ([IO.Path]::GetTempPath()) ('fc-startup-' + [guid]::NewGuid().ToString('N')) }
elseif (![IO.Path]::IsPathRooted($EvidenceRoot)) { $EvidenceRoot = Join-Path $repo $EvidenceRoot }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path $EvidenceRoot) { throw 'Startup smoke requires a new evidence directory.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
if (!$PublishDirectory) {
    $native = Join-Path $EvidenceRoot 'native'
    & (Join-Path $repo 'scripts/publish-release.ps1') -EvidenceRoot $native
    if ($LASTEXITCODE) { throw "Release build/publish failed: $LASTEXITCODE" }
    $PublishDirectory = Join-Path $native 'publish'
}
if (![IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory = Join-Path $repo $PublishDirectory }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$exe = (Resolve-Path (Join-Path $PublishDirectory 'FluentCode.exe')).Path
$profile = Join-Path $EvidenceRoot 'profile'
New-Item -ItemType Directory -Path $profile | Out-Null
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class SmokeWindow { [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process); [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam); }'
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.WorkingDirectory = Split-Path $exe
$start.EnvironmentVariables['FC_STARTUP_SMOKE_ROOT'] = $profile
if ($PanelFeedback) {
    if (!$Wsl) { throw '-PanelFeedback requires -Wsl (read-only live OpenCode connection).' }
    $start.EnvironmentVariables['FC_SHELL_CAPTURE_TEST'] = '1'
    $start.EnvironmentVariables['FC_SHELL_CAPTURE_WSL'] = '1'
    $start.EnvironmentVariables['FC_SHELL_CAPTURE_OUT'] = $EvidenceRoot
    $start.EnvironmentVariables['FC_PANEL_FEEDBACK_TEST'] = '1'
}
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
    if ($PanelFeedback) {
        while (!(Test-Path (Join-Path $profile 'ready.txt'))) {
            if ($process.WaitForExit(100)) { throw "Panel fixture exited early: $($process.ExitCode)" }
            if (Test-Path (Join-Path $profile 'failed.txt')) { throw ([IO.File]::ReadAllText((Join-Path $profile 'failed.txt'))) }
            if ($watch.Elapsed.TotalSeconds -gt 180) { throw 'Panel feedback fixture timed out.' }
        }
        $layout = Get-Content -Raw (Join-Path $EvidenceRoot 'panel-layout.json') | ConvertFrom-Json
        if (@($layout).Count -ne 4) { throw 'Expected four real WinUI layout states.' }
        foreach ($state in $layout) {
            foreach ($gap in $state.Gaps) {
                if ([Math]::Abs($gap.BeforeDip - 16) -gt 0.5 -or [Math]::Abs($gap.AfterDip - 16) -gt 0.5) { throw "Unequal separator gaps: $($state.State)/$($gap.Name)" }
                Write-Output "PASS $($state.State) $($gap.Name): $($gap.BeforePixels)/$($gap.AfterPixels) physical pixels ($($gap.BeforeDip)/$($gap.AfterDip) DIP)"
            }
        }
        $feedback = Get-Content -Raw (Join-Path $EvidenceRoot 'panel-feedback.json') | ConvertFrom-Json
        if (!$feedback.Passed -or !$feedback.NoLayoutShift -or !$feedback.NoVisibleSuccessText) { throw 'Copy feedback assertion failed.' }
        foreach ($name in @('40-panel-no-strip', '41-panel-browser-strip', '42-panel-terminal-strip', '43-panel-both-strips', '50-copy-feedback')) {
            if (!(Test-Path (Join-Path $EvidenceRoot ($name + '.png')))) { throw "Missing real-app capture: $name" }
        }
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
    Write-Output "PASS startup/native panel checks: PID $($process.Id), live HWND $handle, alive $($result.AliveSeconds)s"
} catch {
    $result.Error = $_.Exception.Message
    throw
} finally {
    if ($process -and !$process.HasExited) {
        $process.Refresh()
        [void][SmokeWindow]::PostMessage($process.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
        if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    if ($process -and $process.HasExited) {
        $result.ExitCode = $process.ExitCode
        if ($PanelFeedback -and $process.ExitCode -ne 0) {
            $result.Passed = $false
            $result.Error = "Panel fixture shutdown failed: $($process.ExitCode)"
        }
    }
    $result | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'results.json')
    Get-FileHash -Algorithm SHA256 -LiteralPath $exe, (Join-Path $PublishDirectory 'FluentCode.dll'), (Join-Path $PublishDirectory 'FluentCode.pri') |
        Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'publish-hashes.json')
    if ($PanelFeedback -and $result.ExitCode -ne 0) { throw $result.Error }
}
