[CmdletBinding()]
param(
    # Optional previously downloaded official archive; its hash is still checked.
    [string]$NodeArchivePath,
    # Maintainer-only: resolve a new lock before installing. Normal builds use npm ci.
    [switch]$UpdateLock
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Browser runtime preparation requires Windows x64.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Browser runtime requires Windows x64.' }

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifacts = Join-Path $root 'artifacts'
$destination = Join-Path $artifacts 'browser-runtime'
$work = Join-Path $artifacts ('browser-runtime-prepare-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
$versions = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime-versions.json') -Raw | ConvertFrom-Json
$savedEnvironment = @{}
$oldProgress = $ProgressPreference
$oldSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol

function Invoke-PinnedNpm([string[]]$Arguments) {
    & $script:node $script:npmCli @Arguments `
        --userconfig="$script:userConfig" --globalconfig="$script:globalConfig" `
        --cache="$script:cache" --prefix="$script:payload" `
        --registry=https://registry.npmjs.org/ --ignore-scripts --omit=dev `
        --no-audit --no-fund --bin-links=false --engine-strict --update-notifier=false
    if ($LASTEXITCODE -ne 0) { throw "Pinned npm failed (exit $LASTEXITCODE)." }
}

try {
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    $baseUrl = 'https://nodejs.org/dist/v' + $versions.nodeVersion
    $shasums = Join-Path $work 'SHASUMS256.txt'
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/SHASUMS256.txt" -OutFile $shasums
    $officialLine = $versions.nodeArchiveSha256 + '  ' + $versions.nodeArchive
    $officialExeLine = $versions.nodeExeSha256 + '  win-x64/node.exe'
    $officialSums = Get-Content -LiteralPath $shasums
    if ($officialSums -notcontains $officialLine -or $officialSums -notcontains $officialExeLine) {
        throw 'Pinned Node archive/executable hashes do not match the official version SHASUMS256.txt.'
    }
    $archive = Join-Path $work $versions.nodeArchive
    if ($NodeArchivePath) {
        Copy-Item -LiteralPath (Resolve-Path -LiteralPath $NodeArchivePath).Path -Destination $archive
    }
    else {
        Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/$($versions.nodeArchive)" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $versions.nodeArchiveSha256) {
        throw 'Official Node archive SHA-256 verification failed.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, (Join-Path $work 'node'))
    $distribution = Join-Path (Join-Path $work 'node') ('node-v' + $versions.nodeVersion + '-win-x64')
    $script:node = Join-Path $distribution 'node.exe'
    $script:npmCli = Join-Path $distribution 'node_modules\npm\bin\npm-cli.js'
    if ((Get-FileHash -LiteralPath $node -Algorithm SHA256).Hash.ToLowerInvariant() -ne $versions.nodeExeSha256) {
        throw 'Extracted Node executable SHA-256 verification failed.'
    }

    # Neither npmrcs, NODE_OPTIONS, caches, prefix nor temporary files come from the user.
    # Environment changes are process-local and are restored; PATH is never changed.
    $names = @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(npm_config_|node_)' } | ForEach-Object { $_.Name })
    $names += @('TEMP', 'TMP')
    foreach ($name in ($names | Select-Object -Unique)) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    $temporary = Join-Path $work 'tmp'
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $env:TEMP = $temporary
    $env:TMP = $temporary
    $script:userConfig = Join-Path $work 'user.npmrc'
    $script:globalConfig = Join-Path $work 'global.npmrc'
    $script:cache = Join-Path $work 'npm-cache'
    [IO.File]::WriteAllText($userConfig, '')
    [IO.File]::WriteAllText($globalConfig, '')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package.json') -Destination $payload
    $lock = Join-Path $PSScriptRoot 'package-lock.json'
    if (Test-Path -LiteralPath $lock) { Copy-Item -LiteralPath $lock -Destination $payload }
    Push-Location $payload
    try {
        if ($UpdateLock) {
            Invoke-PinnedNpm -Arguments @('install', '--package-lock-only')
            Copy-Item -LiteralPath (Join-Path $payload 'package-lock.json') -Destination $lock
        }
        if (-not (Test-Path -LiteralPath $lock)) { throw 'Tracked package-lock.json is missing; use -UpdateLock once to create it.' }
        Invoke-PinnedNpm -Arguments @('ci')
    }
    finally { Pop-Location }

    Copy-Item -LiteralPath $node -Destination (Join-Path $payload 'node.exe')
    Copy-Item -LiteralPath $shasums -Destination (Join-Path $payload 'SHASUMS256.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'runtime-versions.json') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $payload
    Copy-Item -Path (Join-Path $PSScriptRoot 'runtime\*') -Destination $payload -Recurse
    $licenses = Join-Path $payload 'licenses'
    New-Item -ItemType Directory -Path $licenses | Out-Null
    Copy-Item -LiteralPath (Join-Path $distribution 'LICENSE') -Destination (Join-Path $licenses 'Node-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $distribution 'README.md') -Destination (Join-Path $licenses 'Node-README.md')
    $npmVersion = & $node -p "require(process.argv[1]).version" (Join-Path $distribution 'node_modules\npm\package.json')
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the archive-local npm version.' }
    & $node (Join-Path $PSScriptRoot 'write-notices.mjs') $payload
    if ($LASTEXITCODE -ne 0) { throw 'Third-party notice generation failed.' }
    & (Join-Path $payload 'node.exe') (Join-Path $payload 'verify-runtime.mjs') --write-manifest $npmVersion
    if ($LASTEXITCODE -ne 0) { throw 'Packaged Node/Lighthouse verification failed.' }

    # Replace only the fixed ignored output after the new candidate has passed.
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    Move-Item -LiteralPath $payload -Destination $destination
    Write-Host "Prepared offline runtime: $destination"
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    $ProgressPreference = $oldProgress
    [Net.ServicePointManager]::SecurityProtocol = $oldSecurityProtocol
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
