#requires -Version 5.1
# Installation-only helpers. No controller, service or hardware is started here.
function Read-IndependentSetupJson([string]$Path) {
    $file=Get-Item -LiteralPath $Path -ErrorAction Stop
    if($file -isnot [IO.FileInfo] -or $file.Length -gt 1MB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '安装状态文件类型或大小无效。'}
    return ([IO.File]::ReadAllText($file.FullName)|ConvertFrom-Json)
}
function Get-IndependentInteractiveSid {
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    if($session -le 0){throw '请从实际运行试验的桌面账户启动安装入口。'}
    $owners=@(Get-CimInstance Win32_Process -Filter "Name='explorer.exe' AND SessionId=$session" -OperationTimeoutSec 10 |
        ForEach-Object { $owner=Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid -OperationTimeoutSec 10
            if($owner.ReturnValue -eq 0){[string]$owner.Sid} } | Sort-Object -Unique)
    if($owners.Count -ne 1 -or $owners[0] -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$'){throw '无法唯一识别当前桌面账户；请从实际试验账户桌面重新运行安装入口。'}
    return $owners[0]
}
function Test-IndependentExistingProject([string]$Path) {
    return (-not [string]::IsNullOrWhiteSpace($Path) -and [IO.Path]::IsPathRooted($Path) -and
        -not $Path.StartsWith('\\') -and -not $Path.Contains('"') -and
        [IO.File]::Exists((Join-Path $Path 'Config\TestConfig.xml')) -and [IO.File]::Exists((Join-Path $Path 'index.db')))
}
function Find-IndependentSetupProject([string]$UserStatePath,[string]$RuntimeConfigPath) {
    # Prefer an explicit last selection. Never replace a broken selection with a
    # different/default project, and never use a packaged sample as a live project.
    if([IO.File]::Exists($UserStatePath)){
        $state=Read-IndependentSetupJson $UserStatePath
        if($state.schemaVersion -ne 1){throw '最后项目记录版本无效。'}
        $store=[string]$state.storeDir;$name=[string]$state.testName
    }elseif([IO.File]::Exists($RuntimeConfigPath)){
        if((Get-Item -LiteralPath $RuntimeConfigPath).Length -gt 1MB){throw '项目配置过大。'}
        $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
        $reader=[Xml.XmlReader]::Create($RuntimeConfigPath,$settings)
        try{$doc=[Xml.XmlDocument]::new();$doc.XmlResolver=$null;$doc.Load($reader)}finally{$reader.Dispose()}
        $store=[string]$doc.TestConfig.Basic.StoreDir;$name=[string]$doc.TestConfig.Basic.TestName
    }else{return ''}
    if(-not [IO.Path]::IsPathRooted($store) -or [string]::IsNullOrWhiteSpace($name) -or
        $name -ne [IO.Path]::GetFileName($name) -or $name -in @('.','..')){return ''}
    $path=[IO.Path]::GetFullPath((Join-Path $store $name))
    if(Test-IndependentExistingProject $path){return $path}
    return ''
}
function Resolve-IndependentInstallContext([string]$Project,[string]$Sid) {
    if(-not $Sid){$Sid=Get-IndependentInteractiveSid}
    if($Sid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$'){throw '交互账户标识无效。'}
    if($Project){
        if(-not (Test-IndependentExistingProject $Project)){throw '指定项目缺少配置或 index.db，未修改安装。'}
        $Project=[IO.Path]::GetFullPath($Project).TrimEnd('\')
    }else{
        $profile=[string](Get-ItemProperty -LiteralPath ('HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\'+$Sid) -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath
        $profile=[Environment]::ExpandEnvironmentVariables($profile)
        try{$Project=Find-IndependentSetupProject (Join-Path $profile 'AppData\Local\Wanxiang\EPBTest\user-state.json') (Join-Path $env:ProgramData 'MTTFTest\Config\TestConfig.xml')}
        catch{Write-Warning ('原项目记录无法使用，将等待首次项目设置：'+$_.Exception.Message);$Project=''}
    }
    return [pscustomobject]@{ProjectDirectory=$Project;InteractiveUserSid=$Sid}
}
function Write-IndependentSetupState([string]$Root,$State) {
    $path=Join-Path $Root 'install-setup.json';$temporary=$path+'.tmp'
    [IO.File]::WriteAllText($temporary,($State|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
    if([IO.File]::Exists($path)){[IO.File]::Replace($temporary,$path,$path+'.previous')}else{[IO.File]::Move($temporary,$path)}
}
function Invoke-IndependentSetupStages([string]$Root,$State,[scriptblock]$Step) {
    if(-not (Test-IndependentExistingProject ([string]$State.projectDirectory))){throw '项目尚未准备完成，未注册恢复。'}
    $State.stage='Binding';Write-IndependentSetupState $Root $State
    foreach($phase in @('Recovery','SessionHost','Shortcut','Verify','RemoveSetupShortcut')){& $Step $phase}
    $State.stage='Ready';Write-IndependentSetupState $Root $State
}
function Initialize-IndependentSetupConfig([string]$Root) {
    $target=Join-Path $env:ProgramData 'MTTFTest\Config'
    [IO.Directory]::CreateDirectory($target)|Out-Null
    foreach($name in @('AIConfig.xml','AlarmConfig.xml','AOConfig.xml','DOConfig.xml','PowerSupplyConfig.xml','TestConfig.xml','UnattendedAlarmConfig.xml','UIConfig.xml')){
        $path=Join-Path $target $name
        if(-not [IO.File]::Exists($path)){[IO.File]::Copy((Join-Path $Root ('Current\Config\'+$name)),$path,$false)}
    }
    # Prevent the legacy first-run installer from configuring a second architecture.
    [IO.File]::WriteAllText((Join-Path $Root 'Current\MTTFTest.FirstRun.configured'),'Independent setup; see install-setup.json')
}
function Set-IndependentDesktopAccess([string]$Root) {
    # Explorer reads the executable/icon before UAC. Do not expose recovery state.
    $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    $current=Join-Path $rootPath 'Current'
    $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    foreach($path in @($rootPath,$current)){
        $item=Get-Item -LiteralPath $path -ErrorAction Stop
        if($item -isnot [IO.DirectoryInfo] -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '桌面访问目录身份无效。'}
        $acl=Get-Acl -LiteralPath $path
        if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw '桌面访问目录所有者不可信。'}
    }
    foreach($path in @($rootPath,$current)){
        $acl=Get-Acl -LiteralPath $path
        $inherit=if($path -eq $current){[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'}else{[Security.AccessControl.InheritanceFlags]::None}
        $rule=[Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),
            [Security.AccessControl.FileSystemRights]::ReadAndExecute,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
        Set-Acl -LiteralPath $path -AclObject $acl
    }
}
function Set-IndependentSetupShortcut([string]$Root,[string]$Version,[bool]$Remove=$false) {
    if(-not $Remove){Set-IndependentDesktopAccess $Root}
    $path=Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) ('MT EPB V'+$Version+' 项目设置.lnk')
    $shell=New-Object -ComObject WScript.Shell
    try{
        if($Remove){
            if([IO.File]::Exists($path)){
                $link=$shell.CreateShortcut($path)
                if(-not [string]::Equals($link.TargetPath,(Join-Path $Root 'Current\MTTFTest.exe'),[StringComparison]::OrdinalIgnoreCase)){throw '项目设置快捷方式属于其他安装。'}
                [IO.File]::Delete($path)
            }
        }else{
            if([IO.File]::Exists($path)){
                $link=$shell.CreateShortcut($path)
                if(-not [string]::Equals($link.TargetPath,(Join-Path $Root 'Current\MTTFTest.exe'),[StringComparison]::OrdinalIgnoreCase) -or $link.Arguments -ne ''){throw '项目设置快捷方式属于其他安装，拒绝覆盖。'}
                return
            }
            $link=$shell.CreateShortcut($path);$link.TargetPath=Join-Path $Root 'Current\MTTFTest.exe'
            $link.WorkingDirectory=Join-Path $Root 'Current';$link.Description='程序已安装；首次创建或打开项目后完成恢复绑定。';$link.IconLocation=(Join-Path $Root 'Current\MTTFTest.exe')+',0';$link.Save()
        }
    }finally{if($link){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)};[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)}
}
