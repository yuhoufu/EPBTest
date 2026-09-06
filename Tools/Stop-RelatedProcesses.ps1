[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Stop', 'Restore', 'Status')][string]$Mode = 'Stop',
    [string]$InstallRoot = '',
    [ValidateRange(1, 180)][int]$WaitSeconds = 45,
    [switch]$PhysicalIsolationConfirmed
)
$ErrorActionPreference = 'Stop'

function Get-RelatedProcesses([string]$Root) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    foreach ($name in @('MTTFTest.exe','MTTFTest.Watchdog.exe','MTTFTest.SessionAgent.exe',
            'MTTFTest.SafetyAgent.exe','MTTFTest.EngineHost.exe')) {
        foreach ($item in @(Get-CimInstance Win32_Process -Filter "Name='$name'" -ErrorAction Stop)) {
            if ([string]::IsNullOrWhiteSpace([string]$item.ExecutablePath)) {
                throw "IdentityBlocked: 无法读取 $name PID=$($item.ProcessId) 的路径。"
            }
            $path = [IO.Path]::GetFullPath([string]$item.ExecutablePath)
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $proc = Get-Process -Id ([int]$item.ProcessId) -ErrorAction SilentlyContinue
            if ($null -eq $proc) { continue }
            try {
                [void]$proc.Handle
                if ($proc.Path -ne $path) { throw 'IdentityBlocked: PID 已复用。' }
                [pscustomobject]@{ Id=$proc.Id; StartTicks=$proc.StartTime.ToUniversalTime().Ticks;
                    Path=$path; Name=$name; Arguments=[string]$item.CommandLine }
            }
            finally { $proc.Dispose() }
        }
    }
}

function Stop-ExactProcess($Identity, [switch]$Graceful) {
    $proc = Get-Process -Id ([int]$Identity.Id) -ErrorAction SilentlyContinue
    if ($null -eq $proc) { return }
    try {
        [void]$proc.Handle
        if ($proc.Path -ne $Identity.Path -or
            $proc.StartTime.ToUniversalTime().Ticks -ne [long]$Identity.StartTicks) {
            throw "IdentityBlocked: PID=$($Identity.Id) 身份已变化。"
        }
        if ($Graceful) { [void]$proc.CloseMainWindow(); return }
        Stop-Process -InputObject $proc -Force -Confirm:$false -ErrorAction Stop
        if (-not $proc.WaitForExit(10000)) { throw "ResidualProcess: PID=$($Identity.Id) 未退出。" }
    }
    finally { $proc.Dispose() }
}

function Test-StopReceipt($Receipt, $Main, [long]$RequestedTicks) {
    return ($null -ne $Receipt -and [int]$Receipt.SchemaVersion -eq 2 -and
        [int]$Receipt.MainProcessId -eq [int]$Main.Id -and
        [long]$Receipt.MainProcessStartUtcTicks -eq [long]$Main.StartTicks -and
        [long]$Receipt.UpdatedUtcTicks -ge $RequestedTicks -and
        [long]$Receipt.RequestedUtcTicks -ge $RequestedTicks -and
        -not [string]::IsNullOrWhiteSpace([string]$Receipt.SessionId) -and
        [int]$Receipt.State -eq 3 -and [int]$Receipt.RelaunchDisposition -eq 1 -and
        $Receipt.MotorsOff -eq $true -and $Receipt.PowerOff -eq $true -and
        $Receipt.PressureSafe -eq $true -and $Receipt.PersistenceDrained -eq $true -and
        $Receipt.LogicalQuiescent -eq $true)
}

function Write-MaintenanceJson([string]$Path, $Value) {
    $temp = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($true)))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp, $Path, [NullString]::Value) }
    else { [IO.File]::Move($temp, $Path) }
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $base = [Environment]::GetFolderPath('ProgramFilesX86')
    if ([string]::IsNullOrWhiteSpace($base)) { $base = [Environment]::GetFolderPath('ProgramFiles') }
    $InstallRoot = Join-Path $base 'MTTFTest'
}
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ([IO.Path]::GetFileName($root) -ne 'MTTFTest') { throw 'InstallRoot 必须明确指向 MTTFTest 安装目录。' }
$stateRoot = Join-Path $env:ProgramData 'MTTFTest'
$inhibit = Join-Path $stateRoot 'maintenance-inhibit.json'
$serviceName = 'MTTFTestSupervisor'
$taskNames = @('MTTFTestSessionAgent', 'MTTFTestAutoStart')
if ($Mode -eq 'Status') {
    [pscustomobject]@{ Inhibited=(Test-Path -LiteralPath $inhibit); Processes=@(Get-RelatedProcesses $root) } | ConvertTo-Json -Depth 5
    return
}
if (-not $PSCmdlet.ShouldProcess($root, "维护操作 $Mode；保留生产数据")) { return }
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请以管理员身份运行。' }
[void](New-Item -ItemType Directory -Path $stateRoot -Force)
$mutex = New-Object Threading.Mutex($false, 'Global\MTTFTest.Maintenance.V217')
$owned = $false
try {
    $owned = $mutex.WaitOne(0)
    if (-not $owned) { throw 'RestartSourceBlocked: 另一个维护事务正在运行。' }
    $state = $null
    if (Test-Path -LiteralPath $inhibit) {
        $state = [IO.File]::ReadAllText($inhibit, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ([string]$state.InstallRoot -ne $root) { throw 'IdentityBlocked: 维护事务属于另一安装目录。' }
    }
    if ($Mode -eq 'Restore') {
        if ($null -eq $state) { Write-Host '没有清场事务，无需恢复。'; return }
        if (-not [bool]$state.CleanupCompleted) { throw 'SafetyBlocked: 清场未完成，禁止恢复后台拉起。' }
        $current = Join-Path $root 'Current'
        $verified = (& (Join-Path $current 'Deployment\Verify-Release.ps1') -ReleaseDirectory $current -AllowInstalledRuntimeState) | ConvertFrom-Json
        if (-not $verified.verified -or $verified.releaseStatus -ne 'FORMAL_RELEASE') { throw 'IdentityBlocked: 正式包校验失败。' }
        # Keep all old session records retired. Starting background services never reuses an active permit.
        $sessions = Join-Path $stateRoot 'Supervisor\sessions'
        if (Test-Path -LiteralPath $sessions) {
            $archive = Join-Path $stateRoot ('MaintenanceArchive\' + $state.TransactionId)
            [void](New-Item -ItemType Directory -Path $archive -Force)
            foreach ($file in @(Get-ChildItem -LiteralPath $sessions -File -Filter '*.json')) {
                Move-Item -LiteralPath $file.FullName -Destination (Join-Path $archive $file.Name) -Force
            }
        }
        if ($state.ServiceExisted) {
            $startMode = if ($state.ServiceStartMode -eq 'Auto') { 'Automatic' } elseif ($state.ServiceStartMode -eq 'Disabled') { 'Disabled' } else { 'Manual' }
            Set-Service -Name $serviceName -StartupType $startMode
            if ($startMode -ne 'Disabled') { Start-Service -Name $serviceName }
        }
        foreach ($task in @($state.Tasks)) {
            if ($task.Enabled) { Enable-ScheduledTask -TaskName $task.Name | Out-Null }
        }
        # SessionAgent has no authority to begin a mechanical test; do not run AutoStart here.
        if (@($state.Tasks | Where-Object { $_.Name -eq 'MTTFTestSessionAgent' -and $_.Enabled }).Count -gt 0) {
            Start-ScheduledTask -TaskName 'MTTFTestSessionAgent'
        }
        $state.RestoredUtc = [DateTime]::UtcNow.ToString('O')
        Write-MaintenanceJson (Join-Path $stateRoot 'last-maintenance-result.json') $state
        Remove-Item -LiteralPath $inhibit -Force
        Write-Host '后台服务已恢复，主程序及机械试验未自动启动。'
        return
    }
    if ($null -eq $state) {
        $processes = @(Get-RelatedProcesses $root)
        $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
        $tasks = @(foreach ($name in $taskNames) {
            $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            if ($null -ne $task) { @{ Name=$name; Enabled=([string]$task.State -ne 'Disabled') } }
        })
        $state = [pscustomobject]@{ TransactionId=[Guid]::NewGuid().ToString('N'); InstallRoot=$root;
            RequestedTicks=[DateTime]::UtcNow.Ticks; Processes=$processes; Tasks=$tasks;
            ServiceExisted=($null -ne $service); ServiceStartMode=[string]$service.StartMode;
            CleanupCompleted=$false; HardwareEvidence='NotProven'; RestoredUtc=''; Error='' }
        Write-MaintenanceJson $inhibit $state
    }
    foreach ($task in @($state.Tasks)) {
        Disable-ScheduledTask -TaskName $task.Name | Out-Null
        Stop-ScheduledTask -TaskName $task.Name
    }
    $mains = @($state.Processes | Where-Object { $_.Name -eq 'MTTFTest.exe' })
    foreach ($main in $mains) { Stop-ExactProcess $main -Graceful }
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    $safe = $false
    do {
        $safe = $true
        foreach ($main in $mains) {
            $proof = $false
            $control = Join-Path $env:LOCALAPPDATA 'MTTFTest\WatchdogControlV2'
            if (Test-Path -LiteralPath $control) {
                foreach ($file in @(Get-ChildItem -LiteralPath $control -Filter '*.application-exit.json' -File)) {
                    try {
                        $receipt = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8) | ConvertFrom-Json
                        if (Test-StopReceipt $receipt $main ([long]$state.RequestedTicks)) { $proof = $true; break }
                    } catch { }
                }
            }
            if (-not $proof) { $safe = $false }
        }
        $active = @(Get-RelatedProcesses $root)
        if (@($active | Where-Object { $_.Name -in @('MTTFTest.SafetyAgent.exe','MTTFTest.EngineHost.exe') }).Count -gt 0) { $safe = $false }
        if ($mains.Count -eq 0 -and @($active | Where-Object { $_.Arguments -match '--session-host|--parent-pid' }).Count -gt 0) { $safe = $false }
        if ($safe -or $PhysicalIsolationConfirmed) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $safe -and -not $PhysicalIsolationConfirmed) {
        throw 'SafetyBlocked: 缺少本次安全退出回执或安全工作进程仍在运行；已禁止主程序重新拉起，保留安全接管。确认物理断能后才可使用 -PhysicalIsolationConfirmed。'
    }
    $state.HardwareEvidence = if ($PhysicalIsolationConfirmed) { 'OperatorPhysicalIsolationConfirmed' } elseif ($mains.Count -eq 0) { 'NoActiveHardwareOwnerObserved_NotPhysicalProof' } else { 'ExactGracefulExitReceipts' }
    if ($state.ServiceExisted) {
        Set-Service -Name $serviceName -StartupType Disabled
        Stop-Service -Name $serviceName -Force
        (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    foreach ($name in @('MTTFTest.exe','MTTFTest.SessionAgent.exe','MTTFTest.Watchdog.exe','MTTFTest.EngineHost.exe','MTTFTest.SafetyAgent.exe')) {
        foreach ($proc in @(Get-RelatedProcesses $root | Where-Object { $_.Name -eq $name })) { Stop-ExactProcess $proc }
    }
    Start-Sleep -Seconds 2
    if (@(Get-RelatedProcesses $root).Count -ne 0) { throw 'ResidualProcess: 清场后仍有相关进程。' }
    $state.CleanupCompleted = $true
    $state.Error = ''
    Write-MaintenanceJson $inhibit $state
    Write-MaintenanceJson (Join-Path $stateRoot 'last-maintenance-result.json') $state
    Write-Host "清场完成：$($state.TransactionId)。生产数据保留；后台保持禁用，使用恢复后台服务入口恢复。"
}
catch {
    if ($null -ne $state) { $state.Error = $_.Exception.Message; Write-MaintenanceJson $inhibit $state }
    Write-Error $_ -ErrorAction Continue
    if ($_.Exception.Message -like '*SafetyBlocked:*') { exit 10 }
    if ($_.Exception.Message -like '*IdentityBlocked:*') { exit 20 }
    if ($_.Exception.Message -like '*ResidualProcess:*') { exit 40 }
    exit 30
}
finally { if ($owned) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
