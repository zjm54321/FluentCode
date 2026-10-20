# Same headless .NET 10 pattern as the existing tests; no restore, UI, or new dependencies.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = Join-Path ([IO.Path]::GetTempPath()) ('opencode-execution-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$assembly = Join-Path $output 'OpenCodeExecutionTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$sources = @(
    'OpenCode/OpenCodeDtos.Messages.cs',
    'OpenCode/OpenCodeDtos.Common.cs',
    'OpenCode/OpenCodeDtos.Sessions.cs',
    'OpenCode/OpenCodeDtos.Location.cs',
    'OpenCode/OpenCodeConstants.cs',
    'OpenCode/OpenCodeJson.cs',
    'OpenCode/OpenCodeExceptions.cs',
    'Notifications/NotificationArgumentCodec.cs',
    'Notifications/NotificationActivationInfo.cs',
    'Notifications/NotificationSource.cs',
    'Notifications/OpenCodeExecutionProjection.cs'
)
$arguments += $sources | ForEach-Object { '"' + (Join-Path $repo ('src/AIFrontend/Services/' + $_)) + '"' }
$arguments += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'
$responseFile = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $responseFile
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "Headless compilation failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'OpenCodeExecutionTests.runtimeconfig.json')
Write-Output 'Headless compilation passed (warnings treated as errors).'
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw "Headless tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
