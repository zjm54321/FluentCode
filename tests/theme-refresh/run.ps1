param(
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [Parameter(Mandatory=$true)][string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
if (Test-Path $EvidenceRoot) { throw 'Theme refresh requires a new evidence directory.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$profile = Join-Path $EvidenceRoot 'profile'
New-Item -ItemType Directory -Path $profile | Out-Null
$exe = (Resolve-Path (Join-Path $PublishDirectory 'FluentCode.exe')).Path
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ThemeWindow {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wparam, string lparam, uint flags, uint timeout, out IntPtr result);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
}
'@
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$old = Get-ItemProperty -LiteralPath $key
$hadValue = $null -ne $old.PSObject.Properties['AppsUseLightTheme']
$oldValue = $old.AppsUseLightTheme
$process = $null
$result = @{ Passed=$false; Executable=$exe; OriginalAppsUseLightTheme=$oldValue }
function Set-AppTheme([int]$light) {
    Set-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -Value $light -Type DWord
    $sent = [IntPtr]::Zero
    [void][ThemeWindow]::SendMessageTimeout([IntPtr]::new(0xffff), 0x1a, [IntPtr]::Zero, 'ImmersiveColorSet', 2, 2000, [ref]$sent)
}
function Wait-Marker([string]$name) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path (Join-Path $profile ($name + '.txt')))) {
        if ($process.WaitForExit(100)) { throw "Theme fixture exited: $($process.ExitCode)" }
        if (Test-Path (Join-Path $profile 'failed.txt')) { throw ([IO.File]::ReadAllText((Join-Path $profile 'failed.txt'))) }
        if ($watch.Elapsed.TotalSeconds -gt 45) { throw "Theme fixture timed out at $name" }
    }
}
try {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $exe
    $start.EnvironmentVariables['FC_STARTUP_SMOKE_ROOT'] = $profile
    $start.EnvironmentVariables['FC_THEME_REFRESH_TEST'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    $result.ProcessId = $process.Id
    Wait-Marker 'system-ready'
    foreach ($phase in @(@('system-light',1), @('system-dark',0), @('override-light',1), @('override-dark',0))) {
        Set-AppTheme $phase[1]
        [IO.File]::WriteAllText((Join-Path $profile ($phase[0] + '.txt')), 'ready')
        Wait-Marker ($phase[0] + '-done')
    }
    Wait-Marker 'ready'
    $ready = [IO.File]::ReadAllText((Join-Path $profile 'ready.txt'))
    if ($ready -notmatch "PID=$($process.Id); HWND=(\d+);") { throw 'Unexpected fixture PID.' }
    $handle = [IntPtr]::new([long]$Matches[1]); [uint32]$owner = 0
    [void][ThemeWindow]::GetWindowThreadProcessId($handle, [ref]$owner)
    if (![ThemeWindow]::IsWindow($handle) -or $owner -ne $process.Id) { throw 'Fixture HWND is not live.' }
    Copy-Item (Join-Path $profile '*.png') $EvidenceRoot
    Copy-Item (Join-Path $profile 'assertions.json') $EvidenceRoot
    $result.Passed = $true
    Write-Output "PASS runtime app and Windows theme changes, same PID $($process.Id): $EvidenceRoot"
} catch { $result.Error=$_.Exception.Message; throw }
finally {
    if ($process -and !$process.HasExited) {
        $process.Refresh()
        [void][ThemeWindow]::PostMessage($process.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
        if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    if ($hadValue) { Set-AppTheme $oldValue }
    else {
        Remove-ItemProperty -LiteralPath $key -Name AppsUseLightTheme
        $sent = [IntPtr]::Zero
        [void][ThemeWindow]::SendMessageTimeout([IntPtr]::new(0xffff), 0x1a, [IntPtr]::Zero, 'ImmersiveColorSet', 2, 2000, [ref]$sent)
    }
    $result | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'results.json')
    Get-FileHash -Algorithm SHA256 -LiteralPath $exe, (Join-Path $PublishDirectory 'FluentCode.dll'), (Join-Path $PublishDirectory 'FluentCode.pri') |
        Select-Object Path,Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'publish-hashes.json')
}
