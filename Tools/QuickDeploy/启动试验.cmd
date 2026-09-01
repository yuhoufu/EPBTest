@echo off
setlocal
chcp 65001 >nul
set "APP=%ProgramFiles%\MTTFTest\Current\MTTFTest.exe"
if not exist "%APP%" (
  echo 尚未安装 V2.14.0.0，请先运行“一键安装现场候选.cmd”。
  if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
  exit /b 2
)
start "MT EPB 试验系统" /d "%ProgramFiles%\MTTFTest\Current" "%APP%"
endlocal
