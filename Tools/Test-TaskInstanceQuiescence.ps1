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
foreach ($scenario in @('stopped', 'draining', 'timeout', 'reenabled', 'query-error', 'connect-error', 'null-instances')) {
    & {
        param($definition, $scenario)
        . ([scriptblock]::Create($definition))
        $script:polls = 0
        $script:sleeps = 0
        $registered = [pscustomobject]@{ Enabled = ($scenario -eq 'reenabled') }
        $registered | Add-Member ScriptMethod GetInstances {
            param($flags)
            if ($flags -ne 0) { throw 'Wrong flags' }
            $script:polls++
            if ($scenario -eq 'null-instances') { return $null }
            $count = 0
            if ($scenario -eq 'timeout' -or ($scenario -eq 'draining' -and $script:polls -eq 1)) { $count = 1 }
            return [pscustomobject]@{ Count = $count }
        }
        $folder = [pscustomobject]@{}
        $folder | Add-Member ScriptMethod GetTask {
            param($name)
            if ($name -ne 'Fixture') { throw 'Wrong task' }
            if ($scenario -eq 'query-error') { throw 'FixtureQuery' }
            return $script:fixtureRegistered
        }
        $scheduler = [pscustomobject]@{}
        $scheduler | Add-Member ScriptMethod Connect {
            if ($scenario -eq 'connect-error') { throw 'FixtureConnect' }
        }
        $scheduler | Add-Member ScriptMethod GetFolder {
            param($path)
            if ($path -ne '\') { throw 'Wrong folder' }
            return $script:fixtureFolder
        }
        $script:fixtureRegistered = $registered
        $script:fixtureFolder = $folder
        $script:fixtureScheduler = $scheduler
        function New-Object {
            param($ComObject)
            if ($ComObject -ne 'Schedule.Service') { throw 'Wrong COM class' }
            return $script:fixtureScheduler
        }
        function Start-Sleep { param($Milliseconds) $script:sleeps++ }
        $caught = $null
        $timeout = if ($scenario -eq 'timeout') { 0 } else { 30000 }
        try { Wait-InstalledTaskStopped 'Fixture' $timeout } catch { $caught = $_ }
        $expected = switch ($scenario) {
            'timeout' { 'InstalledTaskStopTimeout' }
            'reenabled' { 'InstalledTaskReenabled' }
            'query-error' { 'FixtureQuery' }
            'connect-error' { 'FixtureConnect' }
            'null-instances' { 'InstalledTaskInstancesUnavailable' }
            default { '' }
        }
        if ($expected) {
            if ($null -eq $caught -or $caught.Exception.Message -notmatch $expected) { throw "Wrong rejection: $scenario $caught" }
        } elseif ($null -ne $caught) { throw $caught }
        if ($scenario -eq 'draining' -and ($script:polls -ne 2 -or $script:sleeps -ne 1)) { throw 'Did not wait for instance exit' }
        if ($scenario -eq 'stopped' -and ($script:polls -ne 1 -or $script:sleeps -ne 0)) { throw 'Stopped task unnecessarily waited' }
        Write-Output "PASS task instances: $scenario"
    } $definition.Extent.Text $scenario
}
