@echo off
setlocal
chcp 65001 >nul
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
echo 警告：即将删除 C:\Program Files\MTTFTest 下的程序、服务、计划任务和快捷方式。
echo C:\ProgramData\MTTFTest 下的配置、日志和事故证据会保留。
echo 卸载前必须先安全停止试验并完全退出主程序。
choice /C YN /N /M "确认继续卸载？[Y/N] "
if errorlevel 2 goto :cancelled
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $source = Get-Content -LiteralPath $env:MTTFTEST_DEPLOY_SCRIPT -Raw -Encoding UTF8; & ([ScriptBlock]::Create($source)) -Mode Uninstall -SourceDirectory $env:MTTFTEST_PACKAGE_SOURCE -InstallRoot (Join-Path $env:ProgramFiles 'MTTFTest') } catch { Write-Error $_; exit 1 }"
if errorlevel 1 goto :failed
echo.
echo 卸载完成。程序已删除，ProgramData 配置、日志和事故证据已保留。
set "MTTFTEST_RESULT=0"
goto :done
:cancelled
echo.
echo 已取消卸载，未做任何更改。
set "MTTFTEST_RESULT=2"
goto :done
:failed
echo.
echo 卸载失败，请保留本窗口信息用于排查。
set "MTTFTEST_RESULT=1"
:done
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal & exit /b %MTTFTEST_RESULT%
:parse_only
if not exist "%MTTFTEST_DEPLOY_SCRIPT%" exit /b 91
if not exist "%MTTFTEST_PACKAGE_SOURCE%" exit /b 92
echo QUICKDEPLOY_UNINSTALL_PARSE_PASS
endlocal
exit /b 0
