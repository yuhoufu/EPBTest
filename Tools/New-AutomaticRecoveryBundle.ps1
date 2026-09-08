#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BaseBundleDirectory,
    [Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(@(git -C $repo status --porcelain).Count -ne 0 -or $LASTEXITCODE -ne 0){throw 'AutomaticBundleRequiresCleanSource'}
$commit=([string](git -C $repo rev-parse HEAD)).Trim()
$base=[IO.Path]::GetFullPath($BaseBundleDirectory)
& (Join-Path $base 'Verify-FieldPackage.ps1')|Out-Null
$id=Get-Content (Join-Path $base 'bundle-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
$output=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('V'+$id.productVersion+'_'+$commit.Substring(0,12)+'_AUTO_RECOVERY_PENDING_ACCEPTANCE')
if((Test-Path $output) -or (Test-Path ($output+'.7z'))){throw 'OutputAlreadyExists'}
[void](New-Item -ItemType Directory -Path $output -Force)
Copy-Item -LiteralPath $base -Destination (Join-Path $output 'Base') -Recurse
foreach($name in @('Install-AutomaticRecoveryBundle.ps1','RecoveryGuard-Acceptance.ps1')){
    [IO.File]::WriteAllText((Join-Path $output $name),[IO.File]::ReadAllText((Join-Path $PSScriptRoot $name)),[Text.UTF8Encoding]::new($true))
}
Copy-Item (Join-Path $repo 'docs\RecoveryGuard_自动恢复候选包使用说明.md') (Join-Path $output '自动恢复安装说明.md')
$commands=[ordered]@{'一键安装正式版'='Install';'一键修复'='Repair';'启动试验'='Launch';'检查运行状态'='Status';'恢复后台服务'='Restore';'一键故障采证'='Evidence';'一键停止全部相关进程'='Stop';'一键卸载'='Uninstall'}
foreach($name in $commands.Keys){
    $action=$commands[$name]
    $line=if($action -in @('Install','Repair')){
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-AutomaticRecoveryBundle.ps1" -Mode '+$action+' -AcceptanceReportPath "%~1"'
    }else{'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Base\FieldPackage-Launcher.ps1" -Action '+$action}
    $lines=@('@echo off','setlocal DisableDelayedExpansion',$line,'set "MTTFTEST_RESULT=%ERRORLEVEL%"','if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause','endlocal & exit /b %MTTFTEST_RESULT%')
    [IO.File]::WriteAllText((Join-Path $output ($name+'.cmd')),($lines -join "`r`n")+"`r`n",[Text.Encoding]::ASCII)
}
$files=@(Get-ChildItem $output -File -Recurse|ForEach-Object {@{name=$_.FullName.Substring($output.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash}})
@{schemaVersion=1;productVersion=$id.productVersion;componentCommit=$id.gitCommit;deploymentToolsCommit=$commit;
    recoveryMode='RecoverExited';supportedRecoveryModes=@('RecoverExited','RecoverStalled');fieldAcceptanceRequired=$true;files=$files}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $output 'automatic-bundle.json') -Encoding UTF8
& (Join-Path $output 'Install-AutomaticRecoveryBundle.ps1') -Mode ValidatePackage
$seven=Join-Path $env:ProgramFiles '7-Zip\7z.exe'
Push-Location (Split-Path $output -Parent)
try{
    & $seven a -t7z ($output+'.7z') ([IO.Path]::GetFileName($output)) -mx=9 -m0=LZMA2:d=128m:fb=273:mf=bt4 -ms=on -mqs=on -mmt=on -myx=9|Out-Host
    if($LASTEXITCODE -ne 0){throw 'CompressionFailed'}
    & $seven t ($output+'.7z')|Out-Host
    if($LASTEXITCODE -ne 0){throw 'ArchiveTestFailed'}
}finally{Pop-Location}
$hash=(Get-FileHash ($output+'.7z')).Hash
$hash|Set-Content ($output+'.7z.sha256') -Encoding ASCII
@{archive=($output+'.7z');sha256=$hash;recoveryMode='RecoverExited';supportedRecoveryModes=@('RecoverExited','RecoverStalled');fieldAcceptanceRequired=$true}|ConvertTo-Json
