@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
fltmc >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo [1/2] 正在验证完整包身份和哈希...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Package\Deployment\Verify-Release.ps1" -ReleaseDirectory "%~dp0Package" -RequireDeploymentApproved
if errorlevel 1 goto :failed
echo [2/2] 正在安装监督服务、登录代理、双槽和桌面快捷方式...
set "MTTFTEST_DEPLOY_SCRIPT=%~dp0快捷部署安装器.ps1"
set "MTTFTEST_PACKAGE_SOURCE=%~dp0Package"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $source = Get-Content -LiteralPath $env:MTTFTEST_DEPLOY_SCRIPT -Raw -Encoding UTF8; & ([ScriptBlock]::Create($source)) -Mode Install -SourceDirectory $env:MTTFTEST_PACKAGE_SOURCE -InstallRoot (Join-Path $env:ProgramFiles 'MTTFTest') } catch { Write-Error $_; exit 1 }"
if errorlevel 1 goto :failed
echo.
echo 安装成功。请双击桌面的“MT EPB 试验系统 V2.14”。
goto :done
:failed
echo.
echo 安装失败。系统未绕过包身份或安全门禁，请保存本窗口内容排查。
:done
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal
