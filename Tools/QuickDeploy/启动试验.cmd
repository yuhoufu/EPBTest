@echo off
setlocal
set "APP=%ProgramFiles%\MTTFTest\Current\MTTFTest.exe"
if not exist "%APP%" (
  echo V2.14.2.0 is not installed. Run the one-click installer first.
  if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
  exit /b 2
)
start "MT EPB Test V2.14" /d "%ProgramFiles%\MTTFTest\Current" "%APP%"
endlocal
