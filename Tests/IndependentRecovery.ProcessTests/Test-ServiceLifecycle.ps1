$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\..\Tools\Service-Lifecycle.ps1')
Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
public sealed class ServiceLifecycleFake {
 public string ServiceName { get { return "OwnedTestService"; } }
 public string Status { get; set; }
 public bool CanStop { get { return true; } }
 public string Mode;
 public int Refreshes, Stops, Starts;
 public void Refresh() { Refreshes++; if (Refreshes >= 2 && Mode == "Starting" && Status == "StartPending") Status="Running"; if (Refreshes>=2 && Mode=="Stopping") Status="Stopped"; }
 public void Stop() { Stops++; if(Mode=="Denied") throw new Win32Exception(5); if(Mode=="LateStop"){ Status="Stopped"; throw new Win32Exception(1062); } if(Mode=="Busy" && Stops==1) throw new Win32Exception(1061); Status="Stopped"; }
 public void Start() { Starts++; Status="Running"; if(Mode=="LateStart") throw new Win32Exception(1056); }
}
"@
$count=0
function Fake([string]$State,[string]$Mode){$f=[ServiceLifecycleFake]::new();$f.Status=$State;$f.Mode=$Mode;return $f}
foreach($mode in @('Normal','Starting','Stopping','LateStop','Busy')){
 $state=if($mode -eq 'Starting'){'StartPending'}elseif($mode -eq 'Stopping'){'StopPending'}else{'Running'}
 $f=Fake $state $mode
 Set-ServiceControllerStateBounded $f Stopped 1500
 if($f.Status -ne 'Stopped' -or ($mode -eq 'Stopping' -and $f.Stops -ne 0)){throw ('Stop transition failed '+$mode)}
 $count++
}
$f=Fake 'Stopped' 'Normal';Set-ServiceControllerStateBounded $f Stopped 200;if($f.Stops){throw 'Repeated stop was sent'};$count++
$f=Fake 'Stopped' 'LateStart';Set-ServiceControllerStateBounded $f Running 500;if($f.Starts -ne 1){throw 'Start retried after already running'};$count++
$f=Fake 'Running' 'Denied';$rejected=$false;try{Set-ServiceControllerStateBounded $f Stopped 500}catch{$rejected=$_.Exception.GetBaseException() -is [ComponentModel.Win32Exception] -and $_.Exception.GetBaseException().NativeErrorCode -eq 5};if(-not $rejected -or $f.Stops -ne 1){throw 'Access failure hidden or retried'};$count++
$f=Fake 'StartPending' 'Stuck';$clock=[Diagnostics.Stopwatch]::StartNew();$rejected=$false;try{Set-ServiceControllerStateBounded $f Stopped 120}catch{$rejected=$_.Exception.Message -like '*Service transition deadline*'};if(-not $rejected -or $clock.Elapsed.TotalSeconds -gt 2 -or $f.Stops){throw 'Pending transition deadline not bounded'};$count++
Write-Output ('PASS service transition races and deadline '+$count+'/'+$count+'; no SCM mutations')