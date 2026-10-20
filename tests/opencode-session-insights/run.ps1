# Offline production-linked .NET 10 csc harness for the Phase 2 session-insights controller.
# No restore, WinUI, live OpenCode/Billion services, config or model calls.
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$BaselineDirectory
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$EvidenceRoot = (Resolve-Path $EvidenceRoot).ProviderPath
$scratch = Join-Path $EvidenceRoot 'temp'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$references = @(Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | Sort-Object FullName)
$openCode = @(
    'OpenCodeClient.cs', 'OpenCodeClient.Interactions.cs', 'OpenCodeClient.Agents.cs', 'OpenCodeClient.SessionInfo.cs',
    'OpenCodeClient.View.cs', 'OpenCodeClient.Usage.cs', 'OpenCodeClient.Capabilities.cs',
    'OpenCodeClient.BillionDiscovery.cs', 'OpenCodeDtos.BillionDiscovery.cs',
    'OpenCodeAuthenticationMode.cs', 'OpenCodeClientOptions.cs', 'OpenCodeConstants.cs', 'OpenCodeExceptions.cs',
    'OpenCodeJson.cs', 'OpenCodeInstructionNotice.cs', 'OpenCodeDtos.Common.cs', 'OpenCodeDtos.Sessions.cs',
    'OpenCodeDtos.Messages.cs', 'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Inbox.cs', 'OpenCodeDtos.Permissions.cs',
    'OpenCodeDtos.Forms.cs', 'OpenCodeDtos.Agents.cs', 'OpenCodeDtos.Models.cs', 'OpenCodeDtos.Capabilities.cs',
    'OpenCodeUsageAggregator.cs', 'OpenCodeUsageService.cs', 'OpenCodeContextEstimate.cs', 'OpenCodeSessionAncestry.cs',
    'OpenCodeCapabilityStatus.cs', 'OpenCodeTodoReplay.cs', 'OpenCodeSubagentStatus.cs'
) | ForEach-Object { Join-Path "$repo/src/AIFrontend/Services/OpenCode" $_ }
$sources = $openCode + @(
    (Join-Path $repo 'src/AIFrontend/Services/Notifications/OpenCodePageSource.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Billion/BillionContextClient.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Billion/BillionContextDtos.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Billion/BillionTelemetryReader.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeSessionInsights.cs'),
    (Join-Path $PSScriptRoot 'Program.cs'))

function Invoke-InsightsCheck([string]$Name, [string[]]$Inputs) {
    $output = Join-Path $EvidenceRoot $Name
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $assembly = Join-Path $output 'OpenCodeSessionInsightsTests.dll'
    $arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
    $arguments += $references | ForEach-Object { '/reference:"' + $_.FullName + '"' }
    $arguments += $Inputs | ForEach-Object { '"' + $_ + '"' }
    $responseFile = Join-Path $output 'compile.rsp'
    $arguments | Set-Content -Encoding UTF8 $responseFile
    $compiledInputs = @($Inputs) + @($references.FullName) + @($compiler, $dotnet, $responseFile)
    $before = @(Get-FileHash -LiteralPath $compiledInputs -Algorithm SHA256 | Select-Object Path, Hash)
    $before | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-before.json')
    Get-FileHash -LiteralPath ($Inputs + @($PSCommandPath)) -Algorithm SHA256 | Select-Object Path, Hash |
        ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes.json')
    try {
        & $dotnet exec $compiler "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
        if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
        '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' |
            Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeSessionInsightsTests.runtimeconfig.json')
        & $dotnet $assembly 2>&1 | Tee-Object -FilePath (Join-Path $output 'tests.log')
        if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
    } finally {
        $after = @(Get-FileHash -LiteralPath $compiledInputs -Algorithm SHA256 | Select-Object Path, Hash)
        $after | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'compiled-inputs-after.json')
        if (Compare-Object $before $after -Property Path, Hash) { throw "$Name compiled inputs changed during validation." }
    }
    Write-Output "PASS $Name inputs unchanged: $($Inputs.Count) sources + $($references.Count) references + compiler/host/rsp = $($compiledInputs.Count) hashes."
}

if ($BaselineDirectory) {
    $baseline = @($sources | ForEach-Object {
        $name = Split-Path $_ -Leaf
        if ($name -in @('OpenCodeSessionInsights.cs', 'BillionContextClient.cs', 'Program.cs')) { Join-Path $BaselineDirectory $name }
        else { $_ }
    })
    Invoke-InsightsCheck 'baseline' $baseline
}
Invoke-InsightsCheck 'current' ($sources + @((Join-Path $PSScriptRoot 'OriginTests.cs'), (Join-Path $PSScriptRoot 'DiscoveryTests.cs'), (Join-Path $PSScriptRoot 'ContextRefreshTests.cs')))
Write-Output "Evidence directory: $EvidenceRoot"
