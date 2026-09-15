#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Validate','ValidateRepair','ValidateUpgrade','Upgrade','Repair','RecoverFiles','Uninstall','Install')][string]$Mode='Validate',
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [string]$ProjectDirectory,
    [string]$InteractiveUserSid,
    [string]$TransactionId,
    [string]$PreviousBundleDirectory
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
function Get-IndependentUpgradePlan($CurrentPlan,$NextPlan,$Receipt) {
    if(-not [string]::Equals($CurrentPlan.Destination,$NextPlan.Destination,[StringComparison]::OrdinalIgnoreCase)){
        throw '升级必须针对已核验的同一安装目录。'
    }
    if($CurrentPlan.Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $NextPlan.Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
       [version]$NextPlan.Version -le [version]$CurrentPlan.Version){throw '升级版本必须高于原安装版本；回滚不能冒充升级。'}
    $current=@(Get-IndependentRepairFiles $CurrentPlan $Receipt)
    # Both plans must originate from verified bundles. A synthetic receipt for
    # the next plan only reuses component/config selection, not old ownership.
    $nextReceipt=[pscustomobject]@{schemaVersion=1;version=$NextPlan.Version;installRoot=$NextPlan.Destination;
        files=@($NextPlan.Files|ForEach-Object{[pscustomobject]@{path=$_.Relative;sha256=$_.Sha256}})}
    $next=@(Get-IndependentRepairFiles $NextPlan $nextReceipt)
    $nextPaths=@{};foreach($file in $next){$nextPaths[$file.Relative]=$true}
    $obsolete=@($current|Where-Object{-not $nextPaths.ContainsKey($_.Relative)})
    $preserved=@($CurrentPlan.Files|Where-Object{$_.Relative -like 'Current/Config/*' -or $_.Relative -notmatch '(?i)\.(exe|dll|pdb|ps1|exe\.config)$'})
    [pscustomobject]@{FromVersion=$CurrentPlan.Version;ToVersion=$NextPlan.Version;Destination=$CurrentPlan.Destination;
        ReplacementFiles=$next;ObsoleteFiles=$obsolete;PreservedFiles=$preserved}
}
function Get-IndependentUpgradeRegistration($Registration,$State,$NextPlan) {
    # Pure metadata preparation. Caller persists the result only in the same
    # maintenance transaction as component hashes and the installation receipt.
    $Registration.Validate();$State.Validate()
    if(-not $State.Maintenance -or $State.SafetyCleanupPending -or
       ($State.Transaction -and -not $State.Transaction.IsTerminal) -or
       ($State.Intent -and $State.Intent.Armed -and -not $State.Intent.ManualStopped) -or
       ($State.Ticket -and -not $State.Ticket.Revoked)){
        throw 'Upgrade registration requires quiescent maintenance with revoked launch authority.'
    }
    if($State.Intent){$Registration.RequireBoundIntent($State.Intent)}
    $hashes=@()
    foreach($path in @($Registration.ExecutablePath,$Registration.SafetyExecutablePath)){
        $components=@($NextPlan.Files|Where-Object{
            [IO.Path]::GetFullPath((Join-Path $NextPlan.Destination $_.Relative)) -eq $path
        })
        if($components.Count -ne 1 -or $components[0].Sha256 -notmatch '^[a-fA-F0-9]{64}$'){
            throw 'Upgrade must preserve exact registered executable paths with verified hashes.'
        }
        $hashes+=,[string]$components[0].Sha256
    }
    $serializer=[System.Web.Script.Serialization.JavaScriptSerializer]::new()
    $serializer.MaxJsonLength=4MB;$serializer.RecursionLimit=32
    $copy=$serializer.Deserialize($serializer.Serialize($Registration),$Registration.GetType())
    $copy.ExecutableSha256=$hashes[0];$copy.SafetyExecutableSha256=$hashes[1]
    $copy.Validate()
    if($State.Intent){$copy.RequireBoundIntent($State.Intent)}
    return $copy
}
function Invoke-IndependentFileRepair([object[]]$Files,[string]$InstallDirectory,[object[]]$RetiredFiles=@(),[scriptblock]$VerifyReplacement=$null,[object[]]$MetadataFiles=@()) {
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
            if($previous.schemaVersion -notin @(1,2) -or $previous.phase -notin @('Replaced','RolledBack')){
                throw ('存在未收尾修复事务，必须先恢复该事务：'+$directory)
            }
        }
    }
    [IO.Directory]::CreateDirectory($transaction)|Out-Null
    $journal=Join-Path $transaction 'transaction.json'
    function Save-RepairJournal([string]$Phase,[string]$Failure){
        $text=[ordered]@{schemaVersion=$(if($MetadataFiles.Count){2}else{1});phase=$Phase;failure=$Failure;entries=$entries;utc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 4
        $temp=$journal+'.tmp';[IO.File]::WriteAllText($temp,$text)
        if([IO.File]::Exists($journal)){[IO.File]::Replace($temp,$journal,$journal+'.previous')}else{[IO.File]::Move($temp,$journal)}
    }
    try{
        $seen=@{};[long]$bytes=0
        $payloads=@($Files|ForEach-Object{[pscustomobject]@{File=$_;Metadata=$false}})+@($MetadataFiles|ForEach-Object{[pscustomobject]@{File=$_;Metadata=$true}})
        foreach($payload in $payloads){
            $file=$payload.File;$relative=([string]$file.Relative).Replace('\','/')
            $target=[IO.Path]::GetFullPath((Join-Path $root $relative))
            $allowed=if($payload.Metadata){$relative -cin @('IndependentState/registration.json','installed-files.json')}
                else{$relative -match '^(Current|FallbackGuard|Tools)/' -and $relative -notlike 'Current/Config/*' -and $relative -match '(?i)\.(exe|dll|pdb|ps1|exe\.config)$'}
            if(-not $target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($target) -or -not $allowed){
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
            if($payload.Metadata -and -not $exists){throw 'Upgrade metadata must replace an existing installation record.'}
            $entries+=,[ordered]@{target=$target;staged=$staged;backup=(Join-Path $transaction ($index.ToString()+'.old'));
                metadata=$payload.Metadata;retired=$false;existed=$exists;sha256=[string]$file.Sha256;originalSha256=if($exists){[string](Get-FileHash -LiteralPath $target).Hash}else{''}}
        }
        foreach($file in $RetiredFiles){
            $relative=([string]$file.Relative).Replace('\','/')
            $target=[IO.Path]::GetFullPath((Join-Path $root $relative))
            if(-not $target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($target) -or
               $relative -notmatch '^(Current|FallbackGuard|Tools)/' -or $relative -like 'Current/Config/*' -or
               $relative -notmatch '(?i)\.(exe|dll|pdb|ps1|exe\.config)$' -or $file.Sha256 -notmatch '^[a-fA-F0-9]{64}$'){
                throw 'Retired component identity is invalid.'
            }
            $seen[$target]=$true;Assert-RepairPath $target
            if(-not [IO.File]::Exists($target)){throw 'Retired component is missing; reconcile the original installation first.'}
            $length=(Get-Item -LiteralPath $target).Length;$bytes+=$length
            if($length -gt 256MB -or $bytes -gt 2GB -or $entries.Count -ge 10000){throw 'Retired component budget exceeded.'}
            if((Get-FileHash -LiteralPath $target).Hash -ne $file.Sha256){throw 'Retired component ownership hash mismatch.'}
            $index=$entries.Count
            $entries+=,[ordered]@{target=$target;staged=(Join-Path $transaction ($index.ToString()+'.new'));
                backup=(Join-Path $transaction ($index.ToString()+'.old'));retired=$true;existed=$true;
                sha256='';originalSha256=[string]$file.Sha256}
        }
        Save-RepairJournal 'Prepared' ''
        foreach($entry in $entries){
            Assert-RepairPath $entry.target
            $touched+=,$entry
            if($entry.existed){
                if((Get-FileHash -LiteralPath $entry.target).Hash -ne $entry.originalSha256){throw '修复目标在替换前变化。'}
                if($entry.retired){[IO.File]::Move($entry.target,$entry.backup)}
                else{[IO.File]::Replace($entry.staged,$entry.target,$entry.backup)}
            }else{
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($entry.target))|Out-Null
                [IO.File]::Move($entry.staged,$entry.target)
            }
            if($entry.retired){
                if([IO.File]::Exists($entry.target) -or (Get-FileHash -LiteralPath $entry.backup).Hash -ne $entry.originalSha256){throw 'Retired component backup verification failed.'}
            }elseif((Get-FileHash -LiteralPath $entry.target).Hash -ne $entry.sha256){throw '修复后摘要不符。'}
        }
        # Read-only postconditions run within the rollback boundary. Metadata
        # mutations require their own coordinated transaction, not this callback.
        if($VerifyReplacement){& $VerifyReplacement | Out-Null}
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
function Restore-IndependentFileTransaction([string]$InstallDirectory,[string]$TransactionId) {
    # Caller owns trusted installation maintenance and the executor lease.
    # Preserve backups during replay, so replay itself can be interrupted.
    if($TransactionId -notmatch '^[a-fA-F0-9]{32}$'){throw 'Invalid transaction ID.'}
    $root=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $directory=Join-Path $root ('Repair\'+$TransactionId)
    function Assert-ReplayPath([string]$Path){
        $cursor=$Path
        while($cursor){
            if(Test-Path -LiteralPath $cursor){
                if(((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Replay path contains a reparse point.'}
            }
            $cursor=[IO.Path]::GetDirectoryName($cursor)
        }
    }
    Assert-ReplayPath $directory
    $lockPath=Join-Path $root 'file-repair.lock';Assert-ReplayPath $lockPath
    $lease=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
        $journal=Join-Path $directory 'transaction.json';Assert-ReplayPath $journal
        if(-not [IO.File]::Exists($journal) -or (Get-Item -LiteralPath $journal).Length -gt 4MB){throw 'Missing or oversized transaction journal.'}
        $record=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
        if($record.schemaVersion -notin @(1,2) -or $record.phase -notin @('Prepared','RollbackFailed','RolledBack')){throw 'Transaction is not eligible for interrupted rollback.'}
        $entries=@($record.entries)
        if($entries.Count -eq 0 -or $entries.Count -gt 10000){throw 'Invalid replay entry count.'}
        $seen=@{};[long]$bytes=0
        foreach($entry in $entries){
            $target=[IO.Path]::GetFullPath([string]$entry.target)
            $backup=[IO.Path]::GetFullPath([string]$entry.backup)
            if(-not $target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or
               [IO.Path]::GetDirectoryName($backup) -ne $directory -or [IO.Path]::GetFileName($backup) -notmatch '^\d+\.old$' -or
               $seen.ContainsKey($target) -or $seen.ContainsKey($backup) -or $entry.existed -isnot [bool]){throw 'Invalid replay file identity.'}
            $relative=$target.Substring($root.Length+1).Replace('\','/')
            $metadata=$record.schemaVersion -eq 2 -and $entry.metadata -is [bool] -and $entry.metadata
            $allowed=if($metadata){$relative -cin @('IndependentState/registration.json','installed-files.json') -and $entry.existed}
                else{$relative -match '^(Current|FallbackGuard|Tools)/' -and $relative -notlike 'Current/Config/*' -and $relative -match '(?i)\.(exe|dll|pdb|ps1|exe\.config)$'}
            if(-not $allowed){throw 'Replay target outside component scope.'}
            $seen[$target]=$true;$seen[$backup]=$true
            Assert-ReplayPath $target;Assert-ReplayPath $backup
            $currentHash='';$backupHash=''
            foreach($path in @($target,$backup)){
                if([IO.Directory]::Exists($path)){throw 'Replay component is a directory.'}
                if([IO.File]::Exists($path)){
                    $length=(Get-Item -LiteralPath $path).Length;$bytes+=$length
                    if($length -gt 256MB -or $bytes -gt 2GB){throw 'Replay file budget exceeded.'}
                    $hash=(Get-FileHash -LiteralPath $path).Hash
                    if($path -eq $target){$currentHash=$hash}else{$backupHash=$hash}
                }
            }
            if($entry.existed){
                if($entry.originalSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
                   ($backupHash -and $backupHash -ne $entry.originalSha256) -or
                   (-not $backupHash -and $currentHash -ne $entry.originalSha256) -or
                   ($currentHash -and $currentHash -ne $entry.originalSha256 -and $currentHash -ne $entry.sha256)){throw 'Original component cannot be safely recovered.'}
            }elseif($entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $backupHash -or ($currentHash -and $currentHash -ne $entry.sha256)){
                throw 'New component ownership changed.'
            }
        }
        for($index=$entries.Count-1;$index -ge 0;$index--){
            $entry=$entries[$index]
            Assert-ReplayPath $entry.target
            if($entry.existed){
                if([IO.File]::Exists($entry.target) -and (Get-FileHash -LiteralPath $entry.target).Hash -eq $entry.originalSha256){continue}
                Assert-ReplayPath $entry.backup
                if((Get-FileHash -LiteralPath $entry.backup).Hash -ne $entry.originalSha256){throw 'Replay backup changed.'}
                $temp=Join-Path $directory ([Guid]::NewGuid().ToString('N')+'.restore')
                [IO.File]::Copy($entry.backup,$temp,$false)
                if([IO.File]::Exists($entry.target)){
                    if((Get-FileHash -LiteralPath $entry.target).Hash -ne $entry.sha256){throw 'Replay target changed.'}
                    [IO.File]::Replace($temp,$entry.target,$temp+'.displaced')
                }else{[IO.File]::Move($temp,$entry.target)}
                if((Get-FileHash -LiteralPath $entry.target).Hash -ne $entry.originalSha256){throw 'Restored hash mismatch.'}
            }elseif([IO.File]::Exists($entry.target)){
                if((Get-FileHash -LiteralPath $entry.target).Hash -ne $entry.sha256){throw 'New component changed during replay.'}
                Remove-Item -LiteralPath $entry.target
            }
        }
        $record.phase='RolledBack';$record.failure='';$record.utc=[DateTime]::UtcNow.ToString('O')
        $tempJournal=$journal+'.replay-'+[Guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllText($tempJournal,($record|ConvertTo-Json -Depth 4))
        [IO.File]::Replace($tempJournal,$journal,$journal+'.before-replay')
        return $directory
    }finally{$lease.Dispose()}
}
function Remove-IndependentOwnedComponents([object[]]$Files,[string]$InstallDirectory) {
    # Component/task teardown and executor maintenance lease are caller gates.
    # Never enumerate the install directory to infer ownership.
    $root=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $targets=@();$seen=@{}
    if(-not $Files -or $Files.Count -gt 10000){throw '卸载文件数量无效。'}
    foreach($file in $Files){
        $relative=([string]$file.Relative).Replace('\','/')
        $target=[IO.Path]::GetFullPath((Join-Path $root $relative))
        if(-not $target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($target) -or
           $relative -notmatch '^(Current|FallbackGuard|Tools)/' -or $relative -like 'Current/Config/*' -or
           $relative -notmatch '(?i)\.(exe|dll|pdb|ps1|exe\.config)$' -or $file.Sha256 -notmatch '^[a-fA-F0-9]{64}$'){
            throw '卸载目标不属于可删除的原安装组件。'
        }
        $seen[$target]=$true
        $cursor=$target
        while($cursor){
            if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '卸载路径含重解析点。'}
            $cursor=[IO.Path]::GetDirectoryName($cursor)
        }
        if([IO.Directory]::Exists($target)){throw '组件路径已变成目录，拒绝删除。'}
        if([IO.File]::Exists($target) -and (Get-FileHash -LiteralPath $target).Hash -ne $file.Sha256){throw '组件内容已变化，保留文件并拒绝本次删除。'}
        $targets+=,[pscustomobject]@{Path=$target;Sha256=[string]$file.Sha256}
    }
    foreach($target in $targets){
        if([IO.File]::Exists($target.Path)){
            if((Get-FileHash -LiteralPath $target.Path).Hash -ne $target.Sha256){throw '删除前组件身份变化。'}
            Remove-Item -LiteralPath $target.Path -ErrorAction Stop
        }
    }
    foreach($target in $targets){if(Test-Path -LiteralPath $target.Path){throw '组件删除未完成。'}}
}
function Invoke-IndependentUninstallSteps([string]$Root,[string]$Version,[string]$InstallationId,[scriptblock]$Unregister,[scriptblock]$RemoveFiles) {
    $journal=Join-Path $Root 'uninstall-result.json'
    $requestId=[Guid]::NewGuid().ToString('N')
    function Save-UninstallStage([string]$Stage,[string]$Failure){
        $text=[ordered]@{schemaVersion=1;requestId=$requestId;installationId=$InstallationId;version=$Version;
            stage=$Stage;failure=$Failure;maintenance=$true;utc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 3
        $temporary=$journal+'.'+$requestId+'.tmp'
        try{
            [IO.File]::WriteAllText($temporary,$text)
            if([IO.File]::Exists($journal)){[IO.File]::Replace($temporary,$journal,$journal+'.previous')}
            else{[IO.File]::Move($temporary,$journal)}
        }finally{if([IO.File]::Exists($temporary)){Remove-Item -LiteralPath $temporary}}
    }
    Save-UninstallStage 'Unregistering' ''
    try{
        & $Unregister|Out-Null
        Save-UninstallStage 'RemovingComponents' ''
        & $RemoveFiles|Out-Null
        Save-UninstallStage 'ComponentsRemoved' ''
    }catch{
        Save-UninstallStage 'Failed' ([string]$_.Exception.Message)
        throw
    }
}
$plan=Get-IndependentBundlePlan $BundleDirectory $InstallRoot
if($Mode -eq 'Validate'){$plan;return}
if($Mode -in @('ValidateRepair','ValidateUpgrade')){
    Assert-IndependentInstallParent $plan.Destination
    $receiptPath=Join-Path $plan.Destination 'installed-files.json'
    $receiptFile=Get-Item -LiteralPath $receiptPath
    if($receiptFile.Length -gt 4MB -or ($receiptFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '原安装记录大小或路径无效。'}
    $receipt=[IO.File]::ReadAllText($receiptPath)|ConvertFrom-Json
    if($Mode -eq 'ValidateUpgrade'){
        if(-not $PreviousBundleDirectory){throw 'Upgrade requires the original verified bundle.'}
        $previousPlan=Get-IndependentBundlePlan $PreviousBundleDirectory $InstallRoot
        Get-IndependentUpgradePlan $previousPlan $plan $receipt
    }else{Get-IndependentRepairFiles $plan $receipt}
    return
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try{
    if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '安装需要管理员权限。'}
}finally{$identity.Dispose()}
Assert-IndependentComponentVersions $plan
if($Mode -in @('Upgrade','Repair','RecoverFiles','Uninstall')){
    if($Mode -eq 'RecoverFiles' -and $TransactionId -notmatch '^[a-fA-F0-9]{32}$'){throw 'RecoverFiles requires an explicit transaction ID.'}
    Assert-IndependentInstallParent $plan.Destination
    $receiptPath=Join-Path $plan.Destination 'installed-files.json'
    $receiptFile=Get-Item -LiteralPath $receiptPath
    if($receiptFile.Length -gt 4MB -or ($receiptFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '原安装文件记录无效。'}
    $receipt=[IO.File]::ReadAllText($receiptPath)|ConvertFrom-Json
    if($Mode -eq 'Upgrade'){
        if(-not $PreviousBundleDirectory){throw 'Upgrade requires the original verified bundle.'}
        $previousPlan=Get-IndependentBundlePlan $PreviousBundleDirectory $InstallRoot
        Assert-IndependentComponentVersions $previousPlan
        $upgradePlan=Get-IndependentUpgradePlan $previousPlan $plan $receipt
        $repairFiles=@($upgradePlan.ReplacementFiles)
    }else{$repairFiles=@(Get-IndependentRepairFiles $plan $receipt)}
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
        if($Mode -eq 'Uninstall'){
            $cache=Join-Path $registration.StateDirectory ('maintenance-'+$protocol.Sha256.ToLowerInvariant())
            [IO.Directory]::CreateDirectory($cache)|Out-Null
            [MTTFTest.Watchdog.Protocol.IndependentProtectedFiles]::RequireTrustedDirectory($cache)
            $cachedProtocol=Join-Path $cache 'MTTFTest.Watchdog.Protocol.dll'
            if(-not [IO.File]::Exists($cachedProtocol)){[IO.File]::Copy($protocol.Source,$cachedProtocol,$false)}
            if((Get-FileHash -LiteralPath $cachedProtocol).Hash -ne $protocol.Sha256){throw '卸载维护协议摘要不符。'}
            $fileLease=[IO.File]::Open((Join-Path $plan.Destination 'file-repair.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            try{
                $manager=@($plan.Files|Where-Object Relative -eq 'Tools/Manage-IndependentRecovery.ps1')[0].Source
                Invoke-IndependentUninstallSteps $plan.Destination $plan.Version $registration.InstallationId {
                    & $manager -Mode Uninstall -RegistrationPath $registrationPath -ExecutorPath $executor -ProtocolAssemblyPath $cachedProtocol -InstalledVersion $plan.Version
                } {
                    Remove-IndependentOwnedComponents $repairFiles $plan.Destination
                }
                Write-Output '本安装服务、任务、快捷方式及原程序组件已移除。项目配置、数据、诊断和重复卸载所需的受保护维护协议保留。'
            }finally{$fileLease.Dispose()}
            return
        }
        if($Mode -eq 'RecoverFiles'){
            $restored=Restore-IndependentFileTransaction $plan.Destination $TransactionId
            Write-Output ('Interrupted file transaction rolled back; maintenance remains enabled and no trial was started: '+$restored)
            return
        }
        if($Mode -eq 'Upgrade'){
            Add-Type -AssemblyName System.Web.Extensions
            $nextRegistration=Get-IndependentUpgradeRegistration $registration $state $plan
            $upgradeId=[Guid]::NewGuid().ToString('N')
            $upgradeDirectory=Join-Path $plan.Destination ('Upgrade\'+$upgradeId)
            [IO.Directory]::CreateDirectory($upgradeDirectory)|Out-Null
            [MTTFTest.Watchdog.Protocol.IndependentProtectedFiles]::RequireTrustedDirectory($upgradeDirectory)
            $serializer=[System.Web.Script.Serialization.JavaScriptSerializer]::new()
            $serializer.MaxJsonLength=4MB;$serializer.RecursionLimit=32
            $registrationPayload=Join-Path $upgradeDirectory 'registration.next.json'
            [IO.File]::WriteAllText($registrationPayload,$serializer.Serialize($nextRegistration))
            $receiptPayload=Join-Path $upgradeDirectory 'installed-files.next.json'
            $nextReceipt=[ordered]@{schemaVersion=1;version=$plan.Version;installRoot=$plan.Destination;
                files=@($plan.Files|ForEach-Object{[ordered]@{path=$_.Relative;sha256=$_.Sha256}})}
            [IO.File]::WriteAllText($receiptPayload,($nextReceipt|ConvertTo-Json -Depth 4))
            $metadata=@(
                [pscustomobject]@{Relative='IndependentState/registration.json';Source=$registrationPayload;Sha256=(Get-FileHash $registrationPayload).Hash},
                [pscustomobject]@{Relative='installed-files.json';Source=$receiptPayload;Sha256=(Get-FileHash $receiptPayload).Hash})
            $expectedState=$serializer.Serialize($store.Read())
            $verify={
                [MTTFTest.Watchdog.Protocol.IndependentExecutorRegistration]::LoadTrusted($registrationPath)|Out-Null
                if($serializer.Serialize($store.Read()) -ne $expectedState){throw 'Persistent state changed during upgrade; rolling back files and metadata.'}
            }.GetNewClosure()
            $outcome=Join-Path $upgradeDirectory 'result.json'
            try{
                $transaction=Invoke-IndependentFileRepair -Files $repairFiles -InstallDirectory $plan.Destination -RetiredFiles $upgradePlan.ObsoleteFiles -VerifyReplacement $verify -MetadataFiles $metadata
                [IO.File]::WriteAllText($outcome,([ordered]@{stage='FilesCommitted';fromVersion=$previousPlan.Version;toVersion=$plan.Version;
                    transaction=$transaction;maintenance=$true;trialStarted=$false;finalizationRequired=$true}|ConvertTo-Json -Depth 3))
                Write-Output ('Upgrade files and registration committed; maintenance remains enabled. Service/task and shortcut finalization is still required; upgrade is not complete. Record: '+$outcome)
            }catch{
                [IO.File]::WriteAllText($outcome,([ordered]@{stage='Failed';failure=[string]$_.Exception.Message;maintenance=$true;trialStarted=$false}|ConvertTo-Json -Depth 3))
                throw
            }
            return
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
