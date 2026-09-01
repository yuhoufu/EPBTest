@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
fltmc >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo [1/2] 正在复核安装源身份和哈希...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Package\Deployment\Verify-Release.ps1" -ReleaseDirectory "%~dp0Package" -RequireDeploymentApproved
if errorlevel 1 goto :failed
echo [2/2] 正在修复服务、登录代理、ACL、双槽和快捷方式...
set "MTTFTEST_DEPLOY_SCRIPT=%~dp0快捷部署安装器.ps1"
set "MTTFTEST_PACKAGE_SOURCE=%~dp0Package"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $source = Get-Content -LiteralPath $env:MTTFTEST_DEPLOY_SCRIPT -Raw -Encoding UTF8; & ([ScriptBlock]::Create($source)) -Mode Repair -SourceDirectory $env:MTTFTEST_PACKAGE_SOURCE -InstallRoot (Join-Path $env:ProgramFiles 'MTTFTest') } catch { Write-Error $_; exit 1 }"
if errorlevel 1 goto :failed
echo.
echo 修复成功。
goto :done
:failed
echo.
echo 修复失败。未绕过安全门禁，请保存本窗口内容排查。
:done
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal
