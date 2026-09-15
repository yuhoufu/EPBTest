# No SCM/task mutations: exercise the production ownership gate with bounded fake records.
$ErrorActionPreference='Stop'
$global:epbTestHostService=$null
$global:epbTestHostTasks=@{}
function Get-CimInstance { param($ClassName,$Filter) if($ClassName -ne 'Win32_Service'){throw 'Unexpected process query during ownership validation'}; $global:epbTestHostService }
function Get-ScheduledTask { param($TaskPath,$TaskName,$ErrorAction) $global:epbTestHostTasks[$TaskName] }
$scriptPath=Join-Path $PSScriptRoot '..\..\Tools\Manage-SessionHost.ps1'
$root='C:\Program Files\EPB-Ownership-Test'
$count=0
function Check([bool]$Reject){
    $failed=$false
    try{& $scriptPath -Mode ValidateOwnership -InstallRoot $root|Out-Null}catch{$failed=$true;if(-not $Reject){Write-Output $_.Exception.ToString()}}
    if($failed -ne $Reject){throw ('Unexpected ownership result; reject='+$Reject)}
    $script:count++
}
Check $false
$global:epbTestHostService=[pscustomobject]@{PathName=('"'+$root+'\Current\MTTFTest.Watchdog.exe"');StartName='LocalSystem'}
$global:epbTestHostTasks['MTTFTestSessionAgent']=[pscustomobject]@{Actions=@([pscustomobject]@{Execute=$root+'\Current\MTTFTest.SessionAgent.exe'})}
Check $false
$global:epbTestHostService.PathName='"C:\Other\MTTFTest.Watchdog.exe"'
Check $true
$global:epbTestHostService.PathName='"'+$root+'\Current\MTTFTest.Watchdog.exe"'
$global:epbTestHostService.StartName='LocalService'
Check $true
$global:epbTestHostService.StartName='LocalSystem'
$global:epbTestHostTasks['MTTFTestSessionAgent'].Actions[0].Execute='C:\Other\MTTFTest.SessionAgent.exe'
Check $true
$global:epbTestHostTasks['MTTFTestSessionAgent'].Actions[0].Execute=$root+'\Current\MTTFTest.SessionAgent.exe'
$global:epbTestHostTasks['MTTFTestAutoStart']=[pscustomobject]@{Actions=@([pscustomobject]@{Execute='C:\Other\MTTFTest.exe'})}
Check $true
$global:epbTestHostTasks['MTTFTestAutoStart'].Actions[0].Execute=$root+'\Current\MTTFTest.exe'
Check $false
$global:epbTestHostTasks['MTTFTestSessionAgent'].Actions+=,[pscustomobject]@{Execute='C:\Other\helper.exe'}
Check $true
Write-Output ('PASS session host foreign ownership rejection '+$count+'/'+$count+'; no SCM or task mutations')