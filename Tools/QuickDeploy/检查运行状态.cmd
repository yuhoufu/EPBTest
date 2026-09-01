@echo off
setlocal
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s=Get-Service -Name MTTFTestSupervisor -ErrorAction SilentlyContinue; $t=Get-ScheduledTask -TaskName MTTFTestSessionAgent -ErrorAction SilentlyContinue; Write-Host ('监督服务：' + $(if($s){$s.Status}else{'未安装'})); Write-Host ('登录代理：' + $(if($t){$t.State}else{'未安装'})); Get-Process -Name 'MTTFTest','MTTFTest.Watchdog','MTTFTest.SessionAgent','MTTFTest.SafetyAgent' -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,SessionId,StartTime | Format-Table -AutoSize"
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal
