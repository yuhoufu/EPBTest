$ErrorActionPreference='Stop'
$work=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root=Join-Path $env:EPB_TEST_ARTIFACT_ROOT ('topbundle-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
foreach($name in @('Base/MTTFTest.exe','Base/MTTFTest.SafetyAgent.exe','Base/MTTFTest.Watchdog.Protocol.dll','Base/Deployment/Install-MTTFTest-Unattended.ps1',
 'FallbackGuard/MTTFTest.FallbackGuard.exe','FallbackGuard/MTTFTest.Watchdog.Protocol.dll','FallbackGuard/System.Data.SQLite.dll','FallbackGuard/x86/SQLite.Interop.dll')){
 $p=Join-Path $root $name;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))|Out-Null;[IO.File]::WriteAllText($p,'VALIDATION-ONLY-NOT-EXECUTABLE')
}
foreach($name in @('Install-AutomaticRecoveryBundle.ps1','RecoveryGuard-Acceptance.ps1','Tools/Install-IndependentRecoveryBundle.ps1','Tools/Manage-IndependentRecovery.ps1','Tools/Manage-SessionHost.ps1','Tools/Service-Lifecycle.ps1','Tools/Export-IndependentRecoveryEvidence.ps1')){
 $p=Join-Path $root $name;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))|Out-Null
 [IO.File]::Copy((Join-Path $work ('Tools/'+[IO.Path]::GetFileName($name))),$p,$false)
}
$files=@(Get-ChildItem $root -File -Recurse|ForEach-Object{[ordered]@{path=$_.FullName.Substring($root.Length).TrimStart('\').Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash}})
$manifest=[ordered]@{schemaVersion=2;version='4.1.0.0';recoveryArchitecture='V4-Independent-SystemExecutor';files=$files}
[IO.File]::WriteAllText((Join-Path $root 'automatic-bundle.json'),($manifest|ConvertTo-Json -Depth 4))
& "$env:SystemRoot\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'Install-AutomaticRecoveryBundle.ps1') -Mode ValidatePackage
if($LASTEXITCODE -ne 0){throw 'Top-level validation failed'}
$reportPath=Join-Path $root 'acceptance-result.json'
& "$env:SystemRoot\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'RecoveryGuard-Acceptance.ps1') -OutputPath $reportPath
if($LASTEXITCODE -ne 0){throw 'Acceptance command failed'}
$report=[IO.File]::ReadAllText($reportPath)|ConvertFrom-Json
if($report.recoveryArchitecture -ne 'V4-Independent-SystemExecutor' -or $report.separateRecoveryGuard -ne 'Independent-SystemExecutor' -or
    $report.hardwareActions -ne 'NOT_VERIFIED' -or $report.countersAndPersistence -ne 'NOT_VERIFIED'){throw 'Acceptance misreported architecture or hardware coverage'}
Write-Output ('PASS top-level independent validation; NO INSTALLATION; '+$root)
