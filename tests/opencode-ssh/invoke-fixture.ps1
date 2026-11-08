param([Parameter(Mandatory=$true)][string]$EvidenceRoot, [Parameter(Mandatory=$true)][string]$FixtureRoot, [Parameter(Mandatory=$true)][int]$FixturePid)
$ErrorActionPreference = 'Stop'
$env:Path = 'C:\Program Files\dotnet;' + $env:Path
& (Join-Path $PSScriptRoot 'run.ps1') -EvidenceRoot $EvidenceRoot -FixtureRoot $FixtureRoot -FixturePid $FixturePid
if (!$?) { exit 1 }
exit 0
