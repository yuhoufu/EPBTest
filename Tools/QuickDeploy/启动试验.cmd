@echo off
setlocal
chcp 65001 >nul
set "APP=%ProgramFiles%\MTTFTest\Current\MTTFTest.exe"
if not exist "%APP%" (
  echo 尚未安装 MT EPB 试验系统，请先运行“一键安装正式版.cmd”。
  if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
  exit /b 2
)
start "MT EPB 试验系统" /d "%ProgramFiles%\MTTFTest\Current" "%APP%"
endlocal
