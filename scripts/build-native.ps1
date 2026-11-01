[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Debug', 'Release')][string]$Configuration,
    [string]$EvidenceRoot
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Native build/publish requires Windows and Visual Studio MSBuild.' }
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $name = 'native-' + $Configuration.ToLowerInvariant() + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $EvidenceRoot = Join-Path $repo ('artifacts\' + $name)
}
elseif (![IO.Path]::IsPathRooted($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repo $EvidenceRoot
}
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Native validation requires a new evidence directory.' }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio MSBuild is required; no installation is performed.' }
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (!$msbuild -or !(Test-Path -LiteralPath $msbuild)) { throw 'Visual Studio MSBuild is required; no installation is performed.' }
Push-Location $repo
try {
    & $msbuild (Join-Path $repo 'FluentCode.sln') /restore /t:Build /m /nologo /verbosity:minimal "/p:Configuration=$Configuration" /p:Platform=x64 "/flp:logfile=$EvidenceRoot\build.log;verbosity=normal;encoding=UTF-8" 2>&1 | Tee-Object (Join-Path $EvidenceRoot 'build-output.txt')
    $build = $LASTEXITCODE
    @{ BuildExitCode = $build; Msbuild = $msbuild } | ConvertTo-Json | Set-Content (Join-Path $EvidenceRoot 'results.json')
    if ($build -ne 0) { exit $build }
    $publish = Join-Path $EvidenceRoot 'publish'
    & $msbuild (Join-Path $repo 'src\FluentCode\FluentCode.csproj') /t:Publish /m /nologo /verbosity:minimal "/p:Configuration=$Configuration" /p:Platform=x64 "/p:PublishDir=$publish\" "/flp:logfile=$EvidenceRoot\publish.log;verbosity=normal;encoding=UTF-8" 2>&1 | Tee-Object (Join-Path $EvidenceRoot 'publish-output.txt')
    $published = $LASTEXITCODE
    $result = @{ BuildExitCode = $build; PublishExitCode = $published; Msbuild = $msbuild; PublishDirectory = $publish }
    $result | ConvertTo-Json | Set-Content (Join-Path $EvidenceRoot 'results.json')
    if ($published -ne 0) { exit $published }
    $required = @('FluentCode.exe', 'FluentCode.dll', 'FluentCode.pri', 'Microsoft.Terminal.Control.dll',
        'BrowserRuntime\node.exe', 'BrowserRuntime\sidecar.mjs', 'BrowserRuntime\runtime-manifest.json',
        'Assets\OpenCode\Mermaid\mermaid.min.js')
    foreach ($relative in $required) {
        if (!(Test-Path -LiteralPath (Join-Path $publish $relative))) { throw "Missing published artifact: $relative" }
    }
    foreach ($relative in @('node.exe', 'sidecar.mjs', 'runtime-manifest.json')) {
        $expected = (Get-FileHash (Join-Path $repo "artifacts\browser-runtime\$relative") -Algorithm SHA256).Hash
        $actual = (Get-FileHash (Join-Path $publish "BrowserRuntime\$relative") -Algorithm SHA256).Hash
        if ($expected -ne $actual) { throw "Published runtime differs from prepared candidate: $relative" }
    }
    $required | ForEach-Object { Get-FileHash -LiteralPath (Join-Path $publish $_) -Algorithm SHA256 } |
        Select-Object Path, Hash | ConvertTo-Json | Set-Content (Join-Path $EvidenceRoot 'publish-hashes.json')
    (Get-Item -LiteralPath (Join-Path $publish 'FluentCode.exe')).VersionInfo |
        Select-Object ProductName, CompanyName, LegalCopyright, FileDescription, Comments, FileVersion, ProductVersion |
        ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'file-properties.json')
    $result.RuntimeCopyVerified = $true
    $result | ConvertTo-Json | Set-Content (Join-Path $EvidenceRoot 'results.json')
    Write-Host "PASS native $Configuration build, publish, and runtime artifact hashes: $EvidenceRoot"
}
finally { Pop-Location }
