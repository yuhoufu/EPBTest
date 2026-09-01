@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
fltmc >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo 即将卸载监督服务、登录任务和桌面快捷方式。
echo 程序槽、项目数据和事故证据将保留。
set "MTTFTEST_DEPLOY_SCRIPT=%~dp0Package\Deployment\Install-MTTFTest-Unattended.ps1"
set "MTTFTEST_PACKAGE_SOURCE=%~dp0Package"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $source = Get-Content -LiteralPath $env:MTTFTEST_DEPLOY_SCRIPT -Raw -Encoding UTF8; & ([ScriptBlock]::Create($source)) -Mode Uninstall -SourceDirectory $env:MTTFTEST_PACKAGE_SOURCE -InstallRoot (Join-Path $env:ProgramFiles 'MTTFTest') } catch { Write-Error $_; exit 1 }"
if errorlevel 1 goto :failed
echo.
echo 卸载成功，程序和证据未删除。
goto :done
:failed
echo.
echo 卸载失败，请保存本窗口内容排查。
:done
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal
