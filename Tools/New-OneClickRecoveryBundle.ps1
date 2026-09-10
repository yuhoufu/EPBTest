#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SourceBundle,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [switch]$SourceIsBaseBundle)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(@(git -C $repo status --porcelain).Count -ne 0 -or $LASTEXITCODE -ne 0){throw 'CleanToolsSourceRequired'}
$commit=([string](git -C $repo rev-parse HEAD)).Trim()
$source=[IO.Path]::GetFullPath($SourceBundle)
if($SourceIsBaseBundle){
    # 新构建直接从已校验Base升级，避免经过已被一键安装策略替代的旧schema中间包。
    & (Join-Path $source 'Verify-FieldPackage.ps1') | Out-Null
    $sourceBase=$source
}else{
    & (Join-Path $source 'Install-AutomaticRecoveryBundle.ps1') -Mode ValidatePackage | Out-Null
    $sourceBase=Join-Path $source 'Base'
}
$mainId=Get-Content (Join-Path $sourceBase 'Package\build-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
$guardId=Get-Content (Join-Path $sourceBase 'Guard\guard-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($guardId.schemaVersion -ne 2 -or $guardId.gitCommit -cne $mainId.gitCommit -or $guardId.gitDirty -ne $false){throw 'VerifiedSourceComponentsRequired'}
$output=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('V'+$mainId.fileVersion+'_'+$commit.Substring(0,12)+'_AUTO_RECOVERY_ONECLICK')
if((Test-Path $output) -or (Test-Path ($output+'.7z'))){throw 'OutputAlreadyExists'}
if($SourceIsBaseBundle){
    [void](New-Item -ItemType Directory -Path $output)
    Copy-Item -LiteralPath $sourceBase -Destination (Join-Path $output 'Base') -Recurse
}else{
    Copy-Item -LiteralPath $source -Destination $output -Recurse
}
function Copy-Tool([string]$Name,[string]$Target) {
    [IO.File]::WriteAllText($Target,[IO.File]::ReadAllText((Join-Path $PSScriptRoot $Name)),[Text.UTF8Encoding]::new($true))
}
function Manifest-Files([string]$Root,[string]$Exclude) {
    @(Get-ChildItem $Root -File -Recurse|Where-Object FullName -ne (Join-Path $Root $Exclude)|ForEach-Object{
        [ordered]@{name=$_.FullName.Substring($Root.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash}
    })
}
$base=Join-Path $output 'Base';$guard=Join-Path $base 'Guard'
if($SourceIsBaseBundle){
    foreach($name in @('启动试验','检查运行状态','恢复后台服务','一键故障采证','一键停止全部相关进程','一键卸载')){
        $entry=[IO.File]::ReadAllText((Join-Path $base ($name+'.cmd')))
        $entry=$entry.Replace('%~dp0FieldPackage-Launcher.ps1','%~dp0Base\FieldPackage-Launcher.ps1')
        [IO.File]::WriteAllText((Join-Path $output ($name+'.cmd')),$entry,[Text.Encoding]::ASCII)
    }
}
Copy-Tool 'RecoveryGuard-Acceptance.ps1' (Join-Path $output 'RecoveryGuard-Acceptance.ps1')
Copy-Tool 'Install-AutomaticRecoveryBundle.ps1' (Join-Path $output 'Install-AutomaticRecoveryBundle.ps1')
Copy-Tool 'FieldPackage-Launcher.ps1' (Join-Path $base 'FieldPackage-Launcher.ps1')
Copy-Tool 'Install-MTTFTest-RecoveryGuard.ps1' (Join-Path $guard 'Install-MTTFTest-RecoveryGuard.ps1')
$guardId.schemaVersion=4;$guardId.deliveryStage='AutomaticRecovery';$guardId.automaticExecutionReady=$true
foreach($entry in @{
    installationPolicy='OperatorManaged';fieldAcceptanceRequired=$false;
    deploymentToolsCommit=$commit;mainIdentitySha256=(Get-FileHash (Join-Path $base 'Package\build-identity.json')).Hash;
    approvedModes=@('RecoverExited','RecoverStalled');
    componentSourceIdentitySha256=(Get-FileHash (Join-Path $sourceBase 'Guard\guard-identity.json')).Hash
}.GetEnumerator()){$guardId|Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value -Force}
Copy-Item (Join-Path $repo 'docs\一键自动恢复安装与修复.md') (Join-Path $guard 'README.md') -Force
$guardId.files=Manifest-Files $guard 'guard-identity.json'
$guardId|ConvertTo-Json -Depth 15|Set-Content (Join-Path $guard 'guard-identity.json') -Encoding UTF8
foreach($name in @('一键安装正式版','一键修复')){
    $action=if($name -eq '一键安装正式版'){'Install'}else{'Repair'}
    foreach($root in @($output,$base)){
        $relative=if($root -eq $base){'..\'}else{''}
        $lines=@('@echo off','setlocal DisableDelayedExpansion',
            ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0'+$relative+'Install-AutomaticRecoveryBundle.ps1" -Mode '+$action),
            'set "MTTFTEST_RESULT=%ERRORLEVEL%"','if not defined MTTFTEST_QUICKDEPLOY_NONINTERACTIVE pause','endlocal & exit /b %MTTFTEST_RESULT%')
        [IO.File]::WriteAllText((Join-Path $root ($name+'.cmd')),($lines -join "`r`n")+"`r`n",[Text.Encoding]::ASCII)
    }
}
foreach($path in @((Join-Path $output '自动恢复安装说明.md'),(Join-Path $base '快捷部署说明.md'))){Copy-Item (Join-Path $repo 'docs\一键自动恢复安装与修复.md') $path -Force}
$bid=Get-Content (Join-Path $base 'bundle-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
$bid.recoveryMode='RecoverExited';$bid.automaticRecoveryEnabled=$true;$bid.deploymentToolsCommit=$commit
$bid.files=Manifest-Files $base 'bundle-identity.json'
$bid|ConvertTo-Json -Depth 8|Set-Content (Join-Path $base 'bundle-identity.json') -Encoding UTF8
$manifest=[ordered]@{schemaVersion=2;productVersion=$mainId.fileVersion;componentCommit=$mainId.gitCommit;
    deploymentToolsCommit=$commit;installationPolicy='OperatorManaged';recoveryMode='RecoverExited';
    supportedRecoveryModes=@('RecoverExited','RecoverStalled');fieldAcceptanceRequired=$false;
    hardwareValidationPerformed=$false;files=(Manifest-Files $output 'automatic-bundle.json')}
$manifest|ConvertTo-Json -Depth 8|Set-Content (Join-Path $output 'automatic-bundle.json') -Encoding UTF8
& (Join-Path $output 'Install-AutomaticRecoveryBundle.ps1') -Mode ValidatePackage
if(@(git -C $repo status --porcelain).Count -ne 0 -or ([string](git -C $repo rev-parse HEAD)).Trim() -cne $commit){throw 'ToolsSourceChanged'}
$seven=Join-Path $env:ProgramFiles '7-Zip\7z.exe'
Push-Location (Split-Path $output -Parent)
try{
    & $seven a -t7z ($output+'.7z') ([IO.Path]::GetFileName($output)) -mx=7 -ms=on | Out-Host
    if($LASTEXITCODE -ne 0){throw 'ArchiveFailed'}
    & $seven t ($output+'.7z') | Out-Host
    if($LASTEXITCODE -ne 0){throw 'ArchiveTestFailed'}
}finally{Pop-Location}
$hash=(Get-FileHash ($output+'.7z')).Hash
$hash|Set-Content ($output+'.7z.sha256') -Encoding ASCII
[pscustomobject]@{directory=$output;archive=($output+'.7z');sha256=$hash;componentCommit=$mainId.gitCommit;deploymentToolsCommit=$commit}|ConvertTo-Json
