# Offline, production-linked DTO/input policy only. No restore/build/WinUI/network/certificate store.
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$BaselineDirectory
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$EvidenceRoot = (Resolve-Path $EvidenceRoot).ProviderPath
$scratch = Join-Path $EvidenceRoot 'temp'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $root 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw 'Cached .NET 10 SDK/reference pack required; no installation attempted.' }
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$references = @(Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | Sort-Object FullName)
$permission = Join-Path $repo 'src/AIFrontend/Services/OpenCode/OpenCodeDtos.Permissions.cs'
$jsonOptions = Join-Path $repo 'src/AIFrontend/Services/OpenCode/OpenCodeJson.cs'

function Invoke-PolicyCheck([string]$Name, [string[]]$Sources) {
    $output = Join-Path $EvidenceRoot $Name
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $assembly = Join-Path $output 'FormInputTests.dll'
    $arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
    $arguments += $references | ForEach-Object { '/reference:"' + $_.FullName + '"' }
    $arguments += $Sources | ForEach-Object { '"' + $_ + '"' }
    $rsp = Join-Path $output 'compile.rsp'
    $arguments | Set-Content -Encoding UTF8 $rsp
    & $dotnet exec $compiler "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.txt')
    if ($LASTEXITCODE -ne 0) { throw "$Name compile failed: $LASTEXITCODE" }
    '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' |
        Set-Content -Encoding UTF8 (Join-Path $output 'FormInputTests.runtimeconfig.json')
    & $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'results.txt')
    if ($LASTEXITCODE -ne 0) { throw "$Name tests failed: $LASTEXITCODE" }
    Write-Output "PASS $Name $($Sources.Count) sources, $($references.Count) references."
}

if ($BaselineDirectory) {
    Invoke-PolicyCheck 'baseline-check' @(
        (Join-Path $BaselineDirectory 'OpenCodeDtos.Forms.cs'),
        (Join-Path $BaselineDirectory 'OpenCodeInteractionPolicy.cs'), $permission, $jsonOptions,
        (Join-Path $PSScriptRoot 'Fixtures.cs'), (Join-Path $PSScriptRoot 'Baseline.cs'))
}
Invoke-PolicyCheck 'current-check' @(
    (Join-Path $repo 'src/AIFrontend/Services/OpenCode/OpenCodeDtos.Forms.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeInteractionPolicy.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeFormFieldPresentation.cs'), $permission, $jsonOptions,
    (Join-Path $PSScriptRoot 'Fixtures.cs'), (Join-Path $PSScriptRoot 'Program.cs'))
if ($BaselineDirectory) {
    $old = (Select-String -Path (Join-Path $EvidenceRoot 'baseline-check/results.txt') -Pattern '^Fixture fingerprint:').Line
    $new = (Select-String -Path (Join-Path $EvidenceRoot 'current-check/results.txt') -Pattern '^Fixture fingerprint:').Line
    if ($old -ne $new) { throw 'Baseline/current fixture fingerprint changed.' }
    Write-Output 'PASS baseline/current fixture fingerprint identical.'
}
Write-Output "Evidence directory: $EvidenceRoot"
