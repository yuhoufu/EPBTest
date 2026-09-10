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
if($manifest.schemaVersion -ne 2 -or $manifest.installationPolicy -cne 'OperatorManaged' -or
    $manifest.fieldAcceptanceRequired -isnot [bool] -or $manifest.fieldAcceptanceRequired){throw 'AutomaticBundleIdentityInvalid'}
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
    $identity.deliveryStage -cne 'AutomaticRecovery' -or $identity.schemaVersion -ne 4){throw 'AutomaticBaseIdentityInvalid'}
& (Join-Path $guard 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Validate -SourceDirectory $guard -RecoveryMode $RecoveryMode | Out-Null
if($Mode -in @('ValidatePackage','Validate')){
    [pscustomobject]@{PackageValidated=$true;RecoveryMode=$RecoveryMode;FieldAcceptanceRequired=$false;TasksChanged=$false}|ConvertTo-Json
    return
}
$bench=$env:COMPUTERNAME
$control=Join-Path $env:ProgramData 'MTTFTest\RecoveryControl'
if(Test-Path $control){
    $status=@(& (Join-Path $guard 'MTTFTest.RecoveryGuard.exe') --status)
    if($LASTEXITCODE -ne 0){throw 'ExistingRecoveryStateUnreadable'}
    $bench=($status -join "`n"|ConvertFrom-Json).BenchId
}
if([string]::IsNullOrWhiteSpace($bench)){throw 'InstallationBenchMissing'}
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    $command="& '"+$PSCommandPath.Replace("'","''")+"' -Mode '$Mode'"
    if($PSBoundParameters.ContainsKey('RecoveryMode')){$command+=" -RecoveryMode '$RecoveryMode'"}
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child=Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded" -Wait -PassThru
    exit $child.ExitCode
}
$stage=$guard
$installRoot=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'MTTFTest'
$global:LASTEXITCODE=0
& (Join-Path $main 'Deployment\Install-MTTFTest-Unattended.ps1') -Mode $Mode -SourceDirectory $main -InstallRoot $installRoot
if($LASTEXITCODE -ne 0){throw 'MainInstallationFailed'}
$global:LASTEXITCODE=0
& (Join-Path $stage 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Install -SourceDirectory $stage -BenchId $bench -MainExecutable (Join-Path $installRoot 'Current\MTTFTest.exe') -RecoveryMode $RecoveryMode
if($LASTEXITCODE -ne 0){throw 'GuardInstallationFailed'}
if((Get-ScheduledTask MTTFTestRecoveryGuardExecution -ErrorAction Stop).State -eq 'Disabled'){throw 'AutomaticExecutionTaskStillDisabled'}
Write-Output "$RecoveryMode automatic recovery installed. A fresh operator-authorized trial is still required."
