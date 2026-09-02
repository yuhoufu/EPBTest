@echo off
setlocal
set "MTTFTEST_DEPLOY_SCRIPT=%~dp0QuickDeploy-Installer.ps1"
set "MTTFTEST_PACKAGE_SOURCE=%~dp0"
set "MTTFTEST_ELEVATE_TARGET=%~f0"
if defined MTTFTEST_QUICKDEPLOY_PARSE_ONLY goto :parse_only
cd /d "%~dp0"
fltmc >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:MTTFTEST_ELEVATE_TARGET -Verb RunAs"
  exit /b
)
echo Installing program, supervisor, session agent and shortcuts...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $source = Get-Content -LiteralPath $env:MTTFTEST_DEPLOY_SCRIPT -Raw -Encoding UTF8; & ([ScriptBlock]::Create($source)) -Mode Install -SourceDirectory $env:MTTFTEST_PACKAGE_SOURCE -InstallRoot (Join-Path $env:ProgramFiles 'MTTFTest') } catch { Write-Error $_; exit 1 }"
if errorlevel 1 goto :failed
echo.
echo Installation completed. Use the desktop shortcut to start V2.14.
goto :done
:failed
echo.
echo Installation failed. Save this window for diagnosis.
:done
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal
exit /b
:parse_only
if not exist "%MTTFTEST_DEPLOY_SCRIPT%" exit /b 91
if not exist "%MTTFTEST_PACKAGE_SOURCE%\MTTFTest.exe" exit /b 92
echo QUICKDEPLOY_INSTALL_PARSE_PASS
endlocal
exit /b 0
