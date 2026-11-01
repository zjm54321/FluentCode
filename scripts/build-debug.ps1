[CmdletBinding()]
param([string]$EvidenceRoot)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-native.ps1') -Configuration Debug -EvidenceRoot $EvidenceRoot
exit $LASTEXITCODE
