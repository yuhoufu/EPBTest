param([Parameter(Mandatory=$true)][string]$BundleDirectory)
$ErrorActionPreference='Stop'
$bundle=[IO.Path]::GetFullPath($BundleDirectory)
$entry=Join-Path $bundle 'Install-AutomaticRecoveryBundle.ps1'
foreach($mode in @('ValidatePackage','Validate')){
    $result=& $entry -Mode $mode | ConvertFrom-Json
    if(-not $result.PackageValidated -or $result.FieldAcceptanceRequired -or $result.TasksChanged){throw 'ReportFreeValidationFailed'}
}
Write-Output 'PASS validation needs no report and makes no installation changes'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($entry,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'ParseFailed'}
$definition=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Resolve-AutomaticRepairMode'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$temp=Join-Path ([IO.Path]::GetTempPath()) ('epb-oneclick-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$settings=Join-Path $temp 'settings.json'
foreach($old in @('RecoverExited','RecoverStalled')){
    [IO.File]::WriteAllText($settings,('{"Mode":"'+$old+'"}'))
    if((Resolve-AutomaticRepairMode Repair RecoverExited $false $settings) -cne $old){throw 'RepairChangedMode'}
}
if((Resolve-AutomaticRepairMode Repair RecoverExited $true $settings) -cne 'RecoverExited'){throw 'OverrideFailed'}
[IO.File]::WriteAllText($settings,'{"Mode":"unknown"}')
try{Resolve-AutomaticRepairMode Repair RecoverExited $false $settings;throw 'ExpectedRejection'}catch{if($_.Exception.Message -notlike 'InstalledRecoveryModeInvalid*'){throw}}
Write-Output 'PASS repair preserves both modes and rejects unknown settings'
$guardEntry=Join-Path $bundle 'Base\Guard\Install-MTTFTest-RecoveryGuard.ps1'
$gAst=[Management.Automation.Language.Parser]::ParseFile($guardEntry,[ref]$tokens,[ref]$errors)
foreach($name in @('Read-VerifiedPackage','Resolve-GuardRecoveryMode','Assert-GuardAcceptanceTarget')){
    $def=$gAst.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    . ([scriptblock]::Create($def.Extent.Text))
}
$guard=Join-Path $bundle 'Base\Guard'
$id=Read-VerifiedPackage $guard
$config=Get-Content (Join-Path $guard 'guard-settings.json') -Raw|ConvertFrom-Json
foreach($mode in @('RecoverExited','RecoverStalled')){if((Resolve-GuardRecoveryMode $id $config $mode) -eq 0){throw 'ModeDowngraded'}}
Assert-GuardAcceptanceTarget $id (Join-Path $bundle 'Base\Package\MTTFTest.exe') 'TestBench' $env:COMPUTERNAME
$wrong=Join-Path $temp 'build-identity.json';[IO.File]::WriteAllText($wrong,'{}')
try{Assert-GuardAcceptanceTarget $id (Join-Path $temp 'MTTFTest.exe') 'TestBench' $env:COMPUTERNAME;throw 'ExpectedMismatch'}catch{if($_.Exception.Message -ne 'DirectInstallTargetMismatch'){throw}}
Write-Output 'PASS automatic modes work and wrong Main identity remains rejected'
$copy=Join-Path $temp 'Guard';Copy-Item -LiteralPath $guard -Destination $copy -Recurse
[IO.File]::AppendAllText((Join-Path $copy 'guard-settings.json'),' ')
try{Read-VerifiedPackage $copy;throw 'ExpectedTamperRejection'}catch{if($_.Exception.Message -notlike '独立包文件损坏*'){throw}}
Write-Output 'PASS modified Guard content remains rejected'
foreach($name in @('一键安装正式版.cmd','一键修复.cmd')){
    foreach($root in @($bundle,(Join-Path $bundle 'Base'))){
        $text=[IO.File]::ReadAllText((Join-Path $root $name))
        if($text -notmatch 'Install-AutomaticRecoveryBundle.ps1' -or $text -match 'AcceptanceReportPath|%~1'){throw 'EntryStillRequiresArguments'}
    }
}
Write-Output 'PASS inner and outer entries share report-free installation'
# Run the actual install tail against filesystem-only installer doubles.
$text=[IO.File]::ReadAllText($entry)
$tail=$text.Substring($text.IndexOf('$stage=$guard'))
$main=Join-Path $temp 'Main';$guard=Join-Path $temp 'FakeGuard'
[void][IO.Directory]::CreateDirectory((Join-Path $main 'Deployment'))
[void][IO.Directory]::CreateDirectory($guard)
$global:epbOneClickFixture=@{seenMain='';seenGuard='';mainFailure=0}
[IO.File]::WriteAllText((Join-Path $main 'Deployment\Install-MTTFTest-Unattended.ps1'), 'param($Mode,$SourceDirectory,$InstallRoot) $global:epbOneClickFixture.seenMain=$Mode; $global:LASTEXITCODE=$global:epbOneClickFixture.mainFailure')
[IO.File]::WriteAllText((Join-Path $guard 'Install-MTTFTest-RecoveryGuard.ps1'), 'param($Mode,$SourceDirectory,$BenchId,$MainExecutable,$RecoveryMode) $global:epbOneClickFixture.seenGuard=$RecoveryMode; $global:LASTEXITCODE=0')
function Get-ScheduledTask { [pscustomobject]@{State='Ready'} }
$bench='Fixture';$RecoveryMode='RecoverStalled'
foreach($Mode in @('Install','Repair')){
    $global:epbOneClickFixture.mainFailure=0;$global:epbOneClickFixture.seenMain='';$global:epbOneClickFixture.seenGuard=''
    . ([scriptblock]::Create($tail)) | Out-Null
    if($global:epbOneClickFixture.seenMain -cne $Mode -or $global:epbOneClickFixture.seenGuard -cne 'RecoverStalled'){throw 'InstallDispatchMismatch'}
}
$global:epbOneClickFixture.mainFailure=1;$global:epbOneClickFixture.seenGuard=''
try{. ([scriptblock]::Create($tail));throw 'ExpectedMainFailure'}catch{if($_.Exception.Message -ne 'MainInstallationFailed'){throw}}
if($global:epbOneClickFixture.seenGuard -ne ''){throw 'GuardRanAfterMainFailure'}
Remove-Variable epbOneClickFixture -Scope Global
$global:LASTEXITCODE=0
Write-Output 'PASS install and repair dispatch correctly; Main failure stops Guard installation'
Write-Output 'PASS one-click package 6/6; no services, tasks or hardware modified'
