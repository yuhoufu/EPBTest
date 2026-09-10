param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-InstalledTaskAction'
}, $true)
if ($null -eq $definition) { throw 'Task action validator missing' }
. ([scriptblock]::Create($definition.Extent.Text))
$expected = 'C:\FixtureOnly\MTTFTest\Current\MTTFTest.Watchdog.exe'
foreach ($scenario in @('valid', 'case', 'old-slot', 'relative', 'missing-argument', 'extra-argument', 'multiple', 'none', 'disabled')) {
    $execute = $expected
    $arguments = '--launch-main'
    switch ($scenario) {
        'case' { $execute = $expected.ToUpperInvariant() }
        'old-slot' { $execute = $expected.Replace('\Current\', '\LastKnownGood\') }
        'relative' { $execute = 'MTTFTest.Watchdog.exe' }
        'missing-argument' { $arguments = '' }
        'extra-argument' { $arguments = '--launch-main --other' }
    }
    $action = [pscustomobject]@{ Execute=$execute; Arguments=$arguments }
    $actions = @($action)
    if ($scenario -eq 'multiple') { $actions = @($action, $action) }
    if ($scenario -eq 'none') { $actions = @() }
    $caught = $null
    try { Assert-InstalledTaskAction ([pscustomobject]@{Actions=$actions;Settings=[pscustomobject]@{Enabled=($scenario -ne 'disabled')}}) $expected '--launch-main' }
    catch { $caught = $_ }
    if (($null -ne $caught) -ne ($scenario -notin @('valid', 'case'))) { throw "Unexpected validation: $scenario" }
    Write-Output "PASS installed task action: $scenario"
}

$healthDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-Health'
}, $true)
if ($null -eq $healthDefinition) { throw 'Health function missing' }
foreach ($scenario in @('valid', 'agent-old', 'auto-arguments', 'auto-working-directory', 'auto-working-missing', 'auto-working-relative', 'task-disabled', 'service-stopped', 'service-old', 'service-query-error', 'service-process-old', 'service-process-changed', 'service-config-changed', 'service-process-exited', 'service-process-open-error', 'health-arguments', 'health-script-missing', 'health-trigger-missing')) {
    & {
        param($healthDefinition, $scenario)
        . ([scriptblock]::Create($healthDefinition))
        $serviceName = 'FixtureSupervisor'
        $taskName = 'FixtureAgent'
        $autoStartTaskName = 'FixtureAuto'
        $healthTaskName = 'FixtureHealth'
        $runtimeConfigNames = @()
        $script:taskQueries = @()
        $script:serviceQueries = 0
        $script:processDisposed = $false
        function Assert-RequiredProgramFiles { param($Path) }
        function Test-Path { param($LiteralPath, $PathType) return ($scenario -ne 'health-script-missing') }
        function Get-Service {
            [CmdletBinding()]param($Name)
            [pscustomobject]@{Status=$(if ($scenario -eq 'service-stopped') { 'Stopped' } else { 'Running' })}
        }
        function Get-CimInstance {
            [CmdletBinding()]param($ClassName, $Filter)
            if ($ClassName -ne 'Win32_Service' -or $Filter -ne "Name='FixtureSupervisor'") {
                throw 'Unexpected service query'
            }
            if ($scenario -eq 'service-query-error') { Write-Error 'FixtureServiceQueryError'; return }
            $path = 'C:\FixtureOnly\MTTFTest\Current\MTTFTest.Watchdog.exe'
            if ($scenario -eq 'service-old') { $path = $path.Replace('\Current\', '\LastKnownGood\') }
            $script:serviceQueries++
            if ($scenario -eq 'service-config-changed' -and $script:serviceQueries -gt 1) {
                $path = $path.Replace('\Current\', '\LastKnownGood\')
            }
            $reportedPid = 42
            if ($scenario -eq 'service-process-changed' -and $script:serviceQueries -gt 1) { $reportedPid = 43 }
            [pscustomobject]@{PathName=('"' + $path + '"');ProcessId=$reportedPid;State='Running'}
        }
        function Get-Process {
            [CmdletBinding()]param($Id)
            if ($Id -ne 42) { throw 'Wrong process query' }
            if ($scenario -eq 'service-process-open-error') { Write-Error 'FixtureProcessOpenError'; return }
            $path = 'C:\FixtureOnly\MTTFTest\Current\MTTFTest.Watchdog.exe'
            if ($scenario -eq 'service-process-old') { $path = $path.Replace('\Current\', '\LastKnownGood\') }
            $process = [pscustomobject]@{Handle=1;HasExited=($scenario -eq 'service-process-exited');Path=$path}
            $process | Add-Member ScriptMethod Dispose { $script:processDisposed = $true }
            return $process
        }
        function Get-ScheduledTask {
            [CmdletBinding()]param($TaskName, $TaskPath)
            if ($TaskPath -ne '\') { throw 'Health queried wrong task folder' }
            $script:taskQueries += $TaskName
            $executable = 'C:\FixtureOnly\MTTFTest\Current\MTTFTest.SessionAgent.exe'
            $arguments = ''
            $workingDirectory = 'C:\FixtureOnly\MTTFTest\Current'
            if ($TaskName -eq 'FixtureAuto') {
                $executable = 'C:\FixtureOnly\MTTFTest\Current\MTTFTest.Watchdog.exe'
                $arguments = '--launch-main'
                if ($scenario -eq 'auto-arguments') { $arguments = '--wrong' }
                if ($scenario -eq 'auto-working-directory') { $workingDirectory = 'C:\FixtureOnly\MTTFTest\LastKnownGood' }
                if ($scenario -eq 'auto-working-missing') { $workingDirectory = '' }
                if ($scenario -eq 'auto-working-relative') { $workingDirectory = 'Current' }
            } elseif ($scenario -eq 'agent-old') {
                $executable = $executable.Replace('\Current\', '\LastKnownGood\')
            }
            if ($TaskName -eq 'FixtureHealth') {
                $executable = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
                $arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\FixtureOnly\MTTFTest\Current\Deployment\Test-MTTFTest-RecoveryHealth.ps1" -InstallRoot "C:\FixtureOnly\MTTFTest"'
                if ($scenario -eq 'health-arguments') { $arguments = '--wrong' }
            }
            $interval = 'PT1M'
            if ($TaskName -eq 'FixtureHealth' -and $scenario -eq 'health-trigger-missing') { $interval = 'PT1H' }
            [pscustomobject]@{
                Actions=@([pscustomobject]@{Execute=$executable;Arguments=$arguments;WorkingDirectory=$workingDirectory})
                Settings=[pscustomobject]@{RestartCount=1;Enabled=($scenario -ne 'task-disabled')}
                Triggers=@([pscustomobject]@{Repetition=$null},
                    [pscustomobject]@{Repetition=[pscustomobject]@{Interval=$interval}})
            }
        }
        $caught = $null
        try { Assert-Health 'C:\FixtureOnly\MTTFTest' } catch { $caught = $_ }
        if (($null -ne $caught) -ne ($scenario -ne 'valid')) { throw "Health integration mismatch: $scenario; $caught" }
        if ($scenario -eq 'valid' -and ($script:taskQueries -join ',') -ne 'FixtureAgent,FixtureAuto,FixtureHealth') {
            throw 'Health did not inspect all three tasks'
        }
        if ($scenario -in @('agent-old', 'auto-arguments') -and $caught.Exception.Message -ne 'InstalledTaskActionMismatch') {
            throw 'Health rejected for wrong reason'
        }
        if ($scenario -in @('auto-working-directory', 'auto-working-missing', 'auto-working-relative') -and $caught.Exception.Message -ne 'InstalledTaskWorkingDirectoryMismatch') {
            throw 'Health did not reject wrong working directory'
        }
        if ($scenario -eq 'task-disabled' -and $caught.Exception.Message -ne 'InstalledTaskNotEnabled') {
            throw 'Health did not reject disabled task'
        }
        if ($scenario -eq 'service-old' -and $caught.Exception.Message -ne 'InstalledSupervisorPathMismatch') {
            throw 'Health did not reject old supervisor path'
        }
        if ($scenario -in @('service-process-old', 'service-process-exited') -and $caught.Exception.Message -ne 'InstalledSupervisorProcessMismatch') {
            throw 'Health did not reject old process image'
        }
        if ($scenario -in @('service-process-changed', 'service-config-changed') -and $caught.Exception.Message -ne 'InstalledSupervisorProcessChanged') {
            throw 'Health did not reject changed process'
        }
        if ($scenario -eq 'service-process-open-error' -and $caught.Exception.Message -notlike '*FixtureProcessOpenError*') {
            throw 'Process open error was not propagated'
        }
        if ($scenario -in @('valid', 'service-process-old', 'service-process-changed', 'service-config-changed', 'service-process-exited') -and -not $script:processDisposed) {
            throw 'Health leaked process handle'
        }
        if ($scenario -eq 'health-arguments' -and $caught.Exception.Message -ne 'InstalledTaskActionMismatch') {
            throw 'Health task arguments were not checked'
        }
        if ($scenario -eq 'health-script-missing' -and $caught.Exception.Message -ne 'InstalledHealthScriptMissing') {
            throw 'Health script existence was not checked'
        }
        if ($scenario -eq 'health-trigger-missing' -and $caught.Exception.Message -ne 'InstalledHealthKeepaliveMissing') {
            throw 'Health trigger was not checked'
        }
        Write-Output "PASS installed health integration: $scenario"
    } $healthDefinition.Extent.Text $scenario
}
