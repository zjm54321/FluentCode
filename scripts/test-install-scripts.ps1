[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
$sourcePackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$repo = Split-Path -Parent $PSScriptRoot
if (!$EvidenceRoot) { $EvidenceRoot = Join-Path $repo ('artifacts\install-script-check-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Use a fresh installation-script evidence directory.' }
$copy = Join-Path $EvidenceRoot "安装 空格 & 单引号'"
New-Item -ItemType Directory -Path $copy | Out-Null
foreach ($name in @('Install.cmd', 'Install.ps1', 'FluentCode_1.2611.15.0_x64.msix', 'FluentCode-signing.cer')) {
    Copy-Item -LiteralPath (Join-Path $PackageDirectory $name) -Destination (Join-Path $copy $name)
}
$checks = [Collections.Generic.List[string]]::new()
function Assert-InstallCheck([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $checks.Add($Message)
}
foreach ($file in @((Join-Path $copy 'Install.ps1'), (Join-Path $PSScriptRoot 'write-install-scripts.ps1'), (Join-Path $PSScriptRoot 'package-msix.ps1'), $PSCommandPath)) {
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
    Assert-InstallCheck ($errors.Count -eq 0) ('PowerShell 5.1 syntax: ' + [IO.Path]::GetFileName($file))
}
$bytes = [IO.File]::ReadAllBytes((Join-Path $copy 'Install.ps1'))
Assert-InstallCheck ($bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) 'Generated Chinese installer has UTF-8 BOM'
. (Join-Path $copy 'Install.ps1') -PackageDirectory $copy
$script:ExpectedHash = $script:ExpectedPackageSha256
$script:ExpectedThumbprint = $script:ExpectedCertificateThumbprint
$script:Trusted = $false
$script:Elevated = $false
$script:Answer = 'INSTALL'
$script:UacMode = 'success'
$script:InstallFailure = $false
$script:Prompts = 0
$script:Imports = 0
$script:Installs = 0
$script:Events = [Collections.Generic.List[string]]::new()
function Test-FluentCodeElevated { return $script:Elevated }
function Test-FluentCodeCertificateTrusted { return $script:Trusted }
function Read-Host { param([string]$Prompt) $script:Prompts++; return $script:Answer }
function Start-Process {
    param($FilePath, $Verb, $ArgumentList, [switch]$Wait, [switch]$PassThru)
    $script:Imports++
    $script:Events.Add('helper')
    if ($script:UacMode -eq 'cancel') { throw [InvalidOperationException]::new('UAC cancelled.', [ComponentModel.Win32Exception]::new(1223)) }
    if ($script:UacMode -eq 'failure') { return [pscustomobject]@{ ExitCode = 1 } }
    Assert-InstallCheck ($Verb -eq 'RunAs' -and $Wait -and $PassThru) 'Only certificate helper requests UAC and is awaited'
    Assert-InstallCheck ($FilePath.EndsWith('\WindowsPowerShell\v1.0\powershell.exe')) 'Helper uses built-in Windows PowerShell'
    $script:HelperText = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($ArgumentList[-1]))
    $script:Trusted = $true
    return [pscustomobject]@{ ExitCode = 0 }
}
function Add-AppxPackage {
    [CmdletBinding()]
    param([string]$Path)
    $script:Installs++
    $script:Events.Add('install')
    Assert-InstallCheck ($Path -eq (Join-Path $copy $script:PackageFileName)) 'Normal-user install receives literal copied package path'
    Assert-InstallCheck (!$script:Elevated -and $script:Trusted) 'Install runs in original ordinary caller after trust helper'
    if ($script:InstallFailure) { throw 'Deployment failed: 0x80073D02' }
}
function Assert-Blocked([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    $before = @($script:Imports, $script:Installs, $script:Prompts)
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    Assert-InstallCheck ($null -ne $caught -and $caught -match $Pattern) $Message
    Assert-InstallCheck (($before -join ',') -eq (@($script:Imports, $script:Installs, $script:Prompts) -join ',')) ($Message + ': blocked before prompt or mutation')
}

$null = Invoke-FluentCodeInstall -Directory $copy -WhatIf
Assert-InstallCheck ($script:Prompts -eq 0 -and $script:Imports -eq 0 -and $script:Installs -eq 0) 'WhatIf validates copied folder without prompting, UAC, trust or install'
$script:ExpectedPackageSha256 = '0' * 64
Assert-Blocked { Invoke-FluentCodeInstall -Directory $copy } 'SHA256' 'Package hash mismatch is rejected'
$script:ExpectedPackageSha256 = $script:ExpectedHash
$script:ExpectedCertificateThumbprint = '0' * 40
Assert-Blocked { Invoke-FluentCodeInstall -Directory $copy } '指纹' 'Certificate thumbprint mismatch is rejected'
$script:ExpectedCertificateThumbprint = $script:ExpectedThumbprint
function Get-AuthenticodeSignature { param($LiteralPath) return [pscustomobject]@{ SignerCertificate = [pscustomobject]@{ Thumbprint = '0' * 40 } } }
Assert-Blocked { Invoke-FluentCodeInstall -Directory $copy } 'Windows' 'Native signer mismatch is rejected'
Remove-Item Function:\Get-AuthenticodeSignature
$script:Elevated = $true
Assert-Blocked { Invoke-FluentCodeInstall -Directory $copy } '管理员' 'Already-elevated installer is rejected'
$script:Elevated = $false
$script:Answer = 'no'
$status = Invoke-FluentCodeInstall -Directory $copy
Assert-InstallCheck ($status -eq 2 -and $script:Imports -eq 0 -and $script:Installs -eq 0) 'Explicit user refusal leaves trust and package unchanged'
$script:Answer = 'INSTALL'
$status = Invoke-FluentCodeInstall -Directory $copy
Assert-InstallCheck ($status -eq 0 -and ($script:Events -join ',') -eq 'helper,install') 'Successful mocked path imports only certificate before ordinary-user install'

$tokens = $null; $errors = $null
$helperAst = [Management.Automation.Language.Parser]::ParseInput($script:HelperText, [ref]$tokens, [ref]$errors)
Assert-InstallCheck ($errors.Count -eq 0) 'Encoded elevated helper parses under Windows PowerShell 5.1'
$commands = @($helperAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true) | ForEach-Object GetCommandName)
Assert-InstallCheck (@($commands | Where-Object { $_ -ne 'Write-Error' }).Count -eq 0) 'Helper has no install, child process, file, policy, root-store or data commands'
$methods = @($helperAst.FindAll({ param($node) $node -is [Management.Automation.Language.InvokeMemberExpressionAst] }, $true) | ForEach-Object { $_.Member.Value })
Assert-InstallCheck (@($methods | Where-Object { $_ -notin @('new', 'FromBase64String', 'Open', 'Find', 'Add', 'Close') }).Count -eq 0) 'Helper member calls are restricted to certificate decoding and exact store import'
Assert-InstallCheck ($script:HelperText.Contains("X509Store]::new('TrustedPeople', 'LocalMachine')") -and $script:HelperText.Contains('$store.Add($certificate)')) 'Only elevated mutation is adding public CER to LocalMachine TrustedPeople'
Assert-InstallCheck ($script:HelperText.Contains($script:ExpectedThumbprint) -and $script:HelperText.Contains("Subject -ne 'CN=mingz'")) 'Elevated helper independently pins certificate thumbprint and publisher'
$beforeImports = $script:Imports
$null = Invoke-FluentCodeInstall -Directory $copy
Assert-InstallCheck ($script:Imports -eq $beforeImports) 'Already-trusted certificate skips UAC helper'
foreach ($mode in @('cancel', 'failure')) {
    $script:Trusted = $false
    $script:UacMode = $mode
    $beforeInstalls = $script:Installs
    $caught = $null
    try { Invoke-FluentCodeInstall -Directory $copy | Out-Null } catch { $caught = $_.Exception.Message }
    Assert-InstallCheck ($null -ne $caught -and $script:Installs -eq $beforeInstalls) ('UAC ' + $mode + ' blocks package installation')
    if ($mode -eq 'cancel') { Assert-InstallCheck ($caught -match '已取消 UAC') 'Wrapped Win32 UAC cancellation has actionable Chinese explanation' }
}
$script:Trusted = $true
$script:InstallFailure = $true
$caught = $null
try { Invoke-FluentCodeInstall -Directory $copy | Out-Null } catch { $caught = $_.Exception.Message }
Assert-InstallCheck ($caught -match '0x80073D02' -and $caught -match '托盘') 'Package-in-use error explains manual app/tray exit without force-close'

# Execute the real CMD launcher only in WhatIf mode, in a separate ordinary PowerShell process.
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $env:ComSpec
$start.WorkingDirectory = $copy
$start.Arguments = '/D /C ""' + (Join-Path $copy 'Install.cmd') + '" -WhatIf <NUL"'
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEnd()
$stderr = $process.StandardError.ReadToEnd()
if (!$process.WaitForExit(120000)) { throw 'WhatIf launcher did not exit within two minutes.' }
[IO.File]::WriteAllText((Join-Path $EvidenceRoot 'launcher-whatif.log'), $stdout + $stderr, [Text.UTF8Encoding]::new($true))
Assert-InstallCheck ($process.ExitCode -eq 0 -and $stdout.Contains('Exit code: 0')) 'Real CMD syntax/exit preservation/WhatIf works from Chinese, spaces, ampersand and apostrophe path'
$installerPath = Join-Path $copy 'Install.ps1'
$installerText = [IO.File]::ReadAllText($installerPath)
[IO.File]::WriteAllText($installerPath, $installerText.Replace($script:ExpectedHash, ('0' * 64)), [Text.UTF8Encoding]::new($true))
try {
    $process = [Diagnostics.Process]::Start($start)
    $failureOutput = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
    if (!$process.WaitForExit(120000)) { throw 'Rejected-input launcher did not exit within two minutes.' }
    Assert-InstallCheck ($process.ExitCode -eq 1 -and $failureOutput.Contains('Exit code: 1')) 'Real CMD preserves validation failure exit code and pauses instead of hiding it'
} finally { [IO.File]::WriteAllText($installerPath, $installerText, [Text.UTF8Encoding]::new($true)) }
$launcherText = [IO.File]::ReadAllText((Join-Path $copy 'Install.cmd'))
Assert-InstallCheck ($launcherText.Contains('pause') -and !$launcherText.Contains('pwsh') -and !$launcherText.Contains('ExecutionPolicy')) 'Launcher pauses and uses no PowerShell 7 or execution-policy change'
$unsigned = Join-Path $EvidenceRoot 'unsigned'
New-Item -ItemType Directory -Path $unsigned | Out-Null
Copy-Item -LiteralPath (Join-Path $copy $script:CertificateFileName) -Destination $unsigned
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $copy $script:PackageFileName))
$target = [IO.Compression.ZipFile]::Open((Join-Path $unsigned $script:PackageFileName), [IO.Compression.ZipArchiveMode]::Create)
try {
    $source = $archive.GetEntry('AppxManifest.xml').Open()
    $stream = $target.CreateEntry('AppxManifest.xml').Open()
    try { $source.CopyTo($stream) } finally { $source.Dispose(); $stream.Dispose() }
} finally { $archive.Dispose(); $target.Dispose() }
$caught = $null
try {
    & (Join-Path $PSScriptRoot 'write-install-scripts.ps1') -PackagePath (Join-Path $unsigned $script:PackageFileName) -CertificatePath (Join-Path $unsigned $script:CertificateFileName) | Out-Null
} catch { $caught = $_.Exception.Message }
Assert-InstallCheck ($null -ne $caught -and !(Test-Path -LiteralPath (Join-Path $unsigned 'Install.cmd')) -and !(Test-Path -LiteralPath (Join-Path $unsigned 'Install.ps1'))) 'Unsigned package cannot generate a falsely usable launcher'
$currentHash = (Get-FileHash -LiteralPath (Join-Path $sourcePackageDirectory $script:PackageFileName) -Algorithm SHA256).Hash
Assert-InstallCheck ($currentHash -eq $script:ExpectedHash) 'Original signed MSIX SHA256 is unchanged'
$result = [pscustomobject]@{ Passed = $true; Checks = $checks.Count; PowerShell = $PSVersionTable.PSVersion.ToString(); RealTrustImported = $false; RealPackageInstalled = $false; CopiedDirectory = $copy; Sha256 = $currentHash; Assertions = $checks }
$result | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'results.json')
$result | ConvertTo-Json -Depth 4
