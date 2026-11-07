param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$EvidenceRoot
)
# Screenshot-only automation. Isolated startup-smoke storage/instance, no connect click, no native test harness.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$publish = if ([IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory } else { Join-Path $repo $PublishDirectory }
$output = if ([IO.Path]::IsPathRooted($EvidenceRoot)) { $EvidenceRoot } else { Join-Path $repo $EvidenceRoot }
if (Test-Path -LiteralPath $output) { throw 'Capture requires a new evidence directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SshCaptureWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
}
'@
$modeName = 'SSH ' + (-join (@(0x81EA, 0x52A8, 0x53D1, 0x73B0) | ForEach-Object { [char]$_ }))
foreach ($theme in @('Light', 'Dark')) {
    $profile = Join-Path $output ('profile-' + $theme.ToLowerInvariant())
    New-Item -ItemType Directory -Path $profile | Out-Null
    [IO.File]::WriteAllText((Join-Path $profile 'settings.json'), ('{"theme":"' + $theme + '"}'), [Text.UTF8Encoding]::new($false))
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $publish 'FluentCode.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $publish
    $start.EnvironmentVariables['FC_STARTUP_SMOKE_ROOT'] = $profile
    $process = $null
    try {
        $process = [Diagnostics.Process]::Start($start)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath (Join-Path $profile 'ready.txt'))) {
            if ($process.WaitForExit(100) -or $watch.Elapsed.TotalSeconds -gt 45) { throw 'Isolated capture window did not become ready.' }
            if (Test-Path -LiteralPath (Join-Path $profile 'failed.txt')) { throw 'Isolated capture startup reported a failure.' }
        }
        $ready = [IO.File]::ReadAllText((Join-Path $profile 'ready.txt'))
        if ($ready -notmatch "PID=$($process.Id); HWND=(\d+);") { throw 'Capture marker belongs to another instance.' }
        $handle = [IntPtr]::new([long]$Matches[1])
        $area = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        [void][SshCaptureWindow]::SetWindowPos($handle, [IntPtr]::new(-1), $area.Left + 30, $area.Top + 30,
            [Math]::Min(1440, $area.Width - 60), [Math]::Min(1200, $area.Height - 60), 0x0040)
        [void][SshCaptureWindow]::SetForegroundWindow($handle)
        Start-Sleep -Milliseconds 800
        $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $modeName)
        $selected = $false
        for ($attempt = 0; $attempt -lt 30 -and !$selected; $attempt++) {
            $items = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
            foreach ($item in $items) {
                if (!$item.Current.IsEnabled) { continue }
                try { $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); $selected = $true; break } catch { }
                try { $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); $selected = $true; break } catch { }
            }
            if (!$selected) { Start-Sleep -Milliseconds 200 }
        }
        if (!$selected) { throw 'SSH connection mode was not reachable through UI Automation.' }
        Start-Sleep -Milliseconds 900
        $rect = [SshCaptureWindow+Rect]::new()
        [void][SshCaptureWindow]::GetWindowRect($handle, [ref]$rect)
        $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
            $file = Join-Path $output ('connection-ssh-' + $theme.ToLowerInvariant() + '.png')
            $bitmap.Save($file, [Drawing.Imaging.ImageFormat]::Png)
            Write-Output "Captured actual isolated SSH connection page: $file"
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
        [void][SshCaptureWindow]::SetWindowPos($handle, [IntPtr]::new(-2), 0, 0, 0, 0, 0x0003)
    } finally {
        if ($process -and !$process.HasExited) {
            [void][SshCaptureWindow]::PostMessage($process.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
            if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
        }
    }
}
