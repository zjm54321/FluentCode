[CmdletBinding(DefaultParameterSetName = 'Unsigned')]
param(
    [string]$EvidenceRoot,
    [string]$StagingRoot = "$env:SystemDrive\fcmsix",
    [string]$PublishedPayloadDirectory,
    [Parameter(ParameterSetName = 'Unsigned')][switch]$Unsigned,
    [Parameter(Mandatory, ParameterSetName = 'Certificate')][string]$CertificateThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Development')][switch]$DevelopmentCertificate
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'MSIX packaging requires Windows, Visual Studio MSBuild/DesktopBridge, and Windows SDK 26100.' }
$repo = Split-Path -Parent $PSScriptRoot
if (!$EvidenceRoot) { $EvidenceRoot = 'artifacts\msix-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) }
if (![IO.Path]::IsPathRooted($EvidenceRoot)) { $EvidenceRoot = Join-Path $repo $EvidenceRoot }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'MSIX packaging requires a new evidence directory.' }
$StagingRoot = [IO.Path]::GetFullPath($StagingRoot)
if ($StagingRoot.Length -gt 45 -or $StagingRoot.StartsWith('\\')) { throw 'Choose a short local staging root (at most 45 characters), not a network share.' }
$stage = Join-Path $StagingRoot ([guid]::NewGuid().ToString('N').Substring(0, 8))
$payload = Join-Path $stage 'payload'
$output = Join-Path $EvidenceRoot 'package'
New-Item -ItemType Directory -Path $EvidenceRoot, $stage, $output | Out-Null
$steps = [Collections.Generic.List[object]]::new()
function Invoke-Native([string]$Name, [string]$Tool, [string[]]$Arguments, [int[]]$AllowedExitCodes = @(0)) {
    $log = Join-Path $EvidenceRoot ($Name + '.log')
    & $Tool @Arguments 2>&1 | Tee-Object -FilePath $log
    $status = $LASTEXITCODE
    $steps.Add(@{ Name = $Name; Tool = $Tool; Arguments = $Arguments; ExitCode = $status; Log = $log })
    $steps | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'commands.json')
    if ($status -notin $AllowedExitCodes) { throw "$Name failed with exit code $status. See $log" }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer/vswhere is required; no installation is performed.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
if (!(Test-Path (Join-Path $vs 'MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets'))) { throw 'Install the Visual Studio MSIX Packaging Tools component before packaging.' }
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64'
$makeappx = Join-Path $sdk 'makeappx.exe'
$signtool = Join-Path $sdk 'signtool.exe'
foreach ($tool in @($msbuild, $makeappx, $signtool)) { if (!(Test-Path -LiteralPath $tool)) { throw "Required installed tool is missing: $tool" } }
$runtime = Join-Path $repo 'artifacts\browser-runtime'
if (!(Test-Path (Join-Path $runtime 'node.exe'))) { throw 'Run tools/browser-runtime/prepare.ps1 before packaging. Packaging never prepares/downloads BrowserRuntime.' }

# Scratch output is separate from the normal framework-dependent build's bin/obj.
if ($PublishedPayloadDirectory) {
    $payload = (Resolve-Path -LiteralPath $PublishedPayloadDirectory).Path
    if (!(Test-Path (Join-Path $payload 'FluentCode.runtimeconfig.json'))) { throw 'The supplied packaged publish is incomplete.' }
    $steps.Add(@{ Name = 'reuse-publish'; Directory = $payload; ExitCode = $null })
} else {
    Invoke-Native 'publish' $msbuild @(
    (Join-Path $repo 'src\FluentCode\FluentCode.csproj'), '/restore', '/t:Publish', '/m', '/nologo', '/verbosity:minimal',
    '/p:Configuration=Release', '/p:Platform=x64', '/p:FluentCodePackaged=true',
    "/p:FluentCodeScratchRoot=$stage\build", "/p:PublishDir=$payload\"
    )
}

# The WAP project has no project reference, so Win2D's WindowsMetadataReference cannot flow
# into its manifest generator. Resolve the restored package's own property, not a cache-version pin.
Invoke-Native 'win2d-metadata' $msbuild @(
    (Join-Path $repo 'src\FluentCode\FluentCode.csproj'), '/nologo', '/p:Configuration=Release',
    '/p:Platform=x64', '/p:FluentCodePackaged=true', '/getProperty:Win2DWinMDPath'
)
$win2dWinmd = (Get-Content -LiteralPath (Join-Path $EvidenceRoot 'win2d-metadata.log') | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
if (!(Test-Path -LiteralPath $win2dWinmd -PathType Leaf)) { throw 'The restored Win2D activation metadata is missing.' }
Copy-Item -LiteralPath $win2dWinmd -Destination (Join-Path $payload 'Microsoft.Graphics.Canvas.winmd')

# The merged app PRI also indexes these project assets (including source variants/attribution).
# WAP merges that PRI into the package index and must be able to include every referenced file.
foreach ($relative in @('Assets\AppIcon', 'Assets\Taskbar')) {
    $assets = Join-Path $repo ('src\FluentCode\' + $relative)
    foreach ($file in Get-ChildItem -LiteralPath $assets -Recurse -File) {
        $target = Join-Path $payload (Join-Path $relative ([IO.Path]::GetRelativePath($assets, $file.FullName)))
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

# Record native dependencies, including Terminal's statically linked CRT and Node's real imports.
$dumpbin = Get-ChildItem (Join-Path $vs 'VC\Tools\MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'bin\Hostx64\x64\dumpbin.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$dumpbin) { throw 'Visual C++ x64 dumpbin is required for the native dependency audit.' }
Invoke-Native 'terminal-dependencies' $dumpbin @('/dependents', (Join-Path $payload 'Microsoft.Terminal.Control.dll'))
Invoke-Native 'node-dependencies' $dumpbin @('/dependents', (Join-Path $payload 'BrowserRuntime\node.exe'))
# App-local VC CRT files remove any separately installed VC runtime prerequisite for other native SDK DLLs.
$redist = Get-ChildItem (Join-Path $vs 'VC\Redist\MSVC') -Directory | Where-Object Name -Match '^\d+\.\d+' |
    Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64' } |
    ForEach-Object { Get-ChildItem $_ -Directory -Filter 'Microsoft.VC*.CRT' } | Select-Object -First 1
if (!$redist) { throw 'The installed Visual C++ x64 redistributable DLL directory is required.' }
Get-ChildItem $redist.FullName -File -Filter '*.dll' | Copy-Item -Destination $payload
@{ Source = $redist.FullName; Files = @(Get-ChildItem $redist.FullName -File -Filter '*.dll' | ForEach-Object Name) } |
    ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'vc-runtime.json')

# Keep the installed redistribution list with the app-local CRT (the VS locale may not be English).
$redistNotice = Get-ChildItem (Join-Path $vs 'Licenses') -Recurse -File -Filter 'Redist.txt' | Select-Object -First 1
if (!$redistNotice) { throw 'The installed Visual Studio redistribution list is missing.' }
Copy-Item $redistNotice.FullName (Join-Path $payload 'ThirdPartyNotices\VisualStudio-Redist.txt')
$wap = Join-Path $repo 'packaging\FluentCode.Package\FluentCode.Package.wapproj'
Invoke-Native 'wap' $msbuild @(
    $wap, '/t:Build', '/m', '/nologo', '/verbosity:minimal', '/p:Configuration=Release', '/p:Platform=x64',
    "/p:FluentCodePayloadDirectory=$payload", "/p:BaseIntermediateOutputPath=$stage\wap\obj\",
    "/p:OutputPath=$stage\wap\bin\", "/p:AppxPackageDir=$stage\packages\", '/p:AppxPackageSigningEnabled=false'
)
$packages = @(Get-ChildItem (Join-Path $stage 'packages') -Recurse -File -Filter '*.msix')
if ($packages.Count -ne 1) { throw "Expected one x64 MSIX; found $($packages.Count)." }
$package = Join-Path $output 'FluentCode_1.2611.20.0_x64.msix'
Copy-Item -LiteralPath $packages[0].FullName -Destination $package

$certificate = $null
if ($DevelopmentCertificate) {
    # Only CurrentUser\My is used. Keys are non-exportable and stay in the user's OS key store,
    # outside this repository/evidence. Nothing is imported into TrustedPeople/Root.
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq 'CN=mingz' -and $_.FriendlyName -eq 'FluentCode MSIX development' -and $_.HasPrivateKey -and
        $_.NotBefore -le (Get-Date) -and $_.NotAfter -gt (Get-Date).AddDays(1)
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (!$certificate) {
        $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=mingz' -FriendlyName 'FluentCode MSIX development' `
            -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
            -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)
    }
} elseif ($CertificateThumbprint) {
    $thumbprint = $CertificateThumbprint -replace '\s', ''
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint"
}
$signature = @{ Signed = $false; Subject = $null; Thumbprint = $null; CryptographicSignatureValid = $false; TrustInstalled = $false }
if ($certificate) {
    if ($certificate.Subject -ne 'CN=mingz' -or !$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'Signing requires an unexpired CurrentUser\My code-signing certificate with private key and subject CN=mingz.' }
    Invoke-Native 'sign' $signtool @('sign', '/fd', 'SHA256', '/s', 'My', '/sha1', $certificate.Thumbprint, $package)
    # A development certificate is intentionally not trusted on this machine. Record trust validation
    # separately from CMS cryptographic verification; never install trust to turn a red check green.
    Invoke-Native 'signature-trust' $signtool @('verify', '/pa', '/v', $package) @(0, 1)
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $archive.GetEntry('AppxSignature.p7x')
        $stream = $entry.Open(); $bytes = [IO.MemoryStream]::new()
        try { $stream.CopyTo($bytes) } finally { $stream.Dispose() }
        $data = $bytes.ToArray(); $bytes.Dispose()
        if ([Text.Encoding]::ASCII.GetString($data, 0, 4) -ne 'PKCX') { throw 'Invalid MSIX signature header.' }
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($data[4..($data.Length - 1)])
        $cms.CheckSignature($true)
        $signer = $cms.SignerInfos[0].Certificate
        if ($signer.Subject -ne 'CN=mingz' -or $signer.Thumbprint -ne $certificate.Thumbprint) { throw 'MSIX signature does not match the manifest publisher/signing certificate.' }
    } finally { $archive.Dispose() }
    Export-Certificate -Cert $certificate -FilePath (Join-Path $output 'FluentCode-signing.cer') -Type CERT | Out-Null
    $signature = @{ Signed = $true; Subject = $certificate.Subject; Thumbprint = $certificate.Thumbprint; CryptographicSignatureValid = $true; TrustInstalled = $false }
}

$unpacked = Join-Path $stage 'unpacked'
Invoke-Native 'unpack' $makeappx @('unpack', '/p', $package, '/d', $unpacked, '/o', '/l')
& (Join-Path $repo 'tests\msix\run.ps1') -UnpackedDirectory $unpacked -ExpectedPayloadDirectory $payload -EvidenceRoot $EvidenceRoot
$signature | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'signature.json')
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($package))" | Set-Content -Encoding UTF8 (Join-Path $output 'SHA256SUMS.txt')
if ($certificate) {
    & (Join-Path $repo 'scripts\write-install-scripts.ps1') -PackagePath $package -CertificatePath (Join-Path $output 'FluentCode-signing.cer')
}
$result = @{
    Passed = $true; Package = $package; Sha256 = $hash; Bytes = (Get-Item $package).Length
    StagingDirectory = $stage; PayloadDirectory = $payload; UnpackedDirectory = $unpacked
    BrowserRuntimeFiles = @(Get-ChildItem (Join-Path $unpacked 'BrowserRuntime') -File -Recurse).Count
    Signature = $signature; InstalledOrLaunched = $false
}
$result | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'results.json')
$result | ConvertTo-Json -Depth 5
