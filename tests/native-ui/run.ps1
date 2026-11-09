# Focused OpenUI Lang 1.1.0 parser conformance + local reactive runtime. No app/harness/full suite.
param([string]$EvidenceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('native-ui-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path $output) { throw 'A new evidence directory is required.' }
New-Item -ItemType Directory -Path $output | Out-Null
Write-Output "Evidence directory: $output"
$assembly = Join-Path $output 'NativeUiTests.dll'
$production = Get-ChildItem (Join-Path $repo 'src/FluentCode/Services/NativeUi/*.cs') | ForEach-Object FullName
$production += (Join-Path $repo 'src/FluentCode/Models/NativeUiMapSettings.cs')
# WinUI-free renderer rules (colour contrast, % lengths, flex shares, icon glyphs) are unit-checked directly.
$production += (Join-Path $repo 'src/FluentCode/Pages/OpenCode/NativeUi/NativeUiStyleMath.cs')
$catalog = Join-Path $repo 'src/FluentCode/Services/NativeUi/catalog.json'
# The locked map libraries target .NET 9; .NET 10 unifies their framework references.
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', '/nowarn:CS1701', ('/out:"' + $assembly + '"'), ('/resource:"' + $catalog + '",FluentCode.NativeUi.catalog.json'))
$arguments += Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
# Exercise the real tile schema and navigator headlessly, using the app's locked packages.
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$lock = Get-Content (Join-Path $repo 'src/FluentCode/packages.lock.json') -Raw | ConvertFrom-Json
$dependencies = ($lock.dependencies.PSObject.Properties | Select-Object -First 1).Value
foreach ($package in @('BruTile', 'Mapsui')) {
    $version = $dependencies.$package.resolved
    $library = Join-Path $packages ($package.ToLowerInvariant() + '/' + $version + '/lib/net9.0/' + $package + '.dll')
    if (!(Test-Path -LiteralPath $library)) { throw "Cached $package $version is required; restore the app before running focused tests." }
    $arguments += '/reference:"' + $library + '"'
    Copy-Item -LiteralPath $library -Destination $output
}
$testSources = Get-ChildItem (Join-Path $PSScriptRoot '*.cs') | ForEach-Object FullName
$arguments += ($production + $testSources) | ForEach-Object { '"' + $_ + '"' }
$response = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $response
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$response" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Native UI compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'NativeUiTests.runtimeconfig.json')
& $dotnet $assembly $repo 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
if ($LASTEXITCODE -ne 0) { throw "Native UI tests failed: $LASTEXITCODE" }
