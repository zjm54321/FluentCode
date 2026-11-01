param([string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$project = Join-Path $PSScriptRoot '..\..\src\ThirdParty\WindowsTerminalControl\src\WindowsTerminalControl.WinUI\WindowsTerminalControl.WinUI.csproj'
& dotnet.exe build $project -nologo -verbosity:minimal "-p:FluentCodeScratchRoot=$EvidenceRoot" -p:Platform=x64 -p:RuntimeIdentifier=win-x64 2>&1 | Tee-Object (Join-Path $EvidenceRoot 'build.txt')
exit $LASTEXITCODE
