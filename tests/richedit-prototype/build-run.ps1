[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$LogRoot,
    [switch]$Manual,
    [switch]$BuildOnly,
    [switch]$Adapter,
    [switch]$AlignmentOnly,
    [switch]$PasteOnly,
    [switch]$SkipBuild,
    [switch]$SingleAttempt
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild missing; nothing will be installed.' }
$cache = Join-Path $env:USERPROFILE '.nuget\packages'
$prefix = if ($Adapter) { 'rte-adapter' } else { 'rep1' }
# An existing global package cache is the ONLY source. No network restore, product build or solution build.
if (!$SkipBuild) {
    & $msbuild (Join-Path $PSScriptRoot 'RichEditPrototype.csproj') /restore /t:Build /m:1 /nologo /verbosity:minimal /warnaserror /p:TreatWarningsAsErrors=true /p:Configuration=Debug /p:Platform=x64 "/p:RestoreSources=$cache" /p:RestoreLockedMode=true /p:NuGetAudit=false "/flp:logfile=$LogRoot\$prefix-msbuild.log;verbosity=minimal;encoding=UTF-8"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($BuildOnly) { exit 0 }
$exe = Join-Path $PSScriptRoot 'bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\RichEditPrototype.exe'
if ($Adapter -and !$AlignmentOnly -and !$Manual) {
    # The trigger probe loads the freshly Debug-built product. Its source-generated WinRT
    # module needs the product's runtime version; start with that compatible newer runtime.
    $productRuntime = Join-Path $PSScriptRoot '../../src/FluentCode/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/WinRT.Runtime.dll'
    Copy-Item $productRuntime (Join-Path (Split-Path $exe) 'WinRT.Runtime.dll') -Force
    Write-Output ('Using product WinRT runtime: ' + [Reflection.AssemblyName]::GetAssemblyName((Resolve-Path $productRuntime)).Version)
}
$arguments = '--log "' + (Join-Path $LogRoot "$prefix-runtime.log") + '"'
if ($Adapter) { $arguments += ' --adapter' }
if ($AlignmentOnly) { $arguments += ' --alignment-only' }
if ($PasteOnly) { $arguments += ' --paste-only' }
if ($Manual) { $arguments += ' --manual' }
if ($Manual) {
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    Write-Output "Prototype PID=$($process.Id)"
    exit 0
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ComposerIdle {
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint size, time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput value);
    public static uint Milliseconds() {
        var value = new LastInput { size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref value)) throw new System.ComponentModel.Win32Exception();
        return unchecked((uint)Environment.TickCount - value.time);
    }
}
'@
$deadline = [DateTime]::UtcNow.AddMinutes(10)
$arguments += ' --input-deadline ' + $deadline.Ticks
for ($attempt = 1; $attempt -le 5; $attempt++) {
    $nextLog = [DateTime]::UtcNow
    # PasteOnly supplies DataPackageView to the shared paste handler and sends no input.
    # A lock-owning caller uses SingleAttempt: do not retain its lease while waiting/retrying.
    if ($SingleAttempt -and $Adapter -and !$PasteOnly -and [ComposerIdle]::Milliseconds() -lt 3000) {
        Write-Output 'Fixture launch not idle-ready; exit 75 so the caller releases its capture lock.'
        exit 75
    }
    while ($Adapter -and !$PasteOnly -and [ComposerIdle]::Milliseconds() -lt 3000) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for user idle (shared 10-minute budget).' }
        if ([DateTime]::UtcNow -ge $nextLog) {
            Write-Output 'waiting for idle before native fixture launch (3 seconds required)'
            $nextLog = [DateTime]::UtcNow.AddSeconds(30)
        }
        Start-Sleep -Milliseconds 500
    }
    $remaining = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
    if ($remaining -le 0) { throw 'Native fixture exhausted its shared 10-minute budget.' }
    $sessions = & quser.exe 2>&1
    $sessionExit = $LASTEXITCODE
    $sessions | Write-Output
    if ($sessionExit -ne 0 -or !($sessions | Where-Object { $_ -match '^\s*>.*\bActive\b' })) {
        throw 'Native fixture requires the current quser session to be Active.'
    }
    if (Get-Process -Name RichEditPrototype -ErrorAction SilentlyContinue) {
        throw 'An existing RichEditPrototype is running; refusing another native launch.'
    }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    Write-Output "Prototype attempt=$attempt PID=$($process.Id)"
    if (-not $process.WaitForExit($remaining)) {
        # Only this wrapper's fixture process is terminated, never a user's FluentCode.
        $process.Kill()
        throw 'Native fixture exhausted its shared 10-minute idle/run budget.'
    }
    Write-Output "Prototype exit=$($process.ExitCode)"
    Copy-Item (Join-Path $LogRoot "$prefix-runtime.log") (Join-Path $LogRoot "$prefix-runtime-attempt-$attempt.log") -Force
    if ($process.ExitCode -ne 75) { exit $process.ExitCode }
    if ($SingleAttempt) { exit 75 }
    Write-Output 'Fixture guard stopped input; waiting for idle before retrying the whole run.'
}
exit 75
