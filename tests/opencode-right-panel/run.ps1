# Headless pure presentation + production wiring checks; no backend or native WinUI.
param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$dll = Join-Path $EvidenceRoot 'RightPanelTests.dll'
$sources = @("$repo/src/AIFrontend/Pages/OpenCode/OpenCodeRightPanelPresentation.cs", "$PSScriptRoot/Program.cs")
$refs = @(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object FullName)
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $dll + '"'))
$arguments += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$rsp = Join-Path $EvidenceRoot 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
$inputs = @($sources + $refs + $compiler + $dotnet + $rsp + $PSCommandPath +
    "$repo/src/AIFrontend/Pages/OpenCodePage.xaml.cs" + "$repo/src/AIFrontend/Pages/OpenCodePage.RightPanelMetadata.cs" +
    "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeRightPanelView.cs" + "$repo/src/AIFrontend/Pages/OpenCodePage.MessagePagination.cs" +
    "$repo/src/AIFrontend/Pages/OpenCodePage.ConnectionFlyout.cs" + "$repo/src/AIFrontend/Pages/OpenCodePage.Sidebars.cs")
$before = @(Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path,Hash)
$before | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'inputs-before.json')
try {
    & $dotnet exec $compiler "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.log')
    if ($LASTEXITCODE -ne 0) { throw "Right panel compile failed: $LASTEXITCODE" }
    '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'RightPanelTests.runtimeconfig.json')
    & $dotnet $dll $repo 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
    if ($LASTEXITCODE -ne 0) { throw "Right panel tests failed: $LASTEXITCODE" }
} finally {
    $after = @(Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path,Hash)
    $after | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'inputs-after.json')
    if (Compare-Object $before $after -Property Path,Hash) { throw 'Right panel validation inputs changed.' }
}
Write-Output "PASS right-panel immutable inputs; evidence $EvidenceRoot"
