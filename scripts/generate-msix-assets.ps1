[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'src\FluentCode\Assets\AppIcon'
$output = Join-Path $repo 'packaging\FluentCode.Package\Assets'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Add-Type -AssemblyName System.Drawing

function Resize-ApprovedIcon([int]$Size, [string]$Name) {
    $input = Join-Path $source "app-$Size.png"
    if (!(Test-Path -LiteralPath $input)) { $input = Join-Path $source 'app-256.png' }
    $image = [Drawing.Image]::FromFile($input)
    $bitmap = [Drawing.Bitmap]::new($Size, $Size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($image, 0, 0, $Size, $Size)
        $bitmap.Save((Join-Path $output $Name), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose(); $image.Dispose() }
}

# Mechanical resizes of the approved icon only; no new artwork or background.
foreach ($scale in @(100, 200, 400)) {
    $qualifier = if ($scale -eq 100) { '' } else { ".scale-$scale" }
    foreach ($logo in @(@('Square44x44Logo', 44), @('Square150x150Logo', 150), @('StoreLogo', 50))) {
        Resize-ApprovedIcon ([int]($logo[1] * $scale / 100)) ($logo[0] + $qualifier + '.png')
    }
}
foreach ($size in @(16, 20, 24, 32, 48, 256)) {
    foreach ($form in @('', '_altform-unplated', '_altform-lightunplated')) {
        Resize-ApprovedIcon $size "Square44x44Logo.targetsize-$size$form.png"
    }
}
Write-Output "Prepared MSIX assets from the approved AppIcon variants: $output"
