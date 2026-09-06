@echo off
setlocal DisableDelayedExpansion
set "MTTFTEST_HELPER=%~dp0Stop-RelatedProcesses.ps1"
set "MTTFTEST_ELEVATE_TARGET=%~f0"
if defined MTTFTEST_QUICKDEPLOY_PARSE_ONLY goto :parse_only
fltmc >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $child = Start-Process -FilePath $env:MTTFTEST_ELEVATE_TARGET -Verb RunAs -PassThru -Wait; exit $child.ExitCode } catch { Write-Error $_; exit 1223 }"
  exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%MTTFTEST_HELPER%" -Mode Stop
set "MTTFTEST_RESULT=%ERRORLEVEL%"
if not "%MTTFTEST_RESULT%"=="0" echo BLOCKED: ExitCode=%MTTFTEST_RESULT%
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal & exit /b %MTTFTEST_RESULT%
:parse_only
if not exist "%MTTFTEST_HELPER%" exit /b 91
echo QUICKDEPLOY_MAINTENANCE_PARSE_PASS
exit /b 0
