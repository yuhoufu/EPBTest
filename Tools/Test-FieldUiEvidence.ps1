#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$RealEvidencePath)
$ErrorActionPreference='Stop'
$reader=Join-Path $PSScriptRoot 'Read-FieldUiEvidence.ps1'
$r=& $reader -Path $RealEvidencePath -ExpectedProcessId 1972|ConvertFrom-Json
if(-not $r.WindowWasObserved -or $r.VisibleMatchingWindows[0] -notlike '*V3.0.0.0*' -or $r.LiveHealthProven -or $r.RollbackAuthorized){throw 'RealEvidenceRegression'}
$r=& $reader -Path $RealEvidencePath -ExpectedProcessId 999999|ConvertFrom-Json
if($r.WindowWasObserved -or $r.RollbackAuthorized){throw 'WrongPidRegression'}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('UiEvidence-'+[guid]::NewGuid().ToString('N')+'.json')
foreach($bad in @('null','[]','{}','[{"Name":"x","Type":"ControlType.Window","Offscreen":"false","Enabled":true}]')){
    [IO.File]::WriteAllText($temp,$bad,[Text.UTF8Encoding]::new($true))
    try{& $reader -Path $temp -ExpectedProcessId 1972;throw 'ExpectedRejection'}catch{if($_.Exception.Message -notlike 'UiEvidence*'){throw}}
}
'PASS 6/6 including actual field JSON; no remote changes'
