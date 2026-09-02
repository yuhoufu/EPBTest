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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%MTTFTEST_DEPLOY_SCRIPT%" -Mode Install -SourceDirectory "%MTTFTEST_PACKAGE_SOURCE%" -InstallRoot "%ProgramFiles%\MTTFTest"
set "MTTFTEST_RESULT=%ERRORLEVEL%"
if not "%MTTFTEST_RESULT%"=="0" echo ERROR: installation failed. ExitCode=%MTTFTEST_RESULT%
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal & exit /b %MTTFTEST_RESULT%
:parse_only
if not exist "%MTTFTEST_DEPLOY_SCRIPT%" exit /b 91
if not exist "%MTTFTEST_PACKAGE_SOURCE%\MTTFTest.exe" exit /b 92
echo QUICKDEPLOY_INSTALL_PARSE_PASS
endlocal
exit /b 0
