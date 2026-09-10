param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-InstalledRuntimeTasks'
}, $true)
if ($null -eq $definition) { throw 'Task stop function missing' }
foreach ($scenario in @('query-error', 'none', 'nested', 'present', 'disable-error', 'stop-error', 'wait-error', 'all', 'main-error', 'idle-error', 'backup-error', 'journal-error')) {
    & {
        param($definition, $scenario)
        . ([scriptblock]::Create($definition))
        $autoStartTaskName = 'FixtureAuto'
        $taskName = 'FixtureAgent'
        $healthTaskName = 'FixtureHealth'
        $script:operations = @()
        $script:targets = @()
        $script:deploymentTaskBackup = [pscustomobject]@{ TransactionId = 'stale' }
        $script:deploymentTaskPreparation = 'stale-preparation.json'
        $newReference = [pscustomobject]@{ TransactionId = 'current' }
        function Write-DeploymentTaskPreparation {
            param($Reference, $Root, $ServiceExistedBefore, $ServiceWasRunning, $Operation, $AclSnapshot, $ServiceRegistrySnapshot)
            $script:operations += 'journal'
            if ($scenario -eq 'journal-error') { throw 'FixtureJournal' }
            return 'fixture-preparation.json'
        }
        function Backup-InstalledRuntimeTasks {
            param($Root, $Tasks)
            $script:operations += 'backup'
            if ($scenario -eq 'backup-error') { throw 'FixtureBackup' }
            return $newReference
        }
        function Get-ScheduledTask {
            [CmdletBinding()]param()
            $script:operations += 'query'
            if ($scenario -eq 'query-error') { Write-Error 'FixtureQuery'; return }
            if ($scenario -ne 'none') {
                [pscustomobject]@{ TaskName = 'FixtureAuto'; TaskPath = $(if ($scenario -eq 'nested') { '\Other\' } else { '\' }) }
            }
            if ($scenario -eq 'all') {
                [pscustomobject]@{ TaskName = 'FixtureAgent'; TaskPath = '\' }
                [pscustomobject]@{ TaskName = 'FixtureHealth'; TaskPath = '\' }
            }
        }
        function Disable-ScheduledTask {
            [CmdletBinding()]param($TaskName, $TaskPath)
            if ($TaskName -notin @('FixtureAuto', 'FixtureAgent', 'FixtureHealth') -or $TaskPath -ne '\') { throw 'Wrong target' }
            $script:operations += 'disable'
            $script:targets += "disable:$TaskName"
            if ($scenario -eq 'disable-error') { Write-Error 'FixtureDisable' }
        }
        function Stop-ScheduledTask {
            [CmdletBinding()]param($TaskName, $TaskPath)
            if ($TaskName -notin @('FixtureAuto', 'FixtureAgent', 'FixtureHealth') -or $TaskPath -ne '\') { throw 'Wrong target' }
            $script:operations += 'stop'
            $script:targets += "stop:$TaskName"
            if ($scenario -eq 'stop-error') { Write-Error 'FixtureStop' }
        }
        function Stop-InstalledSessionAgent { param($Root) $script:operations += 'agent' }
        function Assert-InstalledMainStopped {
            param($Root)
            $script:operations += 'main'
            if ($scenario -eq 'main-error') { throw 'FixtureMain' }
        }
        function Assert-RuntimeIdle {
            param($Root)
            $script:operations += 'idle'
            if ($scenario -eq 'idle-error') { throw 'FixtureIdle' }
        }
        function Wait-InstalledTaskStopped {
            param($Name)
            if ($Name -notin @('FixtureAuto', 'FixtureAgent', 'FixtureHealth')) { throw 'Wrong wait target' }
            $script:operations += 'wait'
            $script:targets += "wait:$Name"
            if ($scenario -eq 'wait-error') { throw 'FixtureWait' }
        }
        $caught = $null
        try { Stop-InstalledRuntimeTasks 'C:\FixtureOnly\MTTFTest' } catch { $caught = $_ }
        if (($null -ne $caught) -ne ($scenario -like '*-error')) { throw "Wrong outcome: $scenario" }
        if ($scenario -in @('query-error', 'backup-error')) {
            if ($null -ne $script:deploymentTaskBackup) { throw 'Failed preparation retained stale backup reference' }
        } elseif (-not [object]::ReferenceEquals($script:deploymentTaskBackup, $newReference)) {
            throw 'Successful publication reference lost before rollback could inspect it'
        }
        if ($scenario -in @('query-error', 'backup-error', 'journal-error')) {
            if ($null -ne $script:deploymentTaskPreparation) { throw 'Failed preparation retained a ready journal reference' }
        } elseif ($script:deploymentTaskPreparation -cne 'fixture-preparation.json') {
            throw 'Successful preparation reference lost'
        }
        $expected = switch ($scenario) {
            'query-error' { 'query' }
            'backup-error' { 'query,backup' }
            'journal-error' { 'query,backup,journal' }
            'disable-error' { 'query,disable' }
            'stop-error' { 'query,disable,stop' }
            'wait-error' { 'query,disable,stop,wait' }
            'present' { 'query,disable,stop,wait,agent,main,idle' }
            'main-error' { 'query,disable,stop,wait,agent,main' }
            'idle-error' { 'query,disable,stop,wait,agent,main,idle' }
            'all' { 'query,disable,disable,disable,stop,stop,stop,wait,wait,wait,agent,main,idle' }
            default { 'query,agent,main,idle' }
        }
        if ($scenario -notin @('query-error', 'backup-error', 'journal-error')) { $expected = $expected -replace '^query,', 'query,backup,journal,' }
        if (($script:operations -join ',') -ne $expected) { throw "Wrong sequence: $scenario" }
        if ($scenario -eq 'all' -and ($script:targets -join ',') -ne
            'disable:FixtureAuto,disable:FixtureAgent,disable:FixtureHealth,stop:FixtureAuto,stop:FixtureAgent,stop:FixtureHealth,wait:FixtureAuto,wait:FixtureAgent,wait:FixtureHealth') {
            throw 'All target identities must be processed exactly once in each phase'
        }
        Write-Output "PASS runtime task stop: $scenario"
    } $definition.Extent.Text $scenario
}
