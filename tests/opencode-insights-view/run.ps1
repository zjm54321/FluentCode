# Offline source-linked regression for OpenCodeSessionInsightsView's persistent Billion controls.
# Compiles the REAL view file (or -ViewSource, e.g. a pre-fix copy) + real snapshot/controller sources against a
# synthetic ownership-aware WinUI shim with the .NET 10 reference pack via csc. No restore, no WinUI, no app build,
# no native launch, no network, no config/app data.
# Billion-origin additions: also compiles the REAL OpenCodePage.SessionInsights.cs plus members extracted verbatim from
# OpenCodePage.xaml.cs (SourceReady, CreateOperationToken, InvalidateForSourceChange) into a synthetic page shell
# (PageHost.cs), and records complete compiled-input hashes (sources, references, compiler, host, response file)
# before and after. -ViewSource with a pre-origin view no longer compiles against the Page partial (new events).
param(
    [string]$SourceRoot,
    [string]$ViewSource,
    [string]$SessionInsightsSource,
    [Alias('EvidenceRoot')][string]$Output
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'

$repo = if ($SourceRoot) { (Resolve-Path $SourceRoot).ProviderPath } else { (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath }
if (!$SessionInsightsSource) { $SessionInsightsSource = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.SessionInsights.cs' }
if (!$ViewSource) { $ViewSource = Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeSessionInsightsView.cs' }
if (!$Output) { $Output = Join-Path ([IO.Path]::GetTempPath()) ('opencode-insights-view-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$temp = Join-Path $Output 'tmp'
New-Item -ItemType Directory -Force -Path $temp | Out-Null
$env:TEMP = $temp
$env:TMP = $temp
Write-Output "Evidence directory: $Output"

$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }

$assembly = Join-Path $Output 'OpenCodeInsightsViewTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
# Same production dependency set as tests/opencode-session-insights/run.ps1 (snapshot records live in OpenCodeSessionInsights.cs).
$openCode = @(
    'OpenCodeClient.cs', 'OpenCodeClient.Interactions.cs', 'OpenCodeClient.Agents.cs', 'OpenCodeClient.SessionInfo.cs',
    'OpenCodeClient.View.cs', 'OpenCodeClient.Usage.cs', 'OpenCodeClient.Capabilities.cs', 'OpenCodeClient.BillionDiscovery.cs',
    'OpenCodeDesktopBridge.cs', 'OpenCodeClient.PluginRpc.cs', 'OpenCodeModelFavoritesRpc.cs',
    'OpenCodeInstructionSource.cs',
    'OpenCodeClient.Projects.cs', 'OpenCodeDtos.Projects.cs',
    'OpenCodeAuthenticationMode.cs', 'OpenCodeClientOptions.cs', 'OpenCodeConstants.cs', 'OpenCodeExceptions.cs',
    'OpenCodeJson.cs', 'OpenCodeInstructionNotice.cs', 'OpenCodeInstructionSource.cs', 'OpenCodeClient.Projects.cs', 'OpenCodeDtos.Projects.cs', 'OpenCodeDtos.Common.cs', 'OpenCodeDtos.Sessions.cs',
    'OpenCodeDtos.Messages.cs', 'OpenCodeDtos.Location.cs', 'OpenCodeDtos.Inbox.cs', 'OpenCodeDtos.Permissions.cs',
    'OpenCodeDtos.Forms.cs', 'OpenCodeDtos.Agents.cs', 'OpenCodeDtos.Models.cs', 'OpenCodeDtos.Capabilities.cs', 'OpenCodeDtos.BillionDiscovery.cs',
    'OpenCodeUsageAggregator.cs', 'OpenCodeUsageService.cs', 'OpenCodeContextEstimate.cs', 'OpenCodeSessionAncestry.cs',
    'OpenCodeCapabilityStatus.cs', 'OpenCodeTodoReplay.cs', 'OpenCodeSubagentStatus.cs'
) | ForEach-Object { Join-Path "$repo/src/AIFrontend/Services/OpenCode" $_ }
$sources = $openCode + @(
    (Join-Path $repo 'src/AIFrontend/Services/Notifications/OpenCodePageSource.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Billion/BillionContextClient.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Billion/BillionContextDtos.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/Billion/BillionTelemetryReader.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeSessionInsights.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/OpenCode/OpenCodeClient.Events.cs'),
    (Join-Path $repo 'src/AIFrontend/Services/OpenCode/OpenCodeEventReducer.cs'),
    (Resolve-Path $ViewSource).ProviderPath,
     (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeContextRing.cs'),
     (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeContextHeader.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeConnectionFlyout.cs'),
    (Resolve-Path $SessionInsightsSource).ProviderPath,
    (Join-Path $PSScriptRoot 'WinUiOwnershipShim.cs'),
    (Join-Path $PSScriptRoot 'PageHost.cs'),
    (Join-Path $PSScriptRoot 'ContextPageHost.cs'),
    (Join-Path $PSScriptRoot 'ContextPageTests.cs'),
    (Join-Path $PSScriptRoot 'AutomaticInsightsTests.cs'),
    (Join-Path $repo 'src/AIFrontend/Pages/OpenCode/OpenCodeSidebarLayout.cs'),
    (Join-Path $PSScriptRoot 'PageOriginTests.cs'),
    (Join-Path $PSScriptRoot 'TodoControllerTests.cs'),
    (Join-Path $PSScriptRoot 'Program.cs'))

# Extract complete production members verbatim (never a copied implementation); fail if missing/ambiguous.
$pagePath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.xaml.cs'
$page = [IO.File]::ReadAllText($pagePath)
$members = @()
foreach ($name in @('SourceReady', 'CreateOperationToken', 'InvalidateForSourceChange')) {
    $found = [regex]::Matches($page, ('(?m)^    private [^\r\n]*\b' + [regex]::Escape($name) + '(?:\(| =>)'))
    if ($found.Count -ne 1) { throw "Expected one production member: $name" }
    $start = $found[0].Index
    $opening = $page.IndexOf('{', $start)
    $arrow = $page.IndexOf('=>', $start)
    $block = if ($opening -ge 0 -and ($arrow -lt 0 -or $opening -lt $arrow)) {
        [regex]::Match($page.Substring($start), '(?ms)\A.*?^    \}\r?$')
    } else {
        [regex]::Match($page.Substring($start), '(?ms)\A.*?=>[^;]*;')
    }
    if (!$block.Success) { throw "Could not extract production member: $name" }
    $members += $block.Value
}
$generated = Join-Path $Output 'PageMembers.cs'
("using System;`nusing System.Threading;`nusing AIFrontend.Services.Notifications;`nnamespace AIFrontend.Pages;`npublic sealed partial class OpenCodePage {`n" +
    ($members -join "`n") + "`n}") | Set-Content -Encoding UTF8 $generated
$sources += $generated

# Actual accepted publication bodies, not copied test implementations. Surrounding cursor/UI services are labelled doubles.
$publicationMembers = @()
$publicationPaths = @((Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.EventSync.cs'), (Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.MessagePagination.cs'))
foreach ($pair in @(@($publicationPaths[0], 'DrainEventQueue'), @($publicationPaths[0], 'StopEventSync'), @($publicationPaths[1], 'ApplyRefreshedMessages'))) {
    $text = [IO.File]::ReadAllText($pair[0])
    $found = [regex]::Matches($text, ('(?m)^    private [^\r\n]*\b' + $pair[1] + '\('))
    if ($found.Count -ne 1) { throw "Expected one producer: $($pair[1])" }
    $block = [regex]::Match($text.Substring($found[0].Index), '(?ms)\A.*?^    \}\r?$')
    if (!$block.Success) { throw "Cannot extract producer: $($pair[1])" }
    $publicationMembers += $block.Value
}
$publicationGenerated = Join-Path $Output 'ContextPublicationMembers.cs'
("using System; using System.Linq; using System.Collections.Generic; using System.Diagnostics; using AIFrontend.Services.OpenCode;`nnamespace AIFrontend.Pages; public sealed partial class OpenCodePage {`n" + ($publicationMembers -join "`n") + "`n}") | Set-Content -Encoding UTF8 $publicationGenerated
$sources += $publicationGenerated

# Compile the actual visibility hook expression from layout, with production sidebar surface state.
$sidebarPath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.Sidebars.cs'
$sidebar = [IO.File]::ReadAllText($sidebarPath)
$visibilityHook = [regex]::Matches($sidebar, '(?m)^\s*OnSessionInsightsVisibilityChanged\([^;]+;')
if ($visibilityHook.Count -ne 1) { throw 'Expected one insights visibility hook in ApplySidebarLayout.' }
$sidebarGenerated = Join-Path $Output 'InsightsVisibilityHook.cs'
('using AIFrontend.Pages.OpenCode; namespace AIFrontend.Pages; public sealed partial class OpenCodePage { internal void ApplyInsightsSurface(OpenCodeSidebarSurface surface, bool hasSessionSurface = true, bool workspaceVisible = true) {' + $visibilityHook[0].Value + '} }') | Set-Content -Encoding UTF8 $sidebarGenerated
$sources += $sidebarGenerated
$publicationPaths += $sidebarPath

$references = @(Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | Sort-Object FullName)
$compiler = Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
Get-FileHash -LiteralPath ($sources + @($PSCommandPath, $pagePath) + $publicationPaths) -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Output 'source-hashes.json')
@{ CompiledSources = @($sources); ExtractedFrom = $pagePath; ExtractedMembers = @('SourceReady', 'CreateOperationToken', 'InvalidateForSourceChange'); PublicationExtractedFrom = $publicationPaths; PublicationExtractedMembers = @('DrainEventQueue', 'StopEventSync', 'ApplyRefreshedMessages'); References = $references.Count } |
    ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $Output 'input-membership.json')
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$responseFile = Join-Path $Output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
$inputs = @($sources) + @($references.FullName) + @($compiler, $dotnet, $responseFile, $pagePath) + $publicationPaths
$before = @(Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash)
$before | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Output 'compiled-inputs-before.json')
& $dotnet exec $compiler "@$responseFile" 2>&1 | Tee-Object -FilePath (Join-Path $Output 'compile.log')
$compileCode = $LASTEXITCODE
@{ Compile = $compileCode; Tests = $null } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Output 'exit-codes.json')
if ($compileCode -ne 0) { throw "Headless compilation failed: $compileCode" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $Output 'OpenCodeInsightsViewTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly 2>&1 | Out-File -Encoding utf8 (Join-Path $Output 'tests.log')
$code = $LASTEXITCODE
@{ Compile = $compileCode; Tests = $code } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Output 'exit-codes.json')
Get-Content (Join-Path $Output 'tests.log')
$after = @(Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash)
$after | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Output 'compiled-inputs-after.json')
if (Compare-Object $before $after -Property Path, Hash) { throw 'Compiled inputs changed during validation.' }
Write-Output "Compiled inputs unchanged: $($sources.Count) sources, $($references.Count) references, $($inputs.Count) hashed inputs."
Write-Output "Test exit code: $code"
exit $code
