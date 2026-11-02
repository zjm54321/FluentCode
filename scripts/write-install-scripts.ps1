[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$CertificatePath
)
$ErrorActionPreference = 'Stop'
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$CertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
$destination = Split-Path -Parent $PackagePath
if ((Split-Path -Parent $CertificatePath) -ne $destination) { throw 'Keep the public CER beside the signed MSIX before generating installation scripts.' }
$publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($CertificatePath))
if ($publicCertificate.Subject -ne 'CN=mingz' -or $publicCertificate.HasPrivateKey) { throw 'Expected a public-only CN=mingz CER.' }
$hash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if (!$entry) { throw 'Missing package manifest.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $version = [string]$manifest.Package.Identity.Version
    if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
} finally { $archive.Dispose() }
$templates = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging\install'
$installer = [IO.File]::ReadAllText((Join-Path $templates 'Install.ps1.in'))
$installer = $installer.Replace('__PACKAGE_FILE__', [IO.Path]::GetFileName($PackagePath).Replace("'", "''"))
$installer = $installer.Replace('__CERTIFICATE_FILE__', [IO.Path]::GetFileName($CertificatePath).Replace("'", "''"))
$installer = $installer.Replace('__PACKAGE_SHA256__', $hash).Replace('__CERTIFICATE_THUMBPRINT__', $publicCertificate.Thumbprint).Replace('__PACKAGE_VERSION__', $version)
# Dot-sourcing loads only the read-only verification helpers, never the interactive install entry point.
. ([scriptblock]::Create($installer))
$null = Get-FluentCodeInstallInputs $destination
$encoding = [Text.UTF8Encoding]::new($true)
[IO.File]::WriteAllText((Join-Path $destination 'Install.ps1'), ($installer -replace '\r?\n', "`r`n"), $encoding)
$launcher = [IO.File]::ReadAllText((Join-Path $templates 'Install.cmd')) -replace '\r?\n', "`r`n"
[IO.File]::WriteAllText((Join-Path $destination 'Install.cmd'), $launcher, [Text.Encoding]::ASCII)
[pscustomobject]@{ Launcher = Join-Path $destination 'Install.cmd'; Sha256 = $hash; CertificateThumbprint = $publicCertificate.Thumbprint } | ConvertTo-Json
