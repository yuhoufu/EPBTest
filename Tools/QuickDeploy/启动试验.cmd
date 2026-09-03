@echo off
setlocal
if defined MTTFTEST_QUICKDEPLOY_PARSE_ONLY goto :parse_only
set "MTTFTEST_PROGRAM_FILES=%ProgramFiles(x86)%"
if not defined MTTFTEST_PROGRAM_FILES set "MTTFTEST_PROGRAM_FILES=%ProgramFiles%"
set "LAUNCHER=%MTTFTEST_PROGRAM_FILES%\MTTFTest\Current\MTTFTest.Watchdog.exe"
set "APP=%MTTFTEST_PROGRAM_FILES%\MTTFTest\Current\MTTFTest.exe"
if not exist "%LAUNCHER%" (
  echo MTTFTest is not installed. Run the install command first.
  if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
  exit /b 2
)
start "MT EPB Test System" /d "%MTTFTEST_PROGRAM_FILES%\MTTFTest\Current" "%LAUNCHER%" --launch-main --main-executable "%APP%"
endlocal
exit /b 0
:parse_only
echo QUICKDEPLOY_LAUNCH_PARSE_PASS
endlocal
exit /b 0
