@echo off
setlocal
set "FLUENTCODE_INSTALL_DIR=%~dp0"
set "FLUENTCODE_INSTALL_WHATIF=0"
if /i "%~1"=="-WhatIf" set "FLUENTCODE_INSTALL_WHATIF=1"
set "FLUENTCODE_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "FLUENTCODE_POWERSHELL=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%FLUENTCODE_POWERSHELL%" -NoLogo -NoProfile -Command "try { & ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $env:FLUENTCODE_INSTALL_DIR 'Install.ps1')))) -PackageDirectory $env:FLUENTCODE_INSTALL_DIR -WhatIf:($env:FLUENTCODE_INSTALL_WHATIF -eq '1') } catch { Write-Host $_ -ForegroundColor Red; exit 1 }"
set "FLUENTCODE_INSTALL_EXIT=%ERRORLEVEL%"
echo.
echo Exit code: %FLUENTCODE_INSTALL_EXIT%
pause
exit /b %FLUENTCODE_INSTALL_EXIT%
