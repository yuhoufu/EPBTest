@echo off
setlocal
set "MTTFTEST_DEPLOY_SCRIPT=%~dp0QuickDeploy-Installer.ps1"
set "MTTFTEST_PACKAGE_SOURCE=%~dp0."
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
if defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE goto :uninstall_without_confirm
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%MTTFTEST_DEPLOY_SCRIPT%" -Mode Uninstall -SourceDirectory "%MTTFTEST_PACKAGE_SOURCE%" -InstallRoot "%MTTFTEST_PROGRAM_FILES%\MTTFTest"
goto :uninstall_finished
:uninstall_without_confirm
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%MTTFTEST_DEPLOY_SCRIPT%" -Mode Uninstall -SourceDirectory "%MTTFTEST_PACKAGE_SOURCE%" -InstallRoot "%MTTFTEST_PROGRAM_FILES%\MTTFTest" -ForceUninstall -Confirm:$false
:uninstall_finished
set "MTTFTEST_RESULT=%ERRORLEVEL%"
if not "%MTTFTEST_RESULT%"=="0" echo ERROR: uninstall failed or cancelled. ExitCode=%MTTFTEST_RESULT%
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal & exit /b %MTTFTEST_RESULT%
:argument_probe
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%MTTFTEST_DEPLOY_SCRIPT%" -Mode Uninstall -SourceDirectory "%MTTFTEST_PACKAGE_SOURCE%" -InstallRoot "%MTTFTEST_PROGRAM_FILES%\MTTFTest"
set "MTTFTEST_RESULT=%ERRORLEVEL%"
endlocal & exit /b %MTTFTEST_RESULT%
:parse_only
if not exist "%MTTFTEST_DEPLOY_SCRIPT%" exit /b 91
if not exist "%MTTFTEST_PACKAGE_SOURCE%" exit /b 92
echo QUICKDEPLOY_UNINSTALL_PARSE_PASS
endlocal
exit /b 0
