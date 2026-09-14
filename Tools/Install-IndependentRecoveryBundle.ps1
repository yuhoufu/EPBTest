#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Validate','ValidateRepair','Repair','Install')][string]$Mode='Validate',
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [string]$ProjectDirectory,
    [string]$InteractiveUserSid
)
$ErrorActionPreference='Stop'
function Get-IndependentBundlePlan([string]$Bundle,[string]$Destination) {
    $bundlePath=[IO.Path]::GetFullPath($Bundle).TrimEnd('\')
    $destinationPath=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if($destinationPath.StartsWith('\\') -or $destinationPath -eq [IO.Path]::GetPathRoot($destinationPath).TrimEnd('\')){throw '安装目录必须为本机非根目录。'}
    $manifestPath=Join-Path $bundlePath 'automatic-bundle.json'
    if((Get-Item -LiteralPath $manifestPath).Length -gt 4MB){throw '安装清单过大。'}
    $manifest=[IO.File]::ReadAllText($manifestPath)|ConvertFrom-Json
    if($manifest.schemaVersion -ne 2 -or $manifest.recoveryArchitecture -ne 'V4-Independent-SystemExecutor' -or
       [string]$manifest.version -notmatch '^\d+\.\d+\.\d+\.\d+$'){throw '独立执行器包架构或版本无效。'}
    $entries=@($manifest.files)
    if($entries.Count -eq 0 -or $entries.Count -gt 10000){throw '文件清单数量无效。'}
    $seen=@{};$plan=@();[long]$total=0
    foreach($entry in $entries){
        $relative=[string]$entry.path
        if([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or
           $relative -match '[:"<>|?*]' -or $relative.StartsWith('/') -or
           @($relative.Split('/')|Where-Object{$_ -in @('','.','..') -or $_.EndsWith(' ') -or $_.EndsWith('.')}).Count){throw '清单路径无效。'}
        if($seen.ContainsKey($relative)){throw '清单路径重复。'};$seen[$relative]=$true
        $source=[IO.Path]::GetFullPath((Join-Path $bundlePath $relative))
        if(-not $source.StartsWith($bundlePath+'\',[StringComparison]::OrdinalIgnoreCase)){throw '清单路径越界。'}
        $file=Get-Item -LiteralPath $source
        if($file -isnot [IO.FileInfo] -or $file.Length -gt 256MB){throw '包文件类型或大小无效。'}
        $cursor=$file
        while($cursor){
            if(($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '包路径包含重解析点。'}
            $cursor=if($cursor -is [IO.FileInfo]){$cursor.Directory}else{$cursor.Parent}
        }
        $total+=$file.Length;if($total -gt 2GB){throw '包大小超过安装预算。'}
        $digest=[string]$entry.sha256
        if($digest -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $digest){throw ('包摘要不符：'+$relative)}
        $target=if($relative.StartsWith('Base/')){'Current/'+$relative.Substring(5)}
            elseif($relative.StartsWith('FallbackGuard/')){$relative}
            elseif($relative -eq 'Tools/Manage-IndependentRecovery.ps1'){$relative}else{$null}
        if($target){$plan+=,[pscustomobject]@{Source=$source;Relative=$target;Sha256=$digest}}
    }
    foreach($required in @('Base/MTTFTest.exe','Base/MTTFTest.SafetyAgent.exe','Base/MTTFTest.Watchdog.Protocol.dll',
        'FallbackGuard/MTTFTest.FallbackGuard.exe','FallbackGuard/MTTFTest.Watchdog.Protocol.dll',
        'FallbackGuard/System.Data.SQLite.dll','FallbackGuard/x86/SQLite.Interop.dll','Tools/Manage-IndependentRecovery.ps1')){
        if(-not $seen.ContainsKey($required)){throw ('缺少必要文件：'+$required)}
    }
    [pscustomobject]@{Version=[string]$manifest.version;Destination=$destinationPath;Files=$plan}
}
function Assert-IndependentInstallParent([string]$Path) {
    $cursor=Get-Item -LiteralPath $Path
    if($cursor -isnot [IO.DirectoryInfo]){throw '安装父目录不存在。'}
    $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    while($cursor){
        if(($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '安装父路径包含重解析点。'}
        $acl=Get-Acl -LiteralPath $cursor.FullName
        if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw '安装父路径所有者不可信。'}
        $danger=[Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership -bor
            [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::Delete
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
            if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $trusted -and
                ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 -and
                ([long]$rule.FileSystemRights -band [long]$danger) -ne 0){throw '安装父路径可被非受信主体替换。'}
        }
        $cursor=$cursor.Parent
    }
}
function Assert-IndependentComponentVersions($Plan) {
    foreach($relative in @('Current/MTTFTest.exe','Current/MTTFTest.SafetyAgent.exe','FallbackGuard/MTTFTest.FallbackGuard.exe')){
        $files=@($Plan.Files|Where-Object Relative -eq $relative)
        if($files.Count -ne 1 -or [Diagnostics.FileVersionInfo]::GetVersionInfo($files[0].Source).FileVersion -cne $Plan.Version){
            throw ('包内组件实际版本与清单不一致：'+$relative)
        }
    }
}
function Get-IndependentRepairFiles($Plan,$Receipt) {
    if($Receipt.schemaVersion -ne 1 -or $Receipt.version -cne $Plan.Version -or
       -not [string]::Equals([string]$Receipt.installRoot,$Plan.Destination,[StringComparison]::OrdinalIgnoreCase)){
        throw '修复包与原安装版本或目录不一致。'
    }
    $owned=@($Receipt.files)
    if($owned.Count -ne $Plan.Files.Count){throw '原安装文件数量与修复包不一致。'}
    $index=@{}
    foreach($file in $owned){
        $relative=[string]$file.path
        if($index.ContainsKey($relative)){throw '原安装文件记录重复。'}
        $index[$relative]=[string]$file.sha256
    }
    foreach($file in $Plan.Files){
        if(-not $index.ContainsKey($file.Relative) -or $index[$file.Relative] -ne $file.Sha256){throw '修复包不是原安装的同一构建。'}
    }
    # Site configuration, state, databases and unknown file types are never
    # reset from package defaults by binary repair.
    foreach($file in $Plan.Files){
        if($file.Relative -notlike 'Current/Config/*' -and
           $file.Relative -match '(?i)\.(exe|dll|pdb|ps1|exe\.config)$'){$file}
    }
}
function Invoke-IndependentFileRepair([object[]]$Files,[string]$InstallDirectory) {
    # Caller must own installation maintenance and the executor lease. This
    # primitive is deliberately not exposed as an ungated installer mode.
    $root=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    if(-not $Files -or $Files.Count -gt 10000){throw '修复候选数量无效。'}
    $transaction=Join-Path $root ('Repair\'+[Guid]::NewGuid().ToString('N'))
    $entries=@();$touched=@();$journal=$null
    function Assert-RepairPath([string]$Path){
        $cursor=$Path
        while($cursor){
            if(Test-Path -LiteralPath $cursor){
                if(((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '修复路径含重解析点。'}
            }
            $cursor=[IO.Path]::GetDirectoryName($cursor)
        }
    }
    Assert-RepairPath $transaction
    $lockPath=Join-Path $root 'file-repair.lock'
    Assert-RepairPath $lockPath
    $repairLease=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
    $history=Join-Path $root 'Repair'
    if([IO.Directory]::Exists($history)){
        $count=0
        foreach($directory in [IO.Directory]::EnumerateDirectories($history)){
            if(++$count -gt 256){throw '修复历史超过检查上限，未开始新的替换。'}
            Assert-RepairPath $directory
            $record=Join-Path $directory 'transaction.json'
            if(-not [IO.File]::Exists($record)){throw ('修复事务记录缺失，禁止覆盖未确认状态：'+$directory)}
            Assert-RepairPath $record
            if((Get-Item -LiteralPath $record).Length -gt 4MB){throw '修复历史记录超限。'}
            $previous=[IO.File]::ReadAllText($record)|ConvertFrom-Json
            if($previous.schemaVersion -ne 1 -or $previous.phase -notin @('Replaced','RolledBack')){
                throw ('存在未收尾修复事务，必须先恢复该事务：'+$directory)
            }
        }
    }
    [IO.Directory]::CreateDirectory($transaction)|Out-Null
    $journal=Join-Path $transaction 'transaction.json'
    function Save-RepairJournal([string]$Phase,[string]$Failure){
        $text=[ordered]@{schemaVersion=1;phase=$Phase;failure=$Failure;entries=$entries;utc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 4
        $temp=$journal+'.tmp';[IO.File]::WriteAllText($temp,$text)
        if([IO.File]::Exists($journal)){[IO.File]::Replace($temp,$journal,$journal+'.previous')}else{[IO.File]::Move($temp,$journal)}
    }
    try{
        $seen=@{};[long]$bytes=0
        foreach($file in $Files){
            $target=[IO.Path]::GetFullPath((Join-Path $root $file.Relative))
            if(-not $target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($target) -or
               $file.Relative -like 'Current/Config/*' -or $file.Relative -notmatch '(?i)\.(exe|dll|pdb|ps1|exe\.config)$'){
                throw '修复目标不在程序组件范围内。'
            }
            $seen[$target]=$true;Assert-RepairPath $target
            $source=Get-Item -LiteralPath $file.Source
            $bytes+=$source.Length
            if($source.Length -gt 256MB -or $bytes -gt 2GB -or $entries.Count -ge 10000){throw '修复文件预算超限。'}
            $index=$entries.Count
            $staged=Join-Path $transaction ($index.ToString()+'.new')
            [IO.File]::Copy($file.Source,$staged,$false)
            if((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $file.Sha256){throw '修复载荷暂存摘要不符。'}
            $exists=[IO.File]::Exists($target)
            $entries+=,[ordered]@{target=$target;staged=$staged;backup=(Join-Path $transaction ($index.ToString()+'.old'));
                existed=$exists;sha256=[string]$file.Sha256;originalSha256=if($exists){[string](Get-FileHash -LiteralPath $target).Hash}else{''}}
        }
        Save-RepairJournal 'Prepared' ''
        foreach($entry in $entries){
            Assert-RepairPath $entry.target
            $touched+=,$entry
            if($entry.existed){
                if((Get-FileHash -LiteralPath $entry.target).Hash -ne $entry.originalSha256){throw '修复目标在替换前变化。'}
                [IO.File]::Replace($entry.staged,$entry.target,$entry.backup)
            }else{
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($entry.target))|Out-Null
                [IO.File]::Move($entry.staged,$entry.target)
            }
            if((Get-FileHash -LiteralPath $entry.target).Hash -ne $entry.sha256){throw '修复后摘要不符。'}
        }
        Save-RepairJournal 'Replaced' ''
        return $transaction
    }catch{
        $failure=$_.Exception.Message;$rollbackFailures=@()
        for($index=$touched.Count-1;$index -ge 0;$index--){
            $entry=$touched[$index]
            try{
                Assert-RepairPath $entry.target
                if([IO.File]::Exists($entry.backup)){
                    if((Get-FileHash -LiteralPath $entry.backup).Hash -ne $entry.originalSha256){throw '回滚副本摘要不符。'}
                    if([IO.File]::Exists($entry.target)){[IO.File]::Replace($entry.backup,$entry.target,$entry.staged+'.failed')}
                    else{[IO.File]::Move($entry.backup,$entry.target)}
                }elseif(-not $entry.existed -and [IO.File]::Exists($entry.target)){
                    if((Get-FileHash -LiteralPath $entry.target).Hash -ne $entry.sha256){throw '新文件归属变化，拒绝删除。'}
                    Remove-Item -LiteralPath $entry.target
                }
            }catch{$rollbackFailures+=,[string]$_.Exception.Message}
        }
        Save-RepairJournal $(if($rollbackFailures.Count){'RollbackFailed'}else{'RolledBack'}) ($failure+'; '+($rollbackFailures -join '; '))
        throw ('文件修复失败，事务证据：'+$transaction+'；'+$failure+'；回滚错误：'+($rollbackFailures -join '; '))
    }
    } finally {$repairLease.Dispose()}
}
$plan=Get-IndependentBundlePlan $BundleDirectory $InstallRoot
if($Mode -eq 'Validate'){$plan;return}
if($Mode -eq 'ValidateRepair'){
    Assert-IndependentInstallParent $plan.Destination
    $receiptPath=Join-Path $plan.Destination 'installed-files.json'
    $receiptFile=Get-Item -LiteralPath $receiptPath
    if($receiptFile.Length -gt 4MB -or ($receiptFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '原安装记录大小或路径无效。'}
    $receipt=[IO.File]::ReadAllText($receiptPath)|ConvertFrom-Json
    Get-IndependentRepairFiles $plan $receipt
    return
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try{
    if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '安装需要管理员权限。'}
}finally{$identity.Dispose()}
Assert-IndependentComponentVersions $plan
if($Mode -eq 'Repair'){
    Assert-IndependentInstallParent $plan.Destination
    $receiptPath=Join-Path $plan.Destination 'installed-files.json'
    $receiptFile=Get-Item -LiteralPath $receiptPath
    if($receiptFile.Length -gt 4MB -or ($receiptFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '原安装文件记录无效。'}
    $repairFiles=@(Get-IndependentRepairFiles $plan ([IO.File]::ReadAllText($receiptPath)|ConvertFrom-Json))
    $protocol=@($plan.Files|Where-Object Relative -eq 'FallbackGuard/MTTFTest.Watchdog.Protocol.dll')[0]
    $assembly=[Reflection.Assembly]::LoadFrom($protocol.Source)
    if(-not [string]::Equals($assembly.Location,$protocol.Source,[StringComparison]::OrdinalIgnoreCase)){throw '当前进程已加载其他协议组件，请在新安装器进程中执行修复。'}
    $registrationPath=Join-Path $plan.Destination 'IndependentState\registration.json'
    $registration=[MTTFTest.Watchdog.Protocol.IndependentExecutorRegistration]::LoadTrustedForMaintenance($registrationPath)
    $main=Join-Path $plan.Destination 'Current\MTTFTest.exe'
    $executor=Join-Path $plan.Destination 'FallbackGuard\MTTFTest.FallbackGuard.exe'
    if(-not [string]::Equals($registration.ExecutablePath,$main,[StringComparison]::OrdinalIgnoreCase) -or
       -not [string]::Equals($registration.SafetyExecutablePath,(Join-Path $plan.Destination 'Current\MTTFTest.SafetyAgent.exe'),[StringComparison]::OrdinalIgnoreCase)){
        throw '注册组件路径不属于本安装。'
    }
    $store=[MTTFTest.Watchdog.Protocol.IndependentProjectStateStore]::new($registration.StateDirectory)
    $state=$store.Read()
    if($state -and $state.Intent){$registration.RequireBoundIntent($state.Intent)}
    [MTTFTest.Watchdog.Protocol.IndependentInstallationBinding]::RequireControllerAbsent($main)
    $store.SetInstallationMaintenance($(if($state){$state.Revision}else{0L}),$true)
    $serviceName='MTTFTestIndependent-'+$registration.InstallationId
    $command='"{0}" --independent-service --registration "{1}"' -f $executor,$registrationPath
    $service=Get-CimInstance Win32_Service -Filter ("Name='"+$serviceName+"'") -OperationTimeoutSec 10
    if($service){
        if($service.PathName -ne $command -or $service.StartName -notin @('LocalSystem','NT AUTHORITY\SYSTEM')){throw '独立服务归属不符，保留维护状态。'}
        Stop-Service -Name $serviceName
        $controller=Get-Service -Name $serviceName
        try{$controller.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(15))}finally{$controller.Dispose()}
    }
    $lease=[MTTFTest.Watchdog.Protocol.IndependentExecutorLease]::new($registration.StateDirectory,$registration.InstallationId)
    try{
        [MTTFTest.Watchdog.Protocol.IndependentInstallationBinding]::RequireControllerAbsent($main)
        $state=$store.Read()
        if(-not $state.Maintenance -or $state.SafetyCleanupPending -or ($state.Intent -and $state.Intent.Armed -and -not $state.Intent.ManualStopped)){throw '维护状态变化，未替换文件。'}
        foreach($child in @($state.SessionProcesses)){
            $process=$null
            try{
                try{$process=[Diagnostics.Process]::GetProcessById($child.Process.Pid)}catch [ArgumentException]{continue}
                if(-not $process.HasExited -and $process.StartTime.ToUniversalTime().Ticks -eq $child.Process.StartUtcTicks){
                    throw '旧会话辅助进程尚未退出，未替换文件。'
                }
            }finally{if($process){$process.Dispose()}}
        }
        $transaction=Invoke-IndependentFileRepair $repairFiles $plan.Destination
        [MTTFTest.Watchdog.Protocol.IndependentExecutorRegistration]::LoadTrusted($registrationPath)|Out-Null
        & (Join-Path $plan.Destination 'Tools\Manage-IndependentRecovery.ps1') -Mode Repair -RegistrationPath $registrationPath -ExecutorPath $executor
        & (Join-Path $plan.Destination 'Tools\Manage-IndependentRecovery.ps1') -Mode Shortcut -RegistrationPath $registrationPath -ExecutorPath $executor
        Write-Output ('原构建组件已修复，服务保持维护停止状态；未启动试验。文件事务：'+$transaction)
    }finally{$lease.Dispose()}
    return
}
if([string]::IsNullOrWhiteSpace($ProjectDirectory) -or [string]::IsNullOrWhiteSpace($InteractiveUserSid)){throw '必须明确项目目录及实际交互用户 SID。'}
if([IO.Directory]::Exists($plan.Destination) -or [IO.File]::Exists($plan.Destination)){throw '已有安装目录，拒绝覆盖；必须走维护升级流程。'}
Assert-IndependentInstallParent ([IO.Path]::GetDirectoryName($plan.Destination))
# Create with its final ACL; do not leave an inherited writable interval.
$security=[Security.AccessControl.DirectorySecurity]::new()
$security.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
[IO.Directory]::CreateDirectory($plan.Destination,$security)|Out-Null
$createdAcl=Get-Acl -LiteralPath $plan.Destination
if($createdAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544') -or
   -not $createdAcl.AreAccessRulesProtected -or
   @($createdAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])|Where-Object{
       $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')}).Count){
    throw '安装目录创建后权限不符，拒绝写入或加载组件。'
}
if(((Get-Item -LiteralPath $plan.Destination).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '安装目录身份已变化。'}
$journal=Join-Path $plan.Destination 'installation-result.json'
try{
    # Persist ownership before the first payload write. Failed installation
    # retains this receipt so later maintenance never guesses owned files.
    $receipt=[ordered]@{schemaVersion=1;version=$plan.Version;installRoot=$plan.Destination;
        files=@($plan.Files|ForEach-Object{[ordered]@{path=[string]$_.Relative;sha256=[string]$_.Sha256}})}
    [IO.File]::WriteAllText((Join-Path $plan.Destination 'installed-files.json'),($receipt|ConvertTo-Json -Depth 4))
    foreach($file in $plan.Files){
        $target=Join-Path $plan.Destination $file.Relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
        [IO.File]::Copy($file.Source,$target,$false)
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.Sha256){throw '落盘文件摘要不符，拒绝加载。'}
    }
    $main=Join-Path $plan.Destination 'Current\MTTFTest.exe'
    $executor=Join-Path $plan.Destination 'FallbackGuard\MTTFTest.FallbackGuard.exe'
    foreach($component in @($main,$executor,(Join-Path $plan.Destination 'Current\MTTFTest.SafetyAgent.exe'))){
        if([Diagnostics.FileVersionInfo]::GetVersionInfo($component).FileVersion -ne $plan.Version){throw '组件版本与安装清单不一致。'}
    }
    $registration=Join-Path $plan.Destination 'IndependentState\registration.json'
    & (Join-Path $plan.Destination 'Tools\Manage-IndependentRecovery.ps1') -Mode Provision -RegistrationPath $registration `
        -ExecutorPath $executor -MainExecutablePath $main -ProjectDirectory $ProjectDirectory -InteractiveUserSid $InteractiveUserSid
    & (Join-Path $plan.Destination 'Tools\Manage-IndependentRecovery.ps1') -Mode Shortcut -RegistrationPath $registration -ExecutorPath $executor
    [IO.File]::WriteAllText($journal,([ordered]@{version=$plan.Version;stage='Provisioned';registration=$registration;trialStarted=$false;utc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 3))
}catch{
    [IO.File]::WriteAllText($journal,([ordered]@{version=$plan.Version;stage='Failed';error=[string]$_.Exception.Message;trialStarted=$false;utc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 3))
    throw
}
