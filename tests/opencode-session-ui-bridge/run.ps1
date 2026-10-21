# Offline installed .NET 10 compiler host: no restore/install/native UI/live endpoint.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem "$root/sdk" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$output = Join-Path $PSScriptRoot ('artifacts/run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $output -Force | Out-Null
Start-Transcript -Path (Join-Path $output 'run.log') | Out-Null
try {
    function Slice([string]$text, [string]$start, [string]$end) {
        $a = $text.IndexOf($start, [StringComparison]::Ordinal)
        if ($a -lt 0) { throw "Missing extraction start: $start" }
        $b = $text.IndexOf($end, $a + $start.Length, [StringComparison]::Ordinal)
        if ($b -lt 0) { throw "Missing extraction end: $end" }
        return $text.Substring($a, $b - $a)
    }
    $appPath = "$repo/src/AIFrontend/App.OpenCode.cs"
    $app = [IO.File]::ReadAllText($appPath)
    $track = Slice $app '    public void TrackOpenCodeSession(string sessionId)' '    private void StartOpenCodeOwnership()'
    $release = Slice $app '    private async Task ReleaseOpenCodeSourceAsync()' '    private void RequireAppThread()'
    $dispatch = $app.Substring($app.IndexOf('    private void RequireAppThread()'))
    $dispatch = $dispatch.Substring(0, $dispatch.LastIndexOf('}'))
    $binding = Slice $app '            PostToApp(() =>' '        catch (OperationCanceledException)'
    # The first PostToApp in this range is the production ConfigureOpenCodeAsync publication.
    $binding = Slice $binding '                lock (_integrationSync)' '            });'
    $current = Slice $app '    public OpenCodePageSource? CurrentOpenCodePageSource' '    public IReadOnlyList<AppIntegrationDiagnostic>'
    $generated = "using System;`nusing System.Threading.Tasks;`nusing AIFrontend.Services.Notifications;`nusing AIFrontend.Services.OpenCode;`nnamespace AIFrontend;`npublic partial class App {`n$current`n$track`n$release`n$dispatch`ninternal void BridgePublishBinding(int generation, OpenCodeNotificationMonitor monitor) {`n$binding`n}`n}"
    [IO.File]::WriteAllText((Join-Path $output 'App.Extracted.cs'), $generated)

    # Exact boundary probe: the whole production UI-facts block is extracted verbatim except
    # its single UtcNow binding becomes an explicit test clock. The real Execution.cs is ALSO
    # compiled and exercised end-to-end; this probe does not substitute that implementation.
    $executionPath = "$repo/src/AIFrontend/Services/Notifications/OpenCodeNotificationMonitor.Execution.cs"
    $execution = [IO.File]::ReadAllText($executionPath)
    $facts = Slice $execution '        // UI facts keep their own original evidence times' '        if (protectedSession) projection = projection with'
    if (($facts.Split(@('var uiNow = DateTimeOffset.UtcNow;'), [StringSplitOptions]::None)).Count -ne 2) { throw 'Clock binding changed' }
    $facts = $facts.Replace('var uiNow = DateTimeOffset.UtcNow;', 'var uiNow = now;')
    $probe = @'
using System;
using AIFrontend.Services.OpenCode;
namespace AIFrontend.Services.Notifications;
public sealed partial class OpenCodeNotificationMonitor {
internal static (OpenCodeSessionUiFact? Working, OpenCodeSessionUiFact? Pending, DateTimeOffset? Completion, OpenCodeSessionUiFact? Question) BridgeBoundary(
    DateTimeOffset now, OpenCodeExecutionInput input, DateTimeOffset historyTimestamp, DateTimeOffset activeAt,
    DateTimeOffset? permissionsAt, DateTimeOffset? formsTimestamp) {
    var session = input.SessionId;
    var window = input.Window;
    var projection = OpenCodeExecutionProjection.Evaluate(input);
    var state = new StreamState { HistoryAt = historyTimestamp, PermissionsAt = permissionsAt, FormsAt = formsTimestamp,
        UnresolvedChildren = projection.KnownUnresolvedChildSessionIds };
    bool activeFresh = input.ActiveIsFresh, running = input.ForegroundRunning;
    bool inboxFresh = input.InboxIsFresh, hasInbox = input.HasInbox, protectedSession = input.HasGateProtection;
    var inboxAt = now;
    var infoAt = now;
    OpenCodeSessionInfo? info = null;
    string? outcome = null;
    double? watermark = null;
'@
    $probe += "`n$facts`nreturn (workingFact, pendingActionFact, completionObservedAt, questionPendingFact);`n}`n}"
    [IO.File]::WriteAllText((Join-Path $output 'Monitor.Boundary.Extracted.cs'), $probe)

    $assembly = Join-Path $output 'SessionUiBridge.dll'
    $args = @('/nologo', '/langversion:latest', '/nullable:enable', '/warnaserror+', '/nowarn:1701,1702', '/target:exe', ('/out:"' + $assembly + '"'))
    $args += Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll" | ForEach-Object { '/reference:"' + $_.FullName + '"' }
    $packages = Join-Path $env:USERPROFILE '.nuget/packages'
    $foundation = Get-ChildItem "$packages/microsoft.windowsappsdk.foundation" -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $windows = Get-ChildItem "$packages/microsoft.windows.sdk.net.ref" -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $refs = @("$($foundation.FullName)/lib/net6.0-windows10.0.17763.0/Microsoft.Windows.AppNotifications.Projection.dll",
        "$($foundation.FullName)/lib/net6.0-windows10.0.17763.0/Microsoft.Windows.AppNotifications.Builder.Projection.dll",
        "$($windows.FullName)/lib/net8.0/Microsoft.Windows.SDK.NET.dll", "$($windows.FullName)/lib/net8.0/WinRT.Runtime.dll")
    foreach ($ref in $refs) { $args += '/reference:"' + $ref + '"'; Copy-Item $ref $output }
    $sources = @()
    foreach ($area in @('Notifications', 'OpenCode')) {
        $sources += Get-ChildItem "$repo/src/AIFrontend/Services/$area/*.cs" | Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object FullName
    }
    foreach ($file in @('TaskbarStatus.cs', 'TaskbarStateAggregator.cs', 'TaskbarStateAggregator.Sessions.cs')) { $sources += "$repo/src/AIFrontend/Services/Taskbar/$file" }
    foreach ($file in @('App.OpenCode.Observations.cs', 'App.OpenCode.SessionState.cs', 'App.OpenCode.Diagnostics.cs')) { $sources += "$repo/src/AIFrontend/$file" }
    $sources += Get-ChildItem "$PSScriptRoot/*.cs" | ForEach-Object FullName
    $sources += Get-ChildItem "$output/*.cs" | ForEach-Object FullName
    $sources + @($appPath, $PSCommandPath) | Sort-Object -Unique | ForEach-Object {
        $hash = Get-FileHash -Algorithm SHA256 $_
        "$($hash.Hash)  $($_.Replace($repo + '\', ''))"
    } | Set-Content -Encoding UTF8 (Join-Path $output 'source-hashes.txt')
    $args += $sources | ForEach-Object { '"' + $_ + '"' }
    $rsp = Join-Path $output 'compile.rsp'
    $args | Set-Content -Encoding UTF8 $rsp
    $runtime = Join-Path $output 'SessionUiBridge.runtimeconfig.json'
    '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 $runtime
    $inputs = @($sources + @($appPath, $PSCommandPath, $rsp, $runtime, $dotnet, "$($sdk.FullName)/Roslyn/bincore/csc.dll") + $refs + @(Get-ChildItem "$output/*.dll").FullName + @(Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll").FullName) | Sort-Object -Unique
    Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'input-hashes-before.json')
    & $dotnet exec "$($sdk.FullName)/Roslyn/bincore/csc.dll" "@$rsp"
    if ($LASTEXITCODE -ne 0) { throw "Bridge compile failed: $LASTEXITCODE" }
    & $dotnet $assembly
    if ($LASTEXITCODE -ne 0) { throw "Bridge tests failed: $LASTEXITCODE" }
    Get-FileHash -LiteralPath $inputs -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'input-hashes-after.json')
    if ([IO.File]::ReadAllText((Join-Path $output 'input-hashes-before.json')) -ne [IO.File]::ReadAllText((Join-Path $output 'input-hashes-after.json'))) { throw 'Bridge inputs changed during validation.' }
    Write-Output "Evidence directory: $output"
} finally { Stop-Transcript | Out-Null }
