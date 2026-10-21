param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
# Same real Page/native/core fixture owns both A6 regressions and A7 cases; not a second slot model.
& (Join-Path $PSScriptRoot '../opencode-tabs/run.ps1') -EvidenceRoot $EvidenceRoot
if($LASTEXITCODE -ne 0){throw "Integrated draft slots failed: $LASTEXITCODE"}
