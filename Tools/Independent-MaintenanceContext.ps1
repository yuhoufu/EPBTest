#requires -Version 5.1
# Data-only discovery. The installer still validates the complete selected bundle
# and protected transaction before any maintenance operation is allowed.
function Read-IndependentMaintenanceJson([string]$Path) {
    $file=Get-Item -LiteralPath $Path -ErrorAction Stop
    if($file -isnot [IO.FileInfo] -or $file.Length -gt 4MB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '维护记录类型或大小无效。'}
    return ([IO.File]::ReadAllText($file.FullName)|ConvertFrom-Json)
}
function Get-IndependentMaintenanceTransaction([string]$Root,[string]$Mode,[string]$Version) {
    $category=if($Mode -eq 'RecoverFiles'){'Repair'}else{'Upgrade'}
    $parent=Join-Path $Root $category
    if(-not [IO.Directory]::Exists($parent)){return $null}
    $candidates=@();$count=0
    foreach($directory in [IO.Directory]::EnumerateDirectories($parent)){
        if(++$count -gt 256){throw '维护历史超过自动检查上限。'}
        $id=[IO.Path]::GetFileName($directory)
        if($id -notmatch '^[a-fA-F0-9]{32}$' -or ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '维护事务目录无效。'}
        $name=if($category -eq 'Repair'){'transaction.json'}else{'result.json'}
        $record=Read-IndependentMaintenanceJson (Join-Path $directory $name)
        $eligible=if($Mode -eq 'RecoverFiles'){$record.phase -in @('Prepared','RollbackFailed')}
            elseif($Mode -eq 'FinalizeUpgrade'){$record.toVersion -eq $Version -and $record.stage -in @('FilesCommitted','Registering','UpdatingShortcut','FinalizationFailed')}
            else{$record.toVersion -eq $Version -and $record.stage -in @('Completed','FilesCommitted','Registering','UpdatingShortcut','FinalizationFailed','RollingBack','RollbackFailed')}
        if($eligible){$candidates+=,[pscustomobject]@{Id=$id;Record=$record}}
    }
    if($candidates.Count -gt 1){throw '存在多个待处理事务，未自动选择；请先检查维护记录以确定目标，避免回退错误版本。'}
    if($candidates.Count -eq 1){return $candidates[0]}
    return $null
}
function Find-IndependentPreviousBundle([string]$Root,[string]$Version) {
    $cache=Join-Path $Root 'BundleCache'
    if(-not [IO.Directory]::Exists($cache)){return ''}
    $receipt=$null;$receiptPath=Join-Path $Root 'installed-files.json'
    if([IO.File]::Exists($receiptPath)){
        $candidateReceipt=Read-IndependentMaintenanceJson $receiptPath
        if($candidateReceipt.version -eq $Version){$receipt=$candidateReceipt}
    }
    $found=@();$count=0
    foreach($directory in [IO.Directory]::EnumerateDirectories($cache)){
        if(++$count -gt 64){throw '安装包缓存超过自动检查上限。'}
        if(([IO.Path]::GetFileName($directory)).StartsWith('.')){continue}
        $path=Join-Path $directory 'automatic-bundle.json'
        $manifest=Read-IndependentMaintenanceJson $path
        if($manifest.version -eq $Version){
            if($receipt){
                $digests=@{};foreach($entry in $manifest.files){$digests[[string]$entry.path]=[string]$entry.sha256}
                $matches=@($receipt.files).Count -gt 0
                foreach($owned in $receipt.files){
                    $relative=[string]$owned.path
                    $source=if($relative.StartsWith('Current/')){'Base/'+$relative.Substring(8)}else{$relative}
                    if(-not $digests.ContainsKey($source) -or $digests[$source] -ne $owned.sha256){$matches=$false;break}
                }
                if(-not $matches){continue}
            }
            $found+=,$directory
        }
    }
    if($found.Count -gt 1){throw '同版本存在多个构建缓存，不能猜测原安装包。'}
    if($found.Count -eq 1){return $found[0]}
    return ''
}
function Select-IndependentPreviousBundle([string]$Version) {
    if([Diagnostics.Process]::GetCurrentProcess().SessionId -le 0){throw '原构建安装包尚未缓存；请从试验账户桌面运行入口，选择原安装包文件夹。'}
    Add-Type -AssemblyName System.Windows.Forms
    $dialog=[Windows.Forms.FolderBrowserDialog]::new()
    try{
        $dialog.Description='首次维护旧安装：请选择 V'+$Version+' 原构建完整安装包文件夹。以后自动使用缓存，无需输入参数。'
        $dialog.ShowNewFolderButton=$false
        if($dialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK){throw '已取消选择原安装包，未执行维护。'}
        return $dialog.SelectedPath
    }finally{$dialog.Dispose()}
}
function Find-IndependentNearbyBundle([string]$Bundle,[string]$Version,[string]$MainHash) {
    $parent=[IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Bundle)).Parent
    if(-not $parent){return ''}
    $searchRoot=$parent.FullName;$cursor=$parent
    for($i=0;$i -lt 6 -and $cursor -and $cursor.Parent;$i++){
        if($cursor.Name -eq '_EPB'){$searchRoot=$cursor.FullName;break}
        $cursor=$cursor.Parent
    }
    if($searchRoot -eq [IO.Path]::GetPathRoot($searchRoot)){return ''}
    $queue=[Collections.Generic.Queue[object]]::new();$queue.Enqueue(@{path=$searchRoot;depth=0})
    $clock=[Diagnostics.Stopwatch]::StartNew();$count=0;$found=@()
    while($queue.Count){
        if(++$count -gt 512 -or $clock.Elapsed.TotalSeconds -gt 5){throw '相邻安装包自动查找达到范围上限，未猜测原包。'}
        $item=$queue.Dequeue();$directory=Get-Item -LiteralPath $item.path
        if($directory.Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
        $manifestPath=Join-Path $item.path 'automatic-bundle.json'
        if([IO.File]::Exists($manifestPath)){
            try{
                $manifest=Read-IndependentMaintenanceJson $manifestPath
                $main=@($manifest.files|Where-Object path -eq 'Base/MTTFTest.exe')
                if($manifest.version -eq $Version -and $main.Count -eq 1 -and (-not $MainHash -or $main[0].sha256 -eq $MainHash)){$found+=,[string]$item.path}
            }catch{continue}
            continue
        }
        if($item.depth -ge 7){continue}
        foreach($child in [IO.Directory]::EnumerateDirectories($item.path)){
            if([IO.Path]::GetFileName($child) -in @('Base','FallbackGuard','Tools','Evidence','.git','node_modules')){continue}
            if($queue.Count -ge 512){throw '相邻安装包目录数量超过自动检查上限。'}
            $queue.Enqueue(@{path=$child;depth=$item.depth+1})
        }
    }
    if($found.Count -eq 1){return $found[0]}
    return ''
}
function Save-IndependentBundleCache([string]$Bundle,[string]$Root,[switch]$ArchitectureRepair) {
    # Invoke only after the caller has validated this bundle and the trusted
    # installation root. Cache only manifest-owned files, never logs/evidence.
    $manifest=Read-IndependentMaintenanceJson (Join-Path $Bundle 'automatic-bundle.json')
    if($manifest.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $manifest.gitCommit -notmatch '^[a-fA-F0-9]{40}$'){throw '缓存安装包身份无效。'}
    $patchBytes=$null
    if($ArchitectureRepair -and $manifest.version -eq '4.1.0.3' -and -not $manifest.installerRepair -and
        [IO.File]::Exists((Join-Path $Root 'InstallerArchitectureRepair\result.json'))){
        $repair=Read-IndependentMaintenanceJson (Join-Path $Root 'InstallerArchitectureRepair\result.json')
        $receipt=Read-IndependentMaintenanceJson (Join-Path $Root 'installed-files.json')
        $entry=@($manifest.files|Where-Object path -eq 'Tools/Manage-IndependentRecovery.ps1')
        $owned=@($receipt.files|Where-Object path -eq 'Tools/Manage-IndependentRecovery.ps1')
        $originalHash='4A89FAFF0EC2940F49161676E9A0C307F59AA61251CB8F8F35E5C2493404B0AA'
        $manager=Join-Path $Bundle 'Tools\Manage-IndependentRecovery.ps1'
        if($entry.Count -ne 1 -or $owned.Count -ne 1 -or $entry[0].sha256 -ne $originalHash -or
            $repair.oldSha256 -ne $originalHash -or (Get-FileHash $manager).Hash -ne $originalHash -or
            (Get-FileHash (Join-Path $Bundle 'Base\MTTFTest.exe')).Hash -ne '5D2409E723A0D2C93173D639E0EAEAB393757FDF7F829B1722AA8D68BD7732A6'){
            throw '旧版安装器修复证据与原包不匹配。'
        }
        $text=[IO.File]::ReadAllText($manager).Replace('[Reflection.Assembly]::LoadFrom($MainExecutablePath)','[Reflection.Assembly]::ReflectionOnlyLoadFrom($MainExecutablePath)')
        $encoding=[Text.UTF8Encoding]::new($true);$patchBytes=$encoding.GetPreamble()+$encoding.GetBytes($text)
        $sha=[Security.Cryptography.SHA256]::Create()
        try{$patchHash=([BitConverter]::ToString($sha.ComputeHash($patchBytes))).Replace('-','')}finally{$sha.Dispose()}
        if($repair.newSha256 -ne $patchHash -or $owned[0].sha256 -ne $patchHash -or
            (Get-FileHash (Join-Path $Root 'Tools\Manage-IndependentRecovery.ps1')).Hash -ne $patchHash){throw '已安装的位数修复无法与原包建立一致关系。'}
        $manifest|Add-Member -NotePropertyName installerRepair -NotePropertyValue ([ordered]@{kind='MetadataOnlyArchitectureProbe';originalManagerSha256=$originalHash;managerSha256=$patchHash})
    }
    $cache=Join-Path $Root 'BundleCache'
    [IO.Directory]::CreateDirectory($cache)|Out-Null
    if((Get-Item -LiteralPath $cache).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '安装包缓存是重解析点。'}
    $destination=Join-Path $cache ($manifest.version+'-'+$manifest.gitCommit+$(if($manifest.installerRepair){'-architecture-repair'}else{''}))
    if([IO.Directory]::Exists($destination)){
        if($patchBytes){
            $saved=Read-IndependentMaintenanceJson (Join-Path $destination 'automatic-bundle.json')
            if($saved.installerRepair.managerSha256 -ne $patchHash -or (Get-FileHash (Join-Path $destination 'Tools\Manage-IndependentRecovery.ps1')).Hash -ne $patchHash){throw '位数修复缓存不一致。'}
        }elseif((Get-FileHash (Join-Path $destination 'automatic-bundle.json')).Hash -ne (Get-FileHash (Join-Path $Bundle 'automatic-bundle.json')).Hash){throw '同身份缓存清单不同，保留证据。'}
        return $destination
    }
    $temporary=Join-Path $cache ('.pending-'+[Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($temporary)|Out-Null
    $count=0;[long]$total=0;$seen=@{}
    foreach($entry in $manifest.files){
        $relative=[string]$entry.path
        if(++$count -gt 10000 -or [IO.Path]::IsPathRooted($relative) -or $relative -match '[:\\]|(^|/)\.\.?(/|$)' -or $seen.ContainsKey($relative)){throw '缓存清单路径无效。'}
        $seen[$relative]=$true
        $source=Join-Path $Bundle $relative;$target=[IO.Path]::GetFullPath((Join-Path $temporary $relative))
        if(-not $target.StartsWith($temporary+'\',[StringComparison]::OrdinalIgnoreCase)){throw '缓存路径越界。'}
        $file=Get-Item -LiteralPath $source;$total+=$file.Length
        if($file.Length -gt 256MB -or $total -gt 2GB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '缓存源文件超过预算或路径无效。'}
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
        [IO.File]::Copy($source,$target,$false)
        if((Get-FileHash $target).Hash -ne $entry.sha256){throw '缓存安装包摘要不符。'}
    }
    [IO.File]::Copy((Join-Path $Bundle 'automatic-bundle.json'),(Join-Path $temporary 'automatic-bundle.json'),$false)
    if($patchBytes){
        [IO.File]::WriteAllBytes((Join-Path $temporary 'Tools\Manage-IndependentRecovery.ps1'),$patchBytes)
        $entry=@($manifest.files|Where-Object path -eq 'Tools/Manage-IndependentRecovery.ps1');$entry[0].sha256=$patchHash.ToLowerInvariant()
        [IO.File]::WriteAllText((Join-Path $temporary 'automatic-bundle.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($true))
    }
    [IO.Directory]::Move($temporary,$destination)
    return $destination
}
