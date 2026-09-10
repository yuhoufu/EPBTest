param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Restore-FailedInstallTransaction'
}, $true)
if ($null -eq $definition) { throw 'Install rollback function missing' }
. ([scriptblock]::Create($definition.Extent.Text))

$serviceName = 'FixtureSupervisor'
$taskBackup = [pscustomobject]@{ TransactionId='tasks' }
$current = [pscustomobject]@{ TransactionId='current' }

foreach ($scenario in @('success-running', 'success-absent', 'task-failure', 'current-failure')) {
    & {
        param($scenario, $taskBackup, $current)
        $script:operations = @()
        function Stop-Supervisor { $script:operations += 'stop-service' }
        function Restore-InstalledRuntimeTasks {
            param($Reference, $Root, [switch]$DeferRunningStart)
            $script:operations += 'restore-tasks'
            if (-not $DeferRunningStart) { throw 'Task start was not deferred' }
            if ($scenario -eq 'task-failure') { throw 'FixtureTaskFailure' }
            return @('FixtureAuto')
        }
        function Restore-CurrentSlotTransaction {
            param($Reference, $Root)
            $script:operations += 'restore-current'
            if ($scenario -eq 'current-failure') { throw 'FixtureCurrentFailure' }
        }
        function Start-Service {
            [CmdletBinding()]param($Name)
            $script:operations += 'start-service'
        }
        function Get-Service {
            [CmdletBinding()]param($Name)
            $script:operations += 'query-service'
            [pscustomobject]@{ Name=$Name }
        }
        function sc.exe {
            $script:operations += 'delete-service'
            $global:LASTEXITCODE = 0
        }
        function Start-RestoredRuntimeTasks {
            param($Names)
            if (($Names -join ',') -cne 'FixtureAuto') { throw 'Wrong deferred task identity' }
            $script:operations += 'start-tasks'
        }

        $caught = $null
        try {
            Restore-FailedInstallTransaction 'C:\FixtureOnly\MTTFTest' `
                $taskBackup $current ($scenario -ne 'success-absent') ($scenario -eq 'success-running')
        }
        catch { $caught = $_ }
        if ($scenario -like '*-failure') {
            if ($null -eq $caught -or $caught.Exception.Message -notlike 'InstallRollbackFailed:*') {
                throw "Rollback failure not surfaced: $scenario $caught"
            }
            if (($script:operations -join ',') -notlike 'stop-service,restore-tasks,*restore-current*') {
                throw "Independent rollback stages were not attempted: $scenario"
            }
            if ($script:operations -contains 'start-tasks' -or
                $script:operations -contains 'start-service' -or
                $script:operations -contains 'delete-service') {
                throw "Runtime restarted after incomplete rollback: $scenario"
            }
        }
        else {
            if ($null -ne $caught) { throw $caught }
            $expected = if ($scenario -eq 'success-running') {
                'stop-service,restore-tasks,restore-current,start-service,start-tasks'
            } else {
                'stop-service,restore-tasks,restore-current,query-service,delete-service,start-tasks'
            }
            if (($script:operations -join ',') -cne $expected) {
                throw "Wrong successful rollback order: $scenario $($script:operations -join ',')"
            }
        }
        Write-Output "PASS install rollback: $scenario"
    } $scenario $taskBackup $current
}
Write-Output 'PASS install rollback 4/4'
