# TB1 shipped resources: validate the actual ICO directories and approved SVG artwork.
function Assert-IconFrames([string]$relativePath, [int[]]$sizes) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $repo $relativePath))
    if ([BitConverter]::ToUInt16($bytes, 0) -ne 0 -or
        [BitConverter]::ToUInt16($bytes, 2) -ne 1 -or
        [BitConverter]::ToUInt16($bytes, 4) -ne $sizes.Count) {
        throw "Invalid ICO directory: $relativePath"
    }
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $entry = 6 + 16 * $i
        $dimension = $sizes[$i] % 256 # ICO encodes 256 px as zero.
        $length = [BitConverter]::ToUInt32($bytes, $entry + 8)
        $offset = [BitConverter]::ToUInt32($bytes, $entry + 12)
        if ($bytes[$entry] -ne $dimension -or $bytes[$entry + 1] -ne $dimension -or
            [BitConverter]::ToUInt16($bytes, $entry + 6) -ne 32 -or
            $length -eq 0 -or $offset -lt (6 + 16 * $sizes.Count) -or
            [long]$offset + $length -gt $bytes.Length) {
            throw "Invalid $($sizes[$i]) px RGBA frame: $relativePath"
        }
    }
}

$assets = 'src/AIFrontend/Assets'
Assert-IconFrames "$assets/AppIcon/app.ico" @(16, 20, 24, 32, 40, 48, 64, 256)
$palettes = @{
    'light' = @('#F3F3F3', '#F7B500', '#0F7B0F', '#C42B1C', '#FFFFFF')
    'dark' = @('#202020', '#FCE100', '#6CCB5F', '#FF99A4', '#1A1A1A')
}
foreach ($theme in @('light', 'dark')) {
    $colors = $palettes[$theme]
    $statuses = @('attention', 'completed', 'failed')
    for ($i = 0; $i -lt $statuses.Count; $i++) {
        $status = $statuses[$i]
        Assert-IconFrames "$assets/Taskbar/$theme-taskbar/taskbar-$status.ico" @(16, 20, 24, 32)
        foreach ($size in @(16, 20, 24, 32)) {
            [xml]$svg = [IO.File]::ReadAllText((Join-Path $repo "$assets/Taskbar/source/composed/$theme-taskbar/${status}_$size.svg"))
            $circles = @($svg.svg.circle)
            $paths = @($svg.svg.path)
            $glyph = if ($status -eq 'attention') { '#1A1A1A' } else { $colors[4] }
            if ($svg.svg.width -ne "$size" -or $svg.svg.height -ne "$size" -or
                $circles[0].r -ne '7.6' -or $circles[0].fill -ne $colors[0] -or
                $circles[1].r -ne '6.5' -or $circles[1].fill -ne $colors[$i + 1] -or
                $paths[0].stroke -ne $glyph) {
                throw "TB1 badge geometry/palette mismatch: $theme/$status/$size"
            }
        }
    }
}
Write-Output 'PASS: TB1 application icon and all six circular overlay assets (sizes, RGBA frames, geometry, theme palettes).'
