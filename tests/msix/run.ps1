[CmdletBinding()]
param([string]$UnpackedDirectory, [string]$ExpectedPayloadDirectory, [string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$tokens = $null; $parseErrors = $null
$scriptAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/package-msix.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$forbidden = $scriptAst.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -in @('Import-Certificate', 'Add-AppxPackage', 'Add-AppPackage', 'Start-Process', 'Restart-Service')
}, $true)
if ($forbidden.Count) { throw 'Packaging must not install/trust/launch/elevate/restart anything.' }
$manifestPath = if ($UnpackedDirectory) { Join-Path $UnpackedDirectory 'AppxManifest.xml' } else { Join-Path $repo 'packaging/FluentCode.Package/Package.appxmanifest' }
[xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath
$ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
foreach ($entry in @{
    f = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
    uap10 = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
    desktop = 'http://schemas.microsoft.com/appx/manifest/desktop/windows10'
    com = 'http://schemas.microsoft.com/appx/manifest/com/windows10'
    rescap = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities'
}.GetEnumerator()) { $ns.AddNamespace($entry.Key, $entry.Value) }
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $ns)
Assert ($identity.Name -eq 'mingz.SST.OpenCodeDesktop.Fluent' -and $identity.Publisher -eq 'CN=mingz' -and $identity.Version -eq '1.2611.11.0' -and $identity.ProcessorArchitecture -eq 'x64') 'Unexpected package identity/version/publisher/architecture.'
$family = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily', $ns)
Assert ($family.Name -eq 'Windows.Desktop' -and $family.MinVersion -eq '10.0.19044.0' -and $family.MaxVersionTested -eq '10.0.26100.0') 'Unexpected OS contract.'
Assert ($manifest.SelectNodes('/f:Package/f:Dependencies/f:PackageDependency', $ns).Count -eq 0) 'Self-contained package must not depend on an App SDK/.NET framework package.'
$application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $ns)
Assert ($application.Executable -eq 'FluentCode.exe' -and $application.EntryPoint -eq 'Windows.FullTrustApplication' -and $application.GetAttribute('TrustLevel', $ns.LookupNamespace('uap10')) -eq 'mediumIL') 'Unexpected full-trust application entrypoint.'
$toast = $manifest.SelectSingleNode('//desktop:ToastNotificationActivation', $ns)
$server = $manifest.SelectSingleNode('//com:ExeServer', $ns)
Assert ($toast.ToastActivatorCLSID -eq '77E0A389-0925-45DF-A8D1-7B7552DAB83D' -and $server.Class.Id -eq $toast.ToastActivatorCLSID -and $server.Executable -eq 'FluentCode.exe' -and $server.Arguments -eq '----AppNotificationActivated:') 'Toast COM activation contract mismatch.'
Assert ($manifest.SelectNodes('//rescap:Capability[@Name="runFullTrust"]', $ns).Count -eq 1) 'Missing runFullTrust capability.'
Assert ($manifest.OuterXml -notmatch 'startupTask|appinstaller') 'No startup task or update endpoint is authorized.'
$checked = 0
if ($UnpackedDirectory) {
    $resourceEvidence = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('fc-msix-pri-' + [guid]::NewGuid().ToString('N')) }
    $sourcePri = if ($ExpectedPayloadDirectory) { Join-Path $ExpectedPayloadDirectory 'FluentCode.pri' } else { '' }
    & (Join-Path $PSScriptRoot 'verify-resource-index.ps1') -PriPath (Join-Path $UnpackedDirectory 'resources.pri') -SourcePriPath $sourcePri -EvidenceRoot $resourceEvidence
    foreach ($relative in @('FluentCode.exe', 'FluentCode.dll', 'resources.pri', 'Microsoft.Terminal.Control.dll', 'Microsoft.UI.Xaml.dll', 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'BrowserRuntime/node.exe', 'BrowserRuntime/sidecar.mjs', 'BrowserRuntime/node_modules/lighthouse/package.json', 'BrowserRuntime/runtime-manifest.json', 'LICENSE', 'licenses/fluentui-system-icons-LICENSE.txt', 'ThirdPartyNotices/WindowsTerminalControl-LICENSE.txt', 'ThirdPartyNotices/OpenCode.txt', 'Assets/AppIcon/app.ico', 'Assets/Square44x44Logo.png', 'Assets/Square150x150Logo.png', 'Assets/StoreLogo.png')) {
        Assert (Test-Path -LiteralPath (Join-Path $UnpackedDirectory $relative) -PathType Leaf) "Missing packaged payload: $relative"
    }
    $runtimeConfig = Get-Content -Raw (Join-Path $UnpackedDirectory 'FluentCode.runtimeconfig.json') | ConvertFrom-Json
    Assert (!$runtimeConfig.runtimeOptions.framework -and !$runtimeConfig.runtimeOptions.frameworks -and $runtimeConfig.runtimeOptions.includedFrameworks) '.NET publish is not self-contained.'
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $UnpackedDirectory 'FluentCode.exe'))
    Assert ($metadata.ProductName -eq 'FluentCode' -and $metadata.FileVersion -eq '1.2611.11.0' -and $metadata.LegalCopyright.Contains('© 2026 mingz')) 'Unexpected executable product/version/copyright.'
    if ($ExpectedPayloadDirectory) {
        foreach ($file in Get-ChildItem -LiteralPath $ExpectedPayloadDirectory -File -Recurse | Where-Object Extension -ne '.pdb') {
            $relative = [IO.Path]::GetRelativePath($ExpectedPayloadDirectory, $file.FullName)
            # WAP consumes the app PRI into resources.pri; its candidates are compared above instead.
            if ($relative -eq 'FluentCode.pri') { continue }
            $target = Join-Path $UnpackedDirectory $relative
            Assert (Test-Path -LiteralPath $target -PathType Leaf) "Payload omitted by WAP: $relative"
            Assert ((Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $target).Hash) "Payload changed by packaging: $relative"
            $checked++
        }
        $preparedRuntime = Join-Path $repo 'artifacts/browser-runtime'
        foreach ($file in Get-ChildItem -LiteralPath $preparedRuntime -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($preparedRuntime, $file.FullName)
            $target = Join-Path (Join-Path $UnpackedDirectory 'BrowserRuntime') $relative
            Assert (Test-Path -LiteralPath $target -PathType Leaf) "Prepared BrowserRuntime file omitted: $relative"
            Assert ((Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $target).Hash) "Prepared BrowserRuntime differs: $relative"
        }
    }
}
$result = @{ Passed = $true; Manifest = $manifestPath; Identity = $identity.Name; Publisher = $identity.Publisher; Version = $identity.Version; PayloadFilesCompared = $checked; InstalledOrLaunched = $false }
if ($EvidenceRoot) {
    New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
    $result | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'package-validation.json')
}
$result | ConvertTo-Json
