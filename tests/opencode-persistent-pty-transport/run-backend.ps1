param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
New-Item -ItemType Directory -Force $EvidenceRoot | Out-Null
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$dll = Join-Path $EvidenceRoot 'PtyBackendTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', "/out:`"$dll`"")
$arguments += Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object { '/reference:"' + $_.FullName + '"' }
@(
 'OpenCodeAuthenticationMode.cs','OpenCodeClientOptions.cs','OpenCodeExceptions.cs','OpenCodeJson.cs','OpenCodeConstants.cs',
 'OpenCodeClient.cs','OpenCodeClient.Interactions.cs','OpenCodeClient.Agents.cs','OpenCodeClient.SessionControls.cs',
 'OpenCodeDtos.SessionControls.cs','OpenCodeDtos.Common.cs','OpenCodeDtos.Location.cs','OpenCodeDtos.Sessions.cs',
 'OpenCodeDtos.Messages.cs','OpenCodeDtos.Inbox.cs','OpenCodeDtos.Permissions.cs','OpenCodeDtos.Forms.cs','OpenCodeDtos.Agents.cs',
 'OpenCodeInstructionNotice.cs','OpenCodeInstructionSource.cs','OpenCodeClient.Projects.cs','OpenCodeDtos.Projects.cs','OpenCodeClient.ServerInfo.cs',
 'OpenCodeClient.PersistentPty.cs','PersistentPtyModels.cs','PersistentPtyConnection.cs',
 'PersistentPty/PersistentPtyLifecycle.cs','PersistentPty/PersistentPtyController.cs'
) | ForEach-Object { $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Services/OpenCode" $_) + '"' }
@('OpenCodePageSource.cs','OpenCodeInteractionGate.cs','NotificationArgumentCodec.cs','NotificationInteractions.cs','NotificationSource.cs','OpenCodeExecutionProjection.cs') |
 ForEach-Object { $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Services/Notifications" $_) + '"' }
@('BackendSupport.cs','BackendTests.cs') | ForEach-Object { $arguments += '"' + (Join-Path $PSScriptRoot $_) + '"' }
# Placement regression check: the production wire closure must compile without page-source,
# renderer/lifecycle contracts, or the unrelated desktop-bridge test closure.
$wireArguments = @($arguments | Where-Object {
 $_ -notlike '/target:*' -and $_ -notlike '/out:*' -and
 $_ -notlike '*PersistentPtyLifecycle.cs*' -and $_ -notlike '*PersistentPtyController.cs*' -and
 $_ -notlike '*OpenCodePageSource.cs*' -and $_ -notlike '*BackendSupport.cs*' -and $_ -notlike '*BackendTests.cs*'
})
$wireArguments += '/target:library'
$wireArguments += '/out:"' + (Join-Path $EvidenceRoot 'PtyWireOnly.dll') + '"'
$wireRsp = Join-Path $EvidenceRoot 'wire-only.rsp'; $wireArguments | Set-Content -Encoding UTF8 $wireRsp
$rsp = Join-Path $EvidenceRoot 'backend.rsp'; $arguments | Set-Content -Encoding UTF8 $rsp
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$sourceInputs = @($arguments | Where-Object { $_ -match '^".+\.cs"$' } | ForEach-Object {
 $path = $_.Trim('"')
 $relative = $path.Substring($repo.Length + 1)
 $snapshot = Join-Path (Join-Path $EvidenceRoot 'inputs') $relative
 New-Item -ItemType Directory -Force (Split-Path $snapshot) | Out-Null
 Copy-Item -LiteralPath $path -Destination $snapshot
 [ordered]@{ path = $path; relativePath = $relative; snapshotPath = $snapshot; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{
 capturedUtc = [DateTime]::UtcNow.ToString('o'); repository = $repo
 compiler = $compiler; compilerSha256 = (Get-FileHash $compiler -Algorithm SHA256).Hash
 sdk = $sdk.Name; referencePack = $pack.Name
 wireResponseFile = $wireRsp; wireResponseSha256 = (Get-FileHash $wireRsp -Algorithm SHA256).Hash
 backendResponseFile = $rsp; backendResponseSha256 = (Get-FileHash $rsp -Algorithm SHA256).Hash
 sources = $sourceInputs
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'candidate-inputs.json')
# csc is silent on success. Write explicit UTF-8 result logs rather than leaving an old
# failure log untouched (or creating no log) when a Tee-Object pipeline receives no output.
$wireOutput = @(& $dotnet exec $compiler "@$wireRsp" 2>&1)
$wireExit = $LASTEXITCODE
$wireResult = if ($wireExit -eq 0) { 'PASS source-free wire HTTP/socket closure compiles without OpenCodePageSource or PTY lifecycle contracts.' } else { 'FAIL source-free wire compilation.' }
@("Compiler: $compiler", "ResponseFile: $wireRsp", "ResponseSHA256: $($manifest.wireResponseSha256)", $wireOutput, "CompilerExitCode: $wireExit", $wireResult) |
 Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'wire-only-compile.log')
$wireOutput | Write-Output; Write-Output $wireResult
if ($wireExit -ne 0) { throw "Source-free wire compile failed: $wireExit" }
$backendOutput = @(& $dotnet exec $compiler "@$rsp" 2>&1)
$backendExit = $LASTEXITCODE
$backendResult = if ($backendExit -eq 0) { 'PASS production-linked persistent PTY backend compilation; warnings treated as errors.' } else { 'FAIL persistent PTY backend compilation.' }
@("Compiler: $compiler", "ResponseFile: $rsp", "ResponseSHA256: $($manifest.backendResponseSha256)", $backendOutput, "CompilerExitCode: $backendExit", $backendResult) |
 Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'backend-compile.log')
$backendOutput | Write-Output; Write-Output $backendResult
if ($backendExit -ne 0) { throw "Backend compile failed: $backendExit" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'PtyBackendTests.runtimeconfig.json')
$testOutput = @(& $dotnet $dll 2>&1)
$testExit = $LASTEXITCODE
$testOutput | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'backend-results.txt')
$testOutput | Write-Output
if ($testExit -ne 0) { throw "Backend tests failed: $testExit" }
foreach ($sourceInput in $sourceInputs) {
 if ((Get-FileHash -LiteralPath $sourceInput.path -Algorithm SHA256).Hash -ne $sourceInput.sha256 -or
     (Get-FileHash -LiteralPath $sourceInput.snapshotPath -Algorithm SHA256).Hash -ne $sourceInput.sha256) {
  throw "Source input changed during focused compile/test: $($sourceInput.relativePath)"
 }
}
[ordered]@{
 completedUtc = [DateTime]::UtcNow.ToString('o'); wireCompilerExitCode = $wireExit; backendCompilerExitCode = $backendExit
 testsExitCode = $testExit; sourceInputsUnchanged = $true
 candidateManifestSha256 = (Get-FileHash (Join-Path $EvidenceRoot 'candidate-inputs.json') -Algorithm SHA256).Hash
 backendAssemblySha256 = (Get-FileHash $dll -Algorithm SHA256).Hash
 wireAssemblySha256 = (Get-FileHash (Join-Path $EvidenceRoot 'PtyWireOnly.dll') -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'candidate-results.json')
Write-Output "PASS exact source snapshots/hashes, response files and compiled candidate linkage: $EvidenceRoot"
