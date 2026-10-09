@echo off
setlocal
if defined MTTFTEST_QUICKDEPLOY_PARSE_ONLY goto :parse_only
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s=Get-Service -Name MTTFTestSupervisor -ErrorAction SilentlyContinue; $t=Get-ScheduledTask -TaskName MTTFTestSessionAgent -ErrorAction SilentlyContinue; $a=Get-ScheduledTask -TaskName MTTFTestAutoStart -ErrorAction SilentlyContinue; Write-Host ('Supervisor service: ' + $(if($s){$s.Status}else{'Not installed'})); Write-Host ('SessionAgent task: ' + $(if($t){$t.State}else{'Not installed'})); Write-Host ('AutoStart task: ' + $(if($a){$a.State}else{'Not installed'})); $p=Get-Process -Name 'MTTFTest','MTTFTest.Watchdog','MTTFTest.SessionAgent','MTTFTest.SafetyAgent' -ErrorAction SilentlyContinue; $p | Select-Object ProcessName,Id,SessionId,StartTime,Path | Format-Table -AutoSize; exit 0"
set "MTTFTEST_RESULT=%ERRORLEVEL%"
if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause
endlocal & exit /b %MTTFTEST_RESULT%
:parse_only
echo QUICKDEPLOY_STATUS_PARSE_PASS
endlocal
exit /b 0
