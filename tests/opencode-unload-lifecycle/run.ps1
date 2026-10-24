# Executes verbatim production lifecycle handlers with deterministic element/service doubles.
# No WinUI runtime or desktop: isolates Loaded -> Loaded -> late Unloaded ordering.
# All runs all eleven regressions; AuditOwned selects the four formerly delegated cases (also expected to pass).
param([string]$EvidenceRoot, [string]$Case = 'All')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-unload-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
function Slice($file, $start, $end) {
    $source = [IO.File]::ReadAllText((Join-Path "$repo/src/AIFrontend" $file)).Replace("`r`n", "`n")
    $from = $source.IndexOf($start, [StringComparison]::Ordinal)
    if ($from -lt 0) { throw "Missing handler start: $file $start" }
    $to = $source.IndexOf($end, $from, [StringComparison]::Ordinal)
    if ($to -lt 0) { throw "Missing handler end: $file $end" }
    return $source.Substring($from, $to - $from)
}
$text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Program.cs'))
$text = $text.Replace('/* RICH */', (Slice 'Pages/OpenCode/OpenCodeRichTextPresenter.cs' '_root.Unloaded +=' "`n    }"))
$text = $text.Replace('/* PALETTE */', (Slice 'Pages/OpenCode/OpenCodeCodePalette.cs' '    private static void Subscribe(' '    internal static void BindSurface('))
$text = $text.Replace('/* TURN */', (Slice 'Pages/OpenCode/OpenCodeTurnChipView.cs' '_button.Loaded +=' '        Update(message);'))
$text = $text.Replace('/* SHIMMER */', (Slice 'Pages/OpenCode/OpenCodeWorkingShimmer.cs' '_host.Loaded +=' "`n    }"))
$text = $text.Replace('/* DIALOG */', (Slice 'Pages/OpenCode/OpenCodeInstructionFileDialog.cs' 'void Unloaded(object sender' '        async void Load()'))
$fonts = Slice 'Services/Appearance/AppFonts.cs' '    private sealed class ElementTracker' "`n}"
$text = $text.Replace('/* FONTS */', $fonts)
$text = $text.Replace('/* GROUP */', (Slice 'Pages/OpenCode/OpenCodeToolGroupView.cs' '_root.Loaded +=' '        ConfigureContainer();'))
$text = $text.Replace('/* INDICATOR */', (Slice 'Pages/OpenCode/OpenCodeWorkingIndicator.cs' '_label.Loaded +=' "`n    }"))
$text = $text.Replace('/* BUBBLE */', (Slice 'Pages/OpenCode/OpenCodeUserBubbleView.cs' '_bubble.Loaded +=' '        ApplyToggleText();'))
$text = $text.Replace('/* PICKER */', (Slice 'Pages/OpenCode/OpenCodePickerFlyout.cs' 'void Unloaded(object sender' "`n        try"))
$text = $text.Replace('/* BROWSER_STRIP */', (Slice 'Pages/OpenCode/OpenCodeBrowserStrip.cs' '        Loaded +=' "`n    }"))
$generated = Join-Path $output 'Lifecycle.Generated.cs'
[IO.File]::WriteAllText($generated, $text)
$dll = Join-Path $output 'UnloadLifecycle.dll'
$argsList = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $dll + '"'))
$argsList += Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$argsList += '"' + $generated + '"'
$argsList | Set-Content -Encoding UTF8 (Join-Path $output 'compile.rsp')
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') ('@' + (Join-Path $output 'compile.rsp'))
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle harness compilation failed.' }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'UnloadLifecycle.runtimeconfig.json')
& $dotnet $dll $Case | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle regression failed.' }
