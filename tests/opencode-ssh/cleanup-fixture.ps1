param([Parameter(Mandatory=$true)][string]$FixtureDirectory)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
$path = [IO.Path]::GetFullPath($FixtureDirectory)
$prefix = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/sshsec-fixture '))
if (!$path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refused non-owned fixture cleanup path.' }
if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
