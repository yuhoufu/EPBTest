param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'),
    [string]$ValidationRoot = 'D:\EPB_Validation')
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @(
        'Assert-DeploymentEvidenceDirectory', 'Initialize-ProtectedDeploymentDirectory',
        'Assert-DeploymentTaskBackupEntries', 'Get-NormalizedScheduledTaskXml',
        'Read-DeploymentTaskBackup', 'Get-InstalledTaskSecurityDescriptor',
        'Set-InstalledTaskSecurityDescriptor', 'Get-InstalledTaskRunningState',
        'Backup-InstalledRuntimeTasks', 'Restore-InstalledRuntimeTasks',
        'Start-RestoredRuntimeTasks')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "Missing function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Elevated validation required'
}
$parent = Get-Item -LiteralPath $ValidationRoot -Force -ErrorAction Stop
if (-not $parent.PSIsContainer -or ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Unsafe validation root'
}
$id = [Guid]::NewGuid().ToString('N')
$fixture = Join-Path $ValidationRoot ('task-restore-' + $id)
[void](New-Item -ItemType Directory -Path $fixture)
$oldProgramData = $env:ProgramData
$env:ProgramData = $fixture
$autoStartTaskName = 'EPB-Validation-Restore-A-' + $id
$taskName = 'EPB-Validation-Restore-B-' + $id
$healthTaskName = 'EPB-Validation-Restore-C-' + $id
$names = @($autoStartTaskName, $taskName, $healthTaskName)
$installRoot = Join-Path $fixture 'Install'
[void](New-Item -ItemType Directory -Path $installRoot)
try {
    foreach ($name in $names) {
        if (Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue) {
            throw "Fixture task collision: $name"
        }
    }
    $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromMinutes(2))
    $principalSystem = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $autoStartTaskName -TaskPath '\' `
        -Action (New-ScheduledTaskAction -Execute $powerShell -Argument '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 60"') `
        -Principal $principalSystem -Settings $settings | Out-Null
    Register-ScheduledTask -TaskName $healthTaskName -TaskPath '\' `
        -Action (New-ScheduledTaskAction -Execute $powerShell -Argument '-NoProfile -NonInteractive -Command "exit 0"') `
        -Principal $principalSystem -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $autoStartTaskName -TaskPath '\'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Get-InstalledTaskRunningState $autoStartTaskName)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Fixture task did not enter running state' }
        Start-Sleep -Milliseconds 100
    }

    $reference = Backup-InstalledRuntimeTasks $installRoot @(Get-ScheduledTask -ErrorAction Stop)
    Stop-ScheduledTask -TaskName $autoStartTaskName -TaskPath '\' -ErrorAction SilentlyContinue
    foreach ($name in @($autoStartTaskName, $healthTaskName)) {
        Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false
    }
    Register-ScheduledTask -TaskName $taskName -TaskPath '\' `
        -Action (New-ScheduledTaskAction -Execute $powerShell -Argument '-NoProfile -NonInteractive -Command "exit 9"') `
        -Principal $principalSystem -Settings $settings | Out-Null

    $deferred = @(Restore-InstalledRuntimeTasks $reference $installRoot -DeferRunningStart)
    if (($deferred -join ',') -cne $autoStartTaskName) {
        throw "Wrong deferred running identity: Count=$($deferred.Count); Value=$($deferred -join ',')"
    }
    Start-RestoredRuntimeTasks $deferred
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Get-InstalledTaskRunningState $autoStartTaskName)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Restored task did not resume running' }
        Start-Sleep -Milliseconds 100
    }
    if (Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue) {
        throw 'Originally absent task was not removed'
    }
    if (-not (Get-ScheduledTask -TaskName $healthTaskName -TaskPath '\' -ErrorAction Stop)) {
        throw 'Originally stopped task was not restored'
    }
    if (Get-InstalledTaskRunningState $healthTaskName) { throw 'Originally stopped task was started' }
    [pscustomobject]@{
        Computer=$env:COMPUTERNAME; EvidenceRoot=$fixture; BackupPath=$reference.Path
        ExactXmlAndSecurityReadback=$true; PriorRunningStateRestored=$true
        PriorAbsenceRestored=$true; ProductionTasksModified=$false; RetainedForAudit=$true
    } | ConvertTo-Json -Depth 4
    Write-Output 'PASS runtime task restore integration 1/1'
}
finally {
    foreach ($name in $names) {
        Stop-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false -ErrorAction SilentlyContinue
    }
    $env:ProgramData = $oldProgramData
}
