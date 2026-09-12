#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$MainDirectory,
    [Parameter(Mandatory=$true)][string]$GuardDirectory,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [ValidateSet('ObserveOnly','RecoverExited')][string]$RecoveryMode='ObserveOnly')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(@(git -C $repo status --porcelain).Count -ne 0 -or $LASTEXITCODE -ne 0){throw '发布工具源码必须干净。'}
$commit=([string](git -C $repo rev-parse HEAD)).Trim()
$main=[IO.Path]::GetFullPath($MainDirectory)
$guard=[IO.Path]::GetFullPath($GuardDirectory)
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -ReleaseDirectory $main | Out-Null
$id=Get-Content (Join-Path $main 'build-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$gid=Get-Content (Join-Path $guard 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($id.gitCommit -ne $gid.gitCommit -or $gid.gitDirty -ne $false){throw '必须使用同一干净提交的正式Main与Guard。'}
& (Join-Path $guard 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Validate -SourceDirectory $guard -RecoveryMode $RecoveryMode | Out-Null
$automatic=$RecoveryMode -eq 'RecoverExited'
if($automatic -and ((Get-FileHash (Join-Path $main 'build-identity.json')).Hash -ine $gid.mainIdentitySha256)){throw '自动恢复验收不匹配主程序包。'}
if(-not $automatic -and ($gid.automaticExecutionReady -ne $false -or $gid.deliveryStage -ne 'ObserveOnlyCommissioning')){throw '观察合包需要观察Guard。'}
[void](New-Item -ItemType Directory $OutputRoot -Force)
$suffix=if($automatic){'_QUICKDEPLOY_GUARD_AUTO_RECOVER_EXITED'}else{'_QUICKDEPLOY_GUARD'}
$output=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('V'+$id.fileVersion+'_正式版_'+$commit.Substring(0,12)+$suffix)
if((Test-Path $output) -or (Test-Path ($output+'.7z'))){throw '输出已存在，不覆盖历史包。'}
[void](New-Item -ItemType Directory $output)
Copy-Item $main (Join-Path $output 'Package') -Recurse
Copy-Item $guard (Join-Path $output 'Guard') -Recurse
foreach($name in @('FieldPackage-Launcher.ps1','Verify-FieldPackage.ps1')){Copy-Item (Join-Path $PSScriptRoot $name) $output}
Copy-Item (Join-Path $repo 'Tools\Stop-RelatedProcesses.ps1') (Join-Path $output 'Stop-RelatedProcesses.ps1')
Copy-Item (Join-Path $repo 'docs\RecoveryGuard_一键部署包使用说明.md') (Join-Path $output '快捷部署说明.md')
$commands=[ordered]@{'一键安装正式版'='Install';'启动试验'='Launch';'检查运行状态'='Status';'一键修复'='Repair';'恢复后台服务'='Restore';'一键故障采证'='Evidence';'一键停止全部相关进程'='Stop';'一键卸载'='Uninstall'}
foreach($name in $commands.Keys){
    $lines=@('@echo off','setlocal DisableDelayedExpansion',
        ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FieldPackage-Launcher.ps1" -Action '+$commands[$name]),
        'set "MTTFTEST_RESULT=%ERRORLEVEL%"',
        'if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause',
        'endlocal & exit /b %MTTFTEST_RESULT%')
    [IO.File]::WriteAllText((Join-Path $output ($name+'.cmd')),($lines -join "`r`n")+"`r`n",[Text.Encoding]::ASCII)
}
$files=@(Get-ChildItem $output -Recurse -File | ForEach-Object {@{name=$_.FullName.Substring($output.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash}})
@{schemaVersion=2;productVersion=$id.fileVersion;gitCommit=$id.gitCommit;deploymentToolsCommit=$commit;recoveryMode=$RecoveryMode;automaticRecoveryEnabled=$automatic;files=$files} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'bundle-identity.json') -Encoding UTF8
& (Join-Path $output 'Verify-FieldPackage.ps1')
$sevenZip=Join-Path $env:ProgramFiles '7-Zip\7z.exe'
if(-not(Test-Path $sevenZip)){$sevenZip=(Get-Command 7z.exe -ErrorAction Stop).Source}
Push-Location (Split-Path $output -Parent)
try {
    & $sevenZip a -t7z ($output+'.7z') ([IO.Path]::GetFileName($output)) -mx=9 -m0=LZMA2:d=128m:fb=273:mf=bt4 -ms=on -mqs=on -mmt=on -myx=9 -sccUTF-8 | Out-Host
    if($LASTEXITCODE -ne 0){throw '7-Zip压缩失败。'}
    & $sevenZip t ($output+'.7z') | Out-Host
    if($LASTEXITCODE -ne 0){throw '7-Zip完整性测试失败。'}
} finally {Pop-Location}
$hash=(Get-FileHash ($output+'.7z')).Hash
$hash | Set-Content ($output+'.7z.sha256') -Encoding ASCII
@{directory=$output;archive=($output+'.7z');sha256=$hash;gitCommit=$commit;validation='Package checks only; JXCQ final-package acceptance pending'} | ConvertTo-Json
