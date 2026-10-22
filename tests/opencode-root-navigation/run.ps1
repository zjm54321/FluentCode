# Offline .NET 10 only. Extract current production methods; never copy/fallback to saved algorithms.
# No restore, production writes, real WinUI, network, SessionReturn or store sources.
param([string]$EvidenceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-root-navigation-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null

# Exact declaration plus class-level indentation bounds. A changed signature/format, duplicate
# declaration, or missing closing boundary fails extraction; the compiler validates the result.
function Extract-Method([string]$Source, [string]$Declaration, [bool]$Expression = $false) {
    $start = '(?m)^    ' + [regex]::Escape($Declaration) + '\r?$'
    if ([regex]::Matches($Source, $start).Count -ne 1) { throw "Expected exactly one declaration: $Declaration" }
    if ($Expression) {
        $pattern = '(?m)^    ' + [regex]::Escape($Declaration) + '\r?\n        [^\r\n]+;\r?$'
    } else {
        $pattern = '(?ms)^    ' + [regex]::Escape($Declaration) + '\r?\n    \{\r?\n.*?^    \}\r?$'
    }
    $found = [regex]::Matches($Source, $pattern)
    if ($found.Count -ne 1) { throw "Method boundary extraction failed: $Declaration" }
    return $found[0].Value
}

$pagePath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.xaml.cs'
$paginationPath = Join-Path $repo 'src/AIFrontend/Pages/OpenCodePage.SessionPagination.cs'
$page = [IO.File]::ReadAllText($pagePath)
$pagination = [IO.File]::ReadAllText($paginationPath)
$methods = @(
    (Extract-Method $page 'private static bool IsRootSession(OpenCodeSessionInfo? session) =>' $true),
    (Extract-Method $page 'private string? RebuildSessionList(List<OpenCodeSessionInfo> sessions)'),
    (Extract-Method $page 'private static ListViewItem CreateSessionItem(OpenCodeSessionInfo session)'),
    (Extract-Method $page 'private void InsertSessionItemFirst(OpenCodeSessionInfo session)'),
    (Extract-Method $page 'private void SelectSessionItem(string sessionId)'),
    (Extract-Method $pagination 'private void AppendSessionItems(List<OpenCodeSessionInfo> sessions)'),
    (Extract-Method $page 'private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)')
)
$header = @'
using System;
using System.Collections.Generic;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
namespace AIFrontend.Pages;
public sealed partial class OpenCodePage
{
'@
$extracted = Join-Path $output 'OpenCodePage.Extracted.cs'
($header + "`r`n" + ($methods -join "`r`n`r`n") + "`r`n}`r`n") | Set-Content -Encoding UTF8 $extracted
Get-FileHash -LiteralPath @($pagePath, $paginationPath) -Algorithm SHA256 | Format-List | Out-String | Set-Content -Encoding UTF8 (Join-Path $output 'production-source-hashes.txt')

$assembly = Join-Path $output 'OpenCodeRootNavigationTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
# Explicit DTO dependencies, not a services glob. No client, return lane or store is compiled.
@('OpenCodeDtos.Sessions.cs', 'OpenCodeDtos.Common.cs', 'OpenCodeDtos.Location.cs', 'OpenCodeConstants.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path "$repo/src/AIFrontend/Services/OpenCode" $_) + '"'
}
$arguments += '"' + $extracted + '"'
@('OpenCodePage.TestHost.cs', 'Program.cs') | ForEach-Object {
    $arguments += '"' + (Join-Path $PSScriptRoot $_) + '"'
}
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeRootNavigationTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (7 production methods extracted; warnings treated as errors).'
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
