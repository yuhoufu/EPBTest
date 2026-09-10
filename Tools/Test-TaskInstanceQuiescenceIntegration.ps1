param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-InstalledTaskStopped'
}, $true)
if ($null -eq $definition) { throw 'Wait function missing' }
. ([scriptblock]::Create($definition.Extent.Text))
$backupDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Backup-InstalledRuntimeTasks'
}, $true)
if ($null -eq $backupDefinition) { throw 'Backup function missing' }
. ([scriptblock]::Create($backupDefinition.Extent.Text))
$entryValidator = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-DeploymentTaskBackupEntries' }, $true)
if ($null -eq $entryValidator) { throw 'Entry validator missing' }
. ([scriptblock]::Create($entryValidator.Extent.Text))
$pathDefinition = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-DeploymentEvidenceDirectory' }, $true)
if ($null -eq $pathDefinition) { throw 'Path guard missing' }
. ([scriptblock]::Create($pathDefinition.Extent.Text))
# Non-elevated local fixture: ACL creation is verified separately on JXCQ.
function Initialize-ProtectedDeploymentDirectory {
    param($Directory)
    Assert-DeploymentEvidenceDirectory $Directory
    [void](New-Item -ItemType Directory -Path $Directory -Force)
    Assert-DeploymentEvidenceDirectory $Directory
}
$securityDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-InstalledTaskSecurityDescriptor'
}, $true)
if ($null -eq $securityDefinition) { throw 'Security function missing' }
. ([scriptblock]::Create($securityDefinition.Extent.Text))
$runningDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-InstalledTaskRunningState'
}, $true)
if ($null -eq $runningDefinition) { throw 'Running-state function missing' }
. ([scriptblock]::Create($runningDefinition.Extent.Text))
$runningDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-InstalledTaskRunningState'
}, $true)
if ($null -eq $runningDefinition) { throw 'Running-state function missing' }
. ([scriptblock]::Create($runningDefinition.Extent.Text))
$name = 'EPB-Validation-TaskExit-' + [Guid]::NewGuid().ToString('N')
$description = 'Isolated EPB task-exit validation ' + $name
$executable = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -Command "Start-Sleep -Seconds 60"'
$created = $false
try {
    $action = New-ScheduledTaskAction -Execute $executable -Argument $arguments
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $name -TaskPath '\' -Action $action -Principal $principal -Settings $settings -Description $description -ErrorAction Stop | Out-Null
    $created = $true
    Write-Output "Created isolated task: $name"
    $before = [string](Export-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop)
    $securityBefore = Get-InstalledTaskSecurityDescriptor $name
    $autoStartTaskName = $name
    $taskName = $name + '-AbsentAgent'
    $healthTaskName = $name + '-AbsentHealth'
    $originalProgramData = $env:ProgramData
    $evidence = Join-Path ([IO.Path]::GetTempPath()) ('epb-task-export-' + [Guid]::NewGuid().ToString('N'))
    try {
        $env:ProgramData = $evidence
        Backup-InstalledRuntimeTasks 'C:\FixtureOnly\MTTFTest' @(Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop)
    } finally { $env:ProgramData = $originalProgramData }
    $files = @(Get-ChildItem -LiteralPath (Join-Path $evidence 'MTTFTestDeploymentEvidence\TaskBackups') -File)
    if ($files.Count -ne 1) { throw 'Expected exactly one published backup' }
    $saved = Get-Content -LiteralPath $files[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $after = [string](Export-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop)
    if ($saved.schema -ne 4 -or $saved.tasks[0].securityDescriptor -cne $securityBefore -or
        (Get-InstalledTaskSecurityDescriptor $name) -cne $securityBefore -or
        $saved.tasks.Count -ne 3 -or $saved.tasks[0].name -cne $name -or
        -not $saved.tasks[0].existed -or $saved.tasks[0].wasRunning -or
        $saved.tasks[0].xml -cne $before -or
        $saved.tasks[1].existed -or $saved.tasks[2].existed -or $after -cne $before -or
        -not (Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop).Settings.Enabled) {
        throw 'Real task backup differs or mutated the original task'
    }
    Write-Output "PASS real task XML backup unchanged; Evidence=$evidence"
    Start-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $task = Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop
        if ($task.State -eq 'Running') { break }
        if ($deadline.Elapsed.TotalSeconds -ge 15) { throw 'Fixture task did not start' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    Disable-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop | Out-Null
    $caught = $null
    try { Wait-InstalledTaskStopped $name 0 } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Message -notlike 'InstalledTaskStopTimeout:*') {
        throw "Disabled running task was not rejected: $caught"
    }
    Write-Output 'PASS real disabled task retains a running instance and is rejected'
    Stop-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop
    Wait-InstalledTaskStopped $name 10000
    Write-Output 'PASS real stopped task has zero instances and is accepted'
} finally {
    if ($created) {
        $task = Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop
        if ($task.Description -ne $description -or @($task.Actions).Count -ne 1 -or
            $task.Actions[0].Execute -ine $executable -or $task.Actions[0].Arguments -cne $arguments) {
            throw "Fixture identity changed; refusing cleanup: $name"
        }
        Disable-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop | Out-Null
        Stop-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop
        Wait-InstalledTaskStopped $name 10000
        Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false -ErrorAction Stop
        $remaining = @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -eq $name -and $_.TaskPath -eq '\' })
        if ($remaining.Count -ne 0) { throw "Fixture cleanup incomplete: $name" }
        Write-Output "Removed isolated task: $name"
    }
}
