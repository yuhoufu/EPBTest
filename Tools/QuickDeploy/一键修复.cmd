@echo off
setlocal
set "MTTFTEST_DEPLOY_SCRIPT=%~dp0QuickDeploy-Installer.ps1"
set "MTTFTEST_PACKAGE_SOURCE=%~dp0Package"
if not exist "%MTTFTEST_PACKAGE_SOURCE%\MTTFTest.exe" set "MTTFTEST_PACKAGE_SOURCE=%~dp0."
set "MTTFTEST_ELEVATE_TARGET=%~f0"
set "MTTFTEST_PROGRAM_FILES=%ProgramFiles(x86)%"
if not defined MTTFTEST_PROGRAM_FILES set "MTTFTEST_PROGRAM_FILES=%ProgramFiles%"
if defined MTTFTEST_QUICKDEPLOY_PARSE_ONLY goto :parse_only
if defined MTTFTEST_QUICKDEPLOY_ARGUMENT_PROBE goto :argument_probe
cd /d "%~dp0"
fltmc >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:MTTFTEST_ELEVATE_TARGET -Verb RunAs"
  exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%MTTFTEST_DEPLOY_SCRIPT%" -Mode Repair -SourceDirectory "%MTTFTEST_PACKAGE_SOURCE%" -InstallRoot "%MTTFTEST_PROGRAM_FILES%\MTTFTest"
set "MTTFTEST_RESULT=%ERRORLEVEL%"
if not "%MTTFTEST_RESULT%"=="0" echo ERROR: repair failed. ExitCode=%MTTFTEST_RESULT%
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal & exit /b %MTTFTEST_RESULT%
:argument_probe
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%MTTFTEST_DEPLOY_SCRIPT%" -Mode Repair -SourceDirectory "%MTTFTEST_PACKAGE_SOURCE%" -InstallRoot "%MTTFTEST_PROGRAM_FILES%\MTTFTest"
set "MTTFTEST_RESULT=%ERRORLEVEL%"
endlocal & exit /b %MTTFTEST_RESULT%
:parse_only
if not exist "%MTTFTEST_DEPLOY_SCRIPT%" exit /b 91
if not exist "%MTTFTEST_PACKAGE_SOURCE%\MTTFTest.exe" exit /b 92
echo QUICKDEPLOY_REPAIR_PARSE_PASS
endlocal
exit /b 0
