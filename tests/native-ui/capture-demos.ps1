# Run only the lane's built client in isolated offline showcase mode, never the native/full-suite harness.
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$FixturesRoot = '',
    [string]$BeforeEvidenceRoot = '',
    # The 120 s default covers the bundled demo set; larger fixture folders (upstream parity ports) need more.
    [ValidateRange(30, 3600)][int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if ([string]::IsNullOrWhiteSpace($FixturesRoot)) { $FixturesRoot = Join-Path $PSScriptRoot 'fixtures' }
$app = (Resolve-Path $AppPath).Path
$root = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path $root) { throw 'A new evidence directory is required.' }
New-Item -ItemType Directory -Path $root | Out-Null
$saved = @{}
foreach ($key in @('FC_NATIVE_UI_CAPTURE_ROOT', 'FC_NATIVE_UI_FIXTURES', 'FC_STARTUP_SMOKE_ROOT')) { $saved[$key] = [Environment]::GetEnvironmentVariable($key) }
$process = $null
try {
    for ($attempt = 0; Get-Process RichEditPrototype -ErrorAction SilentlyContinue; $attempt++) {
        if ($attempt -ge 20) { throw 'Native composer test still running after 20 minutes; no window launched.' }
        Start-Sleep -Seconds 60
    }
    # CopyFromScreen/native chart evidence requires an active Windows desktop; never send desktop input.
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $session = (& quser 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $session -notmatch '(?im)^\s*>.*\s(?:Active|\u6D3B\u52A8)\s') { throw "Windows session is not Active; no window launched.`n$session" }
    Write-Output $session
    $env:FC_NATIVE_UI_CAPTURE_ROOT = $root
    $env:FC_NATIVE_UI_FIXTURES = (Resolve-Path $FixturesRoot).Path
    $env:FC_STARTUP_SMOKE_ROOT = Join-Path $root 'profile'
    New-Item -ItemType Directory -Path $env:FC_STARTUP_SMOKE_ROOT | Out-Null
    $process = Start-Process -FilePath $app -WorkingDirectory (Split-Path $app) -PassThru
    $finished = $process.WaitForExit($TimeoutSeconds * 1000)
    foreach ($failure in @((Join-Path $root 'failed.txt'), (Join-Path $env:FC_STARTUP_SMOKE_ROOT 'failed.txt'))) {
        if (Test-Path $failure) { throw ([IO.File]::ReadAllText($failure)) }
    }
    if (!$finished) { throw 'Offline native UI capture timed out.' }
    if (!(Test-Path (Join-Path $root 'ready.txt'))) { throw "Offline capture did not finish (process exit $($process.ExitCode))." }
    # Keep generated evidence metadata LF-only; artifacts are never committed.
    $manifest = Join-Path $root 'demos.json'
    [IO.File]::WriteAllText($manifest, [IO.File]::ReadAllText($manifest).Replace("`r`n", "`n").TrimEnd("`n") + "`n", [Text.UTF8Encoding]::new($false))
    $demos = @(Get-ChildItem $FixturesRoot -Filter 'demo-*.oui' | ForEach-Object { $_.BaseName.Substring(5) } | Sort-Object)
    foreach ($name in @($demos) + @('error', 'streaming')) {
        foreach ($theme in @('light', 'dark')) {
            $file = Join-Path $root "$name-$theme.png"
            if (!(Test-Path $file) -or (Get-Item $file).Length -eq 0) { throw "Missing PNG: $file" }
        }
    }
    foreach ($name in @('map', 'video', 'audio', 'gallery', 'youtube')) {
        if ($demos -contains $name) { foreach ($theme in @('light', 'dark')) { if (!(Test-Path (Join-Path $root "$name-before-$theme.png"))) { throw "Missing click-to-load before capture: $name / $theme" } } }
    }
    if ($BeforeEvidenceRoot) {
        $before = Join-Path $root 'before-visual-fixes'; New-Item -ItemType Directory -Path $before | Out-Null
        foreach ($name in @('calculator', 'sizing', 'showcase-analytics')) { foreach ($theme in @('light', 'dark')) { Copy-Item -LiteralPath (Join-Path $BeforeEvidenceRoot "$name-$theme.png") -Destination $before } }
        [IO.File]::WriteAllText((Join-Path $before 'provenance.txt'), "Unmodified prior-candidate PNGs copied from $BeforeEvidenceRoot`nAfter-fix PNGs are in the parent directory.`n", [Text.UTF8Encoding]::new($false))
    }
    Write-Output "PASS $(@(Get-ChildItem $root -Filter '*.png').Count) native UI merged demo/state/before PNGs: $root"
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id }
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
}
