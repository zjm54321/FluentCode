param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$assembly = Join-Path $EvidenceRoot 'OpenCodeWslQuickAccessTests.dll'
$sources = @(Get-ChildItem (Join-Path $repo 'src/AIFrontend/Models/*.cs') | ForEach-Object FullName)
$sources += Join-Path $repo 'src/AIFrontend/Services/OpenCode/OpenCodeClientOptions.cs'
$sources += Join-Path $repo 'src/AIFrontend/Services/OpenCode/OpenCodeAuthenticationMode.cs'
$sources += Join-Path $repo 'src/AIFrontend/Services/Appearance/AppFontSettings.cs'
$sources += Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeWslQuickAccess.cs'
$sources += Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeWslQuickAccess.PathMapping.cs'
$sources += Join-Path $repo 'src/AIFrontend/Pages/OpenCode/WslQuickAccessEditorPreference.cs'
$sources += Join-Path $repo 'src/AIFrontend/Pages/OpenCode/WslQuickAccessHome.cs'
$sources += Join-Path $PSScriptRoot 'Program.cs'
$refs = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object FullName)
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += $refs | ForEach-Object { '/reference:"' + $_ + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$responseFile = Join-Path $EvidenceRoot 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
& $dotnet exec $compiler "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt')
if ($LASTEXITCODE -ne 0) { throw "WSL quick-access compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'OpenCodeWslQuickAccessTests.runtimeconfig.json')
& $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "WSL quick-access tests failed: $LASTEXITCODE" }
$launcherSource = Get-Content -Raw (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeWslQuickAccess.Windows.cs')
if ($launcherSource -match 'LaunchFolderPathAsync|StorageFolder' -or $launcherSource -notmatch 'Start\(OpenCodeWslQuickAccess.ExplorerLaunch\(target,') { throw 'Explorer must use the mapped native launch command directly.' }
Write-Output 'PASS native Explorer route source contract (no host application launched)'
$viewSource = Get-Content -Raw (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeWslQuickAccessView.cs')
foreach ($pattern in @('SplitButton _editor', '_editor.Click \+=', '_editor.Flyout = _editors', 'new RadioMenuFlyoutItem', 'foreach \(var editor in choices.Installed\)', 'IsChecked = editor.Name == _currentEditor\?\.Name')) {
    if ($viewSource -notmatch $pattern) { throw "Native editor SplitButton/menu contract missing: $pattern" }
}
if ($viewSource -match 'ToggleMenuFlyoutItem') { throw 'Editor choice must use native radio items.' }
Write-Output 'PASS native SplitButton/menu source contract; detection/preference tested in pure projection'
Write-Output "PASS WSL quick access; evidence: $EvidenceRoot"
