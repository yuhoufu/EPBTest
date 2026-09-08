#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Install','Repair','ValidatePackage','Validate')][string]$Mode='Install',
    [string]$AcceptanceReportPath='',
    [ValidateSet('RecoverExited','RecoverStalled')][string]$RecoveryMode='RecoverExited')
$ErrorActionPreference='Stop'
function Resolve-AutomaticRepairMode([string]$Action,[string]$RequestedMode,[bool]$ExplicitMode,[string]$SettingsPath) {
    if($Action -ne 'Repair' -or $ExplicitMode -or -not (Test-Path -LiteralPath $SettingsPath)){return $RequestedMode}
    $installed=Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8|ConvertFrom-Json
    switch -CaseSensitive ([string]$installed.Mode) {
        '1' {return 'RecoverExited'}
        'RecoverExited' {return 'RecoverExited'}
        '2' {return 'RecoverStalled'}
        'RecoverStalled' {return 'RecoverStalled'}
        '0' {return $RequestedMode}
        'ObserveOnly' {return $RequestedMode}
        default {throw 'InstalledRecoveryModeInvalid: 无法识别已安装恢复模式，禁止修复覆盖。'}
    }
}
$RecoveryMode=Resolve-AutomaticRepairMode $Mode $RecoveryMode ($PSBoundParameters.ContainsKey('RecoveryMode')) (Join-Path $env:ProgramData 'MTTFTestRecoveryGuard\guard-settings.json')
$bundle=$PSScriptRoot
$manifest=Get-Content (Join-Path $bundle 'automatic-bundle.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($manifest.schemaVersion -ne 1 -or $manifest.recoveryMode -cne 'RecoverExited' -or
    $manifest.fieldAcceptanceRequired -isnot [bool] -or -not $manifest.fieldAcceptanceRequired){throw 'AutomaticBundleIdentityInvalid'}
if(@($manifest.supportedRecoveryModes) -cnotcontains $RecoveryMode){throw 'AutomaticBundleModeUnsupported'}
$names=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($file in $manifest.files){
    $path=[IO.Path]::GetFullPath((Join-Path $bundle $file.name))
    if([IO.Path]::IsPathRooted($file.name) -or $file.name.Contains(':') -or
        -not $path.StartsWith($bundle.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
        -not $names.Add($file.name.Replace('\','/')) -or (Get-FileHash -LiteralPath $path).Hash -ine $file.sha256){throw 'AutomaticBundleFileMismatch'}
}
$actual=@(Get-ChildItem $bundle -File -Recurse|Where-Object FullName -ne (Join-Path $bundle 'automatic-bundle.json'))
if($actual.Count -ne $names.Count){throw 'AutomaticBundleUnexpectedFiles'}
$base=Join-Path $bundle 'Base'
& (Join-Path $base 'Verify-FieldPackage.ps1') 6>$null | Out-Null
Write-Host "基础组件校验通过；本安装入口使用 $RecoveryMode 自动恢复模式。"
$main=Join-Path $base 'Package'; $guard=Join-Path $base 'Guard'
$identity=Get-Content (Join-Path $guard 'guard-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
$mainId=Get-Content (Join-Path $main 'build-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($identity.gitCommit -cne $mainId.gitCommit -or $identity.gitDirty -ne $false -or
    $identity.deliveryStage -cne 'ObserveOnlyCommissioning' -or $identity.schemaVersion -ne 2){throw 'AutomaticBaseIdentityInvalid'}
if($Mode -eq 'ValidatePackage'){
    [pscustomobject]@{PackageValidated=$true;RecoveryMode=$RecoveryMode;FieldAcceptanceRequired=$true;TasksChanged=$false}|ConvertTo-Json
    return
}
if([string]::IsNullOrWhiteSpace($AcceptanceReportPath)){
    throw '尚未提供现场验收报告。请按说明使用 -AcceptanceReportPath 指定报告；不会安装观察模式或启用执行任务。'
}
$reportPath=[IO.Path]::GetFullPath($AcceptanceReportPath)
$reportBytes=[IO.File]::ReadAllBytes($reportPath)
$report=[Text.Encoding]::UTF8.GetString($reportBytes).TrimStart([char]0xfeff)|ConvertFrom-Json
. (Join-Path $bundle 'RecoveryGuard-Acceptance.ps1')
$mainHash=(Get-FileHash (Join-Path $main 'build-identity.json')).Hash
$guardHash=(Get-FileHash (Join-Path $guard 'MTTFTest.RecoveryGuard.exe')).Hash
$coreHash=(Get-FileHash (Join-Path $guard 'MTTFTest.RecoveryControl.dll')).Hash
$baseHash=(Get-FileHash (Join-Path $guard 'guard-identity.json')).Hash
[void](Assert-GuardAcceptanceReport $report $RecoveryMode $mainHash $guardHash $coreHash $baseHash)
$evidenceRoot=Split-Path $reportPath -Parent
Assert-GuardAcceptanceEvidenceFiles $report $evidenceRoot
if($report.machineName -ine $env:COMPUTERNAME){throw 'AcceptanceInstallTargetMismatch'}
$bench=$env:COMPUTERNAME
$control=Join-Path $env:ProgramData 'MTTFTest\RecoveryControl'
if(Test-Path $control){
    $status=@(& (Join-Path $guard 'MTTFTest.RecoveryGuard.exe') --status)
    if($LASTEXITCODE -ne 0){throw 'ExistingRecoveryStateUnreadable'}
    $bench=($status -join "`n"|ConvertFrom-Json).BenchId
}
if([string]::IsNullOrWhiteSpace($bench) -or $report.benchId -cne $bench){throw 'AcceptanceInstallTargetMismatch'}
if($Mode -eq 'Validate'){Write-Output 'Automatic package and field acceptance validated; no mutation';return}
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    $command="& '"+$PSCommandPath.Replace("'","''")+"' -Mode '$Mode' -RecoveryMode '$RecoveryMode' -AcceptanceReportPath '"+$reportPath.Replace("'","''")+"'"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child=Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded" -Wait -PassThru
    exit $child.ExitCode
}
# Materialize a schema-3 accepted Guard only after real report and target checks.
# No source checkout or Git installation is required on the field computer.
$stage=Join-Path $env:ProgramData ('MTTFTest\AcceptedGuardPackages\'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $stage -Force)
Get-ChildItem $guard -File|Copy-Item -Destination $stage
Copy-Item (Join-Path $guard 'guard-identity.json') (Join-Path $stage 'observe-base-identity.json')
Copy-Item (Join-Path $bundle 'RecoveryGuard-Acceptance.ps1') $stage
[IO.File]::WriteAllBytes((Join-Path $stage 'acceptance-report.json'),$reportBytes)
foreach($relative in @($report.checks.evidencePath|Sort-Object -Unique)){
    $target=Join-Path (Join-Path $stage 'Acceptance') $relative
    [void](New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force)
    Copy-Item -LiteralPath (Join-Path $evidenceRoot $relative) -Destination $target
}
Assert-GuardAcceptanceEvidenceFiles $report (Join-Path $stage 'Acceptance')
$identity.schemaVersion=3; $identity.deliveryStage='AutomaticRecovery'; $identity.automaticExecutionReady=$true
foreach($entry in @{mainIdentitySha256=$mainHash;approvedModes=@($report.approvedModes);acceptanceBenchId=$bench;
    acceptanceMachineName=$report.machineName;automaticPublisherSha256=(Get-FileHash $PSCommandPath).Hash}.GetEnumerator()){
    $identity|Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value -Force
}
$identity.files=@(Get-ChildItem $stage -File -Recurse|Where-Object FullName -ne (Join-Path $stage 'guard-identity.json')|ForEach-Object {
    @{name=$_.FullName.Substring($stage.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash}
})
$identity|ConvertTo-Json -Depth 12|Set-Content (Join-Path $stage 'guard-identity.json') -Encoding UTF8
& (Join-Path $stage 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Validate -SourceDirectory $stage -RecoveryMode $RecoveryMode|Out-Null
$installRoot=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'MTTFTest'
& (Join-Path $main 'Deployment\Install-MTTFTest-Unattended.ps1') -Mode $Mode -SourceDirectory $main -InstallRoot $installRoot
& (Join-Path $stage 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Install -SourceDirectory $stage -BenchId $bench -MainExecutable (Join-Path $installRoot 'Current\MTTFTest.exe') -RecoveryMode $RecoveryMode
if((Get-ScheduledTask MTTFTestRecoveryGuardExecution -ErrorAction Stop).State -eq 'Disabled'){throw 'AutomaticExecutionTaskStillDisabled'}
Write-Output "$RecoveryMode automatic recovery installed. A fresh operator-authorized trial is still required."
