@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s=Get-Service -Name MTTFTestSupervisor -ErrorAction SilentlyContinue; $t=Get-ScheduledTask -TaskName MTTFTestSessionAgent -ErrorAction SilentlyContinue; Write-Host ('Supervisor service: ' + $(if($s){$s.Status}else{'Not installed'})); Write-Host ('Session agent task: ' + $(if($t){$t.State}else{'Not installed'})); Get-Process -Name 'MTTFTest','MTTFTest.Watchdog','MTTFTest.SessionAgent','MTTFTest.SafetyAgent' -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,SessionId,StartTime | Format-Table -AutoSize"
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal
