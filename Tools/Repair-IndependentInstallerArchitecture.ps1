#requires -Version 5.1
[CmdletBinding()]
param([string]$InstallRoot='', [switch]$Elevated)
# Public entry points accept PowerShell 7; .NET Framework deployment work is
# executed by the Windows PowerShell host with typed, data-only arguments.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $epbBridgeParameters = @{}
    foreach ($epbBridgeKey in $PSBoundParameters.Keys) {
        $epbBridgeValue = $PSBoundParameters[$epbBridgeKey]
        if ($epbBridgeValue -is [Management.Automation.SwitchParameter]) { $epbBridgeValue = [bool]$epbBridgeValue }
        $epbBridgeParameters[$epbBridgeKey] = $epbBridgeValue
    }
    $epbBridgeData = @{ Script = $PSCommandPath; Parameters = $epbBridgeParameters } | ConvertTo-Json -Depth 5 -Compress
    $epbBridgePayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($epbBridgeData))
    $epbBridgeCode = '$ErrorActionPreference="Stop";$ProgressPreference="SilentlyContinue";$d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("' + $epbBridgePayload + '"))|ConvertFrom-Json;$p=@{};foreach($v in $d.Parameters.PSObject.Properties){$p[$v.Name]=$v.Value};$global:LASTEXITCODE=0;& ([string]$d.Script) @p;exit $LASTEXITCODE'
    $epbBridgeEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($epbBridgeCode))
    $epbBridgeModulePath = $env:PSModulePath
    try {
        $env:PSModulePath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules"
        & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand $epbBridgeEncoded
        $epbBridgeExitCode = $LASTEXITCODE
    } finally { $env:PSModulePath = $epbBridgeModulePath }
    exit $epbBridgeExitCode
}
$ErrorActionPreference='Stop'
function Read-ArchitectureRepairJson([string]$Path) {
    $file=Get-Item -LiteralPath $Path
    if($file.Length -gt 1MB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '安装记录大小或路径无效。'}
    return ([IO.File]::ReadAllText($Path)|ConvertFrom-Json)
}
function Repair-IndependentArchitectureFiles([string]$Root) {
    $oldHash='4A89FAFF0EC2940F49161676E9A0C307F59AA61251CB8F8F35E5C2493404B0AA'
    $mainHash='5D2409E723A0D2C93173D639E0EAEAB393757FDF7F829B1722AA8D68BD7732A6'
    $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    $state=Read-ArchitectureRepairJson (Join-Path $rootPath 'install-setup.json')
    $receiptPath=Join-Path $rootPath 'installed-files.json'
    $receipt=Read-ArchitectureRepairJson $receiptPath
    if($state.version -ne '4.1.0.3' -or $state.stage -notin @('Binding','Ready') -or
        $receipt.schemaVersion -ne 1 -or $receipt.version -ne '4.1.0.3' -or
        -not [string]::Equals([string]$receipt.installRoot,$rootPath,[StringComparison]::OrdinalIgnoreCase)){
        throw '此修复仅适用于本次4.1.0.3安装器位数错误，不适用于其他安装或版本。'
    }
    if((Get-FileHash -LiteralPath (Join-Path $rootPath 'Current\MTTFTest.exe')).Hash -ne $mainHash){throw '主程序不是已确认失败的4.1.0.3构建，未修改。'}
    $manager=Join-Path $rootPath 'Tools\Manage-IndependentRecovery.ps1'
    $repairRoot=Join-Path $rootPath 'InstallerArchitectureRepair'
    $originalManager=Join-Path $repairRoot 'Manage-IndependentRecovery.before.ps1'
    $originalReceipt=Join-Path $repairRoot 'installed-files.before.json'
    $currentHash=(Get-FileHash -LiteralPath $manager).Hash
    $source=if($currentHash -eq $oldHash){$manager}else{$originalManager}
    if(-not [IO.File]::Exists($source) -or (Get-FileHash -LiteralPath $source).Hash -ne $oldHash){throw '安装脚本不是已确认的原版，未执行替换。'}
    $text=[IO.File]::ReadAllText($source)
    $old='[Reflection.Assembly]::LoadFrom($MainExecutablePath)'
    if(([regex]::Matches($text,[regex]::Escape($old))).Count -ne 1){throw '能力探测语句不唯一，未修改。'}
    $fixed=$text.Replace($old,'[Reflection.Assembly]::ReflectionOnlyLoadFrom($MainExecutablePath)')
    $encoding=[Text.UTF8Encoding]::new($true)
    $bytes=$encoding.GetPreamble()+$encoding.GetBytes($fixed)
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$newHash=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')}finally{$sha.Dispose()}
    if($currentHash -notin @($oldHash,$newHash)){throw '安装脚本已被其他修改替换，未继续。'}
    $entries=@($receipt.files);$seen=@{};$managerEntry=$null
    if($entries.Count -eq 0 -or $entries.Count -gt 10000){throw '安装文件记录数量无效。'}
    foreach($entry in $entries){
        $relative=[string]$entry.path
        if([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)|:' -or $seen.ContainsKey($relative)){throw '安装文件记录路径无效。'}
        $seen[$relative]=$true;$path=[IO.Path]::GetFullPath((Join-Path $rootPath $relative))
        if(-not $path.StartsWith($rootPath+'\',[StringComparison]::OrdinalIgnoreCase)){throw '安装文件记录越界。'}
        $actual=(Get-FileHash -LiteralPath $path).Hash
        if($relative -eq 'Tools/Manage-IndependentRecovery.ps1'){
            if($entry.sha256 -notin @($oldHash,$newHash) -or $actual -notin @($oldHash,$newHash)){throw '安装脚本记录不一致。'}
            $managerEntry=$entry
        }elseif($actual -ne $entry.sha256){throw ('已安装文件变化，停止修复：'+$relative)}
    }
    if(-not $managerEntry -or -not $seen.ContainsKey('Current/MTTFTest.exe')){throw '原安装缺少必要文件记录。'}
    if($currentHash -eq $newHash -and $managerEntry.sha256 -eq $newHash){return 'AlreadyPatched'}
    if($state.stage -ne 'Binding' -or [IO.Directory]::Exists((Join-Path $rootPath 'IndependentState')) -or
        [IO.File]::Exists((Join-Path $rootPath 'Current\MTTFTest.exe.independent.json'))){throw '恢复注册已开始或完成，不能按本次未注册失败修复。'}
    [IO.Directory]::CreateDirectory($repairRoot)|Out-Null
    if(-not [IO.File]::Exists($originalManager)){[IO.File]::Copy($manager,$originalManager,$false)}
    if(-not [IO.File]::Exists($originalReceipt)){[IO.File]::Copy($receiptPath,$originalReceipt,$false)}
    # Backups precede both replacements. A retry recognizes exactly the old/new
    # script digest and can finish a crash between script and receipt publication.
    $managerEntry.sha256=$newHash.ToLowerInvariant()
    $managerTemp=Join-Path $repairRoot 'manager.new'
    $receiptTemp=Join-Path $repairRoot 'receipt.new'
    [IO.File]::WriteAllBytes($managerTemp,$bytes)
    [IO.File]::WriteAllText($receiptTemp,($receipt|ConvertTo-Json -Depth 4),$encoding)
    if($currentHash -ne $newHash){[IO.File]::Replace($managerTemp,$manager,(Join-Path $repairRoot 'manager.replaced'))}
    [IO.File]::Replace($receiptTemp,$receiptPath,(Join-Path $repairRoot 'receipt.replaced'))
    [IO.File]::WriteAllText((Join-Path $repairRoot 'result.json'),([ordered]@{
        stage='Patched';version='4.1.0.3';oldSha256=$oldHash;newSha256=$newHash;
        change='Metadata-only capability probe';programFilesChanged=$false;projectDataChanged=$false;
        utc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 3),$encoding)
    return 'Patched'
}
if(-not $InstallRoot){$InstallRoot=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'MTTFTest'}
$InstallRoot=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if($InstallRoot.Contains('"') -or $InstallRoot.StartsWith('\\')){throw '安装目录无效。'}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    if($Elevated){throw '提权后仍无管理员权限。'}
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -InstallRoot "'+$InstallRoot+'" -Elevated'
    $child=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $child.ExitCode
}
$protocol=Join-Path $InstallRoot 'Current\MTTFTest.Watchdog.Protocol.dll'
if((Get-FileHash -LiteralPath $protocol).Hash -ne 'C6C557192FBBE4BCE52B7B3E6991D8AB93AB9C6A0801C34A77C374CAF878B38E'){throw '安装协议组件不是已确认的原构建，未修改。'}
[Reflection.Assembly]::LoadFrom($protocol)|Out-Null
[MTTFTest.Watchdog.Protocol.IndependentProtectedFiles]::RequireTrustedDirectory($InstallRoot)
foreach($name in @('installed-files.json','install-setup.json','Tools\Manage-IndependentRecovery.ps1')){
    [MTTFTest.Watchdog.Protocol.IndependentProtectedFiles]::RequireTrustedFile((Join-Path $InstallRoot $name))
}
$lease=[IO.File]::Open((Join-Path $InstallRoot 'install-setup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $state=Read-ArchitectureRepairJson (Join-Path $InstallRoot 'install-setup.json')
    if($state.stage -ne 'Ready'){
        [MTTFTest.Watchdog.Protocol.IndependentInstallationBinding]::RequireControllerAbsent((Join-Path $InstallRoot 'Current\MTTFTest.exe'))
    }
    $result=Repair-IndependentArchitectureFiles $InstallRoot
    Write-Output ('安装器位数修复：'+$result+'；保留原主程序和项目数据。')
}finally{$lease.Dispose()}
if($state.stage -ne 'Ready'){
    $complete=Join-Path $InstallRoot 'Tools\Complete-IndependentSetup.ps1'
    $helper=Join-Path $InstallRoot 'Tools\Independent-InstallSetup.ps1'
    if((Get-FileHash $complete).Hash -ne '5876B85FAA536750B8B25A4299FE1ADA7742B48797C48F860F99F8255C89CDD2' -or
        (Get-FileHash $helper).Hash -ne 'EC12B26B21C0C66F8338609C8CF7A64CC4329A669BF584626E2D69920DF02729'){throw '安装收尾脚本与原包不一致，已保留修复结果但未执行收尾。'}
    & $complete -Mode Complete -InstallRoot $InstallRoot
    Write-Output '已完成原4.1.0.3安装收尾；没有启动试验。'
}else{Write-Output '本安装已完成，未重复注册或启停服务。'}
$installationResult=Join-Path $InstallRoot 'installation-result.json'
if([IO.File]::Exists($installationResult)){
    $previousResult=Read-ArchitectureRepairJson $installationResult
    if($previousResult.version -eq '4.1.0.3' -and $previousResult.stage -eq 'Failed'){
        $temporary=$installationResult+'.architecture-repair.tmp'
        [IO.File]::WriteAllText($temporary,([ordered]@{version='4.1.0.3';stage='Ready';trialStarted=$false;repair='InstallerArchitecture';utc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 3),[Text.UTF8Encoding]::new($true))
        [IO.File]::Replace($temporary,$installationResult,(Join-Path $InstallRoot 'InstallerArchitectureRepair\installation-result.before.json'))
    }
}
