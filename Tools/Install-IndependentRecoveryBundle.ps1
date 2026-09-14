#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Validate','ValidateRepair','Install')][string]$Mode='Validate',
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
