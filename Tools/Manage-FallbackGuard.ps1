[CmdletBinding()]
param(
    [ValidateSet('Enable','Disable','Status','Install','Uninstall','Run')][string]$Mode = 'Status',
    [Parameter(Mandatory=$true)][string]$ProjectDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{32}$')][string]$SessionId,
    [string]$ExecutablePath = '',
    [switch]$BenchActive
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath($ProjectDirectory)
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $PSScriptRoot '..\FallbackGuard\MTTFTest.FallbackGuard.exe' }
$executable = [IO.Path]::GetFullPath($ExecutablePath)
if (-not [IO.File]::Exists($executable)) { throw "FallbackGuard 不存在：$executable" }
$taskName = "MTTFTest-FallbackGuard-$SessionId"
function Invoke-Guard([string]$Action) {
    $arguments = @($Action, '--project-directory', $project, '--session-id', $SessionId)
    if ($Action -eq '--enable' -and $BenchActive) { $arguments += '--active' }
    & $executable @arguments
    if ($LASTEXITCODE -ne 0) { throw "FallbackGuard 返回 $LASTEXITCODE" }
}
switch ($Mode) {
    'Enable' { Invoke-Guard '--enable' }
    'Disable' { Invoke-Guard '--disable' }
    'Status' { Invoke-Guard '--status' }
    'Run' {
        # The task maintains only this external process. Disabled settings make
        # it exit normally; no principal recovery process is launched here.
        $arguments = '--run --project-directory "{0}" --session-id {1}' -f $project, $SessionId
        $child = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $null = $child.Handle
        $child.WaitForExit()
        $code = $child.ExitCode
        $child.Dispose()
        exit $code
    }
    'Install' {
        if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw '同名任务已存在；请先核对归属后卸载。' }
        Invoke-Guard '--enable'
        $account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $script = [IO.Path]::GetFullPath($PSCommandPath)
        $arguments = '-NoProfile -WindowStyle Hidden -File "{0}" -Mode Run -ProjectDirectory "{1}" -SessionId {2} -ExecutablePath "{3}"' -f $script,$project,$SessionId,$executable
        $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument $arguments
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $account
        $principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description "EPB 独立兜底；项目=$project；会话=$SessionId" | Out-Null
        Start-ScheduledTask -TaskName $taskName
    }
    'Uninstall' {
        Invoke-Guard '--disable'
        $ledgerPath = Join-Path $project "fallback-$SessionId.json"
        if ([IO.File]::Exists($ledgerPath)) {
            if (([IO.FileInfo]$ledgerPath).Length -gt 65536) { throw '账本超限，需先调和。' }
            $ledger = [IO.File]::ReadAllText($ledgerPath) | ConvertFrom-Json
            if ($ledger.Owner -eq 'Fallback' -or $ledger.Phase -eq 'Yielded') { throw '存在未交还或未完成事务；已禁用新增接管，待调和后卸载任务。' }
        }
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($task) {
            if ($task.Description -ne "EPB 独立兜底；项目=$project；会话=$SessionId") { throw '任务归属不匹配，拒绝删除。' }
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        }
        Write-Output '独立任务已卸载；保留账本、停止事实及原恢复组件。'
    }
}
