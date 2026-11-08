param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$EvidenceRoot
)
# ONE isolated offline launch. The in-app env gate renders all four native frames with
# RenderTargetBitmap. Crops copy those pixels at exactly 3x; no UIA, desktop input or screen grab.
# Use capture.sh so this runner is protected by the shared /tmp/opencode/capture.lock.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$publish = if ([IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory } else { Join-Path $repo $PublishDirectory }
$output = if ([IO.Path]::IsPathRooted($EvidenceRoot)) { $EvidenceRoot } else { Join-Path $repo $EvidenceRoot }
$output = [IO.Path]::GetFullPath($output)
if (Test-Path -LiteralPath $output) { throw 'Capture requires a new evidence directory.' }
$exe = (Resolve-Path -LiteralPath (Join-Path $publish 'FluentCode.exe')).ProviderPath

# Fresh preflight, after the shell wrapper acquired the shared lock and before this sole launch.
$rich = @(Get-Process -Name RichEditPrototype -ErrorAction SilentlyContinue).Count
if ($rich -ne 0) { throw "RichEditPrototype is running (count=$rich); capture not launched." }
$running = -join (@(0x8FD0, 0x884C, 0x4E2D) | ForEach-Object { [char]$_ })
$session = @(quser 2>$null) | Where-Object { $_ -match [regex]::Escape($env:USERNAME) } | Select-Object -First 1
if (!$session -or ($session -notmatch 'Active' -and !$session.Contains($running))) { throw "No active session for $($env:USERNAME); capture not launched. quser: $session" }
New-Item -ItemType Directory -Path $output | Out-Null
Write-Output "Preflight: RichEditPrototype count=0; quser: $($session.Trim())"

Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class IndicatorCaptureWindow { [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam); }'
Add-Type -AssemblyName System.Drawing
$profile = Join-Path $output 'profile'
New-Item -ItemType Directory -Path $profile | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.WorkingDirectory = Split-Path $exe
$start.EnvironmentVariables['FC_STARTUP_SMOKE_ROOT'] = $profile
$start.EnvironmentVariables['FC_SHELL_CAPTURE_TEST'] = '1'
$start.EnvironmentVariables['FC_INDICATOR_CAPTURE_TEST'] = '1'
$start.EnvironmentVariables['FC_SHELL_CAPTURE_OUT'] = $output
$start.EnvironmentVariables['FC_SHELL_CAPTURE_WSL'] = '0'
$start.EnvironmentVariables['FC_PANEL_FEEDBACK_TEST'] = '0'
$start.EnvironmentVariables['FC_TRANSCRIPT_INPUT_TEST'] = '0'
$start.EnvironmentVariables['FC_THEME_REFRESH_TEST'] = '0'
$process = $null
$script:assertions = 0
function Require([bool]$condition, [string]$label) {
    $script:assertions++
    if (!$condition) { throw $label }
}
function Crop-Preview([string]$name) {
    $source = [Drawing.Bitmap]::new((Join-Path $output "$name.png"))
    $meta = [IO.File]::ReadAllText((Join-Path $output "$name.json")) | ConvertFrom-Json
    $atlas = $null; $atlasGraphics = $null; $preview = $null; $previewGraphics = $null
    try {
        $sx = $source.Width / $meta.RootWidth; $sy = $source.Height / $meta.RootHeight
        $selected = @($meta.Tabs | Where-Object { $_.SessionId -match 'working|long|background|retry|failed|interrupted|waiting' })
        Require ($selected.Count -ge 6) "$name has every requested status header for crops"
        $rectangles = @($selected | ForEach-Object {
            $left = [Math]::Max(0, [int][Math]::Floor($_.X * $sx)); $top = [Math]::Max(0, [int][Math]::Floor($_.Y * $sy))
            $right = [Math]::Min($source.Width, [int][Math]::Ceiling(($_.X + $_.Width) * $sx))
            $bottom = [Math]::Min($source.Height, [int][Math]::Ceiling(($_.Y + $_.Height) * $sy))
            [Drawing.Rectangle]::new($left, $top, $right - $left, $bottom - $top)
        })
        $tileWidth = [int](($rectangles | ForEach-Object Width | Measure-Object -Maximum).Maximum * 3)
        $tileHeight = [int](($rectangles | ForEach-Object Height | Measure-Object -Maximum).Maximum * 3)
        $columns = [Math]::Max(1, [Math]::Min(3, [int][Math]::Floor(1280 / $tileWidth)))
        $rows = [int][Math]::Ceiling($rectangles.Count / $columns)
        $atlas = [Drawing.Bitmap]::new([int]($columns * $tileWidth), [int]($rows * $tileHeight))
        $atlasGraphics = [Drawing.Graphics]::FromImage($atlas)
        $atlasGraphics.Clear($source.GetPixel(0, 0))
        $atlasGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $atlasGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
        for ($i = 0; $i -lt $rectangles.Count; $i++) {
            $rect = $rectangles[$i]
            $target = [Drawing.Rectangle]::new([int](($i % $columns) * $tileWidth), [int]([Math]::Floor($i / $columns) * $tileHeight), $rect.Width * 3, $rect.Height * 3)
            $atlasGraphics.DrawImage($source, $target, $rect, [Drawing.GraphicsUnit]::Pixel)
        }
        $atlas.Save((Join-Path $output "$name-headers-3x.png"), [Drawing.Imaging.ImageFormat]::Png)
        $overviewWidth = [Math]::Min(1280, $source.Width)
        $overviewHeight = [int][Math]::Round($source.Height * $overviewWidth / $source.Width)
        $previewWidth = [Math]::Max($overviewWidth, $atlas.Width)
        $preview = [Drawing.Bitmap]::new([int]$previewWidth, [int]($overviewHeight + 16 + $atlas.Height))
        $previewGraphics = [Drawing.Graphics]::FromImage($preview)
        $previewGraphics.Clear($source.GetPixel(0, 0))
        $previewGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $previewGraphics.DrawImage($source, 0, 0, [int]$overviewWidth, [int]$overviewHeight)
        $previewGraphics.DrawImageUnscaled($atlas, 0, $overviewHeight + 16)
        $preview.Save((Join-Path $output ("review-" + $name.Substring(5) + '.png')), [Drawing.Imaging.ImageFormat]::Png)
        Write-Output "3x crop: $(Join-Path $output "$name-headers-3x.png")"
        Write-Output "Review: $(Join-Path $output ("review-" + $name.Substring(5) + '.png'))"
    } finally {
        if ($previewGraphics) { $previewGraphics.Dispose() }; if ($preview) { $preview.Dispose() }
        if ($atlasGraphics) { $atlasGraphics.Dispose() }; if ($atlas) { $atlas.Dispose() }; $source.Dispose()
    }
}
$result = [ordered]@{ Executable = $exe; Passed = $false; LaunchCount = 0; PreflightSession = $session.Trim(); RichEditPrototypeCount = $rich }
try {
    $process = [Diagnostics.Process]::Start($start)
    $result.ProcessId = $process.Id; $result.LaunchCount = 1
    Write-Output "Launched PID $($process.Id)"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath (Join-Path $profile 'ready.txt'))) {
        if ($process.WaitForExit(200)) { throw "Capture process exited early: $($process.ExitCode)" }
        if ($watch.Elapsed.TotalSeconds -gt 240) { throw 'Indicator capture timed out.' }
    }
    $ready = [IO.File]::ReadAllText((Join-Path $profile 'ready.txt'))
    Require ($ready -match "PID=$($process.Id); HWND=(\d+);") 'Capture marker belongs to another instance.'
    $result.WindowHandle = [long]$Matches[1]
    if (Test-Path -LiteralPath (Join-Path $profile 'failed.txt')) { throw ('In-app capture failed: ' + [IO.File]::ReadAllText((Join-Path $profile 'failed.txt'))) }
    foreach ($width in @(1920, 1024)) {
        foreach ($theme in @('light', 'dark')) {
            $name = "tabs-$width-$theme"
            Require (Test-Path -LiteralPath (Join-Path $output "$name.png")) "Missing capture $name.png"
            Require (Test-Path -LiteralPath (Join-Path $output "$name.json")) "Missing native geometry $name.json"
            $meta = [IO.File]::ReadAllText((Join-Path $output "$name.json")) | ConvertFrom-Json
            Require ($meta.WindowWidth -eq $width) "$name window width differs"
            Require ($meta.SessionTabCount -eq $(if ($width -eq 1024) { 8 } else { 9 })) "$name tab count differs"
            Crop-Preview $name
        }
    }
    $result.Assertions = $script:assertions; $result.Passed = $true
    Write-Output "PASS indicator native captures/crops: $($script:assertions) assertions; 1 isolated offline launch, no desktop input."
} catch {
    $result.Error = $_.Exception.Message
    throw
} finally {
    if ($process -and !$process.HasExited) {
        $process.Refresh()
        [void][IndicatorCaptureWindow]::PostMessage($process.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
        if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() } # Exact Process object/PID only.
    }
    if ($process) { $result.ExitCode = $process.ExitCode }
    $result.Assertions = $script:assertions
    if (Test-Path -LiteralPath (Join-Path $profile 'capture-log.txt')) { Copy-Item -LiteralPath (Join-Path $profile 'capture-log.txt') -Destination (Join-Path $output 'capture-log.txt') }
    $result | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'results.json')
}
