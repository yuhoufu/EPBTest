@echo off
setlocal
if defined MTTFTEST_QUICKDEPLOY_PARSE_ONLY goto :parse_only
set "APP=%ProgramFiles%\MTTFTest\Current\MTTFTest.exe"
if not exist "%APP%" (
  echo MTTFTest is not installed. Run the install command first.
  if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
  exit /b 2
)
start "MT EPB Test System" /d "%ProgramFiles%\MTTFTest\Current" "%APP%"
endlocal
exit /b 0
:parse_only
echo QUICKDEPLOY_LAUNCH_PARSE_PASS
endlocal
exit /b 0
