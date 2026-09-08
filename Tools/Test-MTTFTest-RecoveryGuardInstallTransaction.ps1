#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'Install-MTTFTest-RecoveryGuard.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
foreach ($name in @('Write-GuardAtomicBytes', 'Invoke-GuardTaskTransaction')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "Missing production function: $name" }
    Invoke-Expression $definition.Extent.Text
}
$realWrite = ${function:Write-GuardAtomicBytes}
function Write-GuardAtomicBytes([string]$Path, [byte[]]$Bytes) {
    if ($script:failRegistration -and $Path -eq $script:registrationPath -and
        [Text.Encoding]::UTF8.GetString($Bytes) -eq 'new-registration') {
        $script:failRegistration = $false
        throw 'Injected registration write failure'
    }
    & $realWrite $Path $Bytes
}
# Replace the scheduler boundary only; exercise the production transaction and
# real atomic files in a new isolated directory. No OS task is queried/mutated.
function Register-ScheduledTask($TaskName, $TaskPath, $Xml, [switch]$Force, $ErrorAction) {
    if ($TaskPath -ne '\') { throw 'Unexpected task folder' }
    $script:calls++
    $script:tasks[$TaskName] = [string]$Xml
    if ($script:calls -in $script:failCalls) { throw 'Injected task response loss after write' }
}
function Unregister-ScheduledTask($TaskName, $TaskPath, $Confirm, $ErrorAction) {
    if ($TaskPath -ne '\') { throw 'Unexpected task folder' }
    $script:calls++
    $script:tasks.Remove($TaskName)
    if ($script:calls -in $script:failCalls) { throw 'Injected task response loss after removal' }
}
function Get-ScheduledTask($TaskPath, $ErrorAction) {
    foreach ($name in $script:tasks.Keys) { [pscustomobject]@{ TaskName = $name; TaskPath = '\' } }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('GuardInstallTransaction-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$results = @()
foreach ($case in @('Install', 'Upgrade', 'FirstWriteLost', 'SecondWriteLost', 'RegistrationFailure', 'RollbackFailure', 'Uninstall', 'UninstallFailure')) {
    $folder = Join-Path $root $case
    [void][IO.Directory]::CreateDirectory($folder)
    $script:registrationPath = Join-Path $folder 'installation.json'
    $settingsPath = Join-Path $folder 'settings.json'
    $hadFixtureSettings = $case -notin @('Install', 'FirstWriteLost')
    if ($hadFixtureSettings) { [IO.File]::WriteAllText($settingsPath, 'old-settings') }
    $script:tasks = @{}
    $previous = [ordered]@{}
    if ($case -ne 'Install' -and $case -ne 'FirstWriteLost') {
        $previous['MTTFTestRecoveryGuardExecution'] = 'old-execute'
        $previous['MTTFTestRecoveryGuard'] = 'old-scan'
        foreach ($name in $previous.Keys) { $script:tasks[$name] = $previous[$name] }
        [IO.File]::WriteAllBytes($script:registrationPath, [byte[]]@(239,187,191,111,108,100))
    }
    $oldBytes = if ([IO.File]::Exists($script:registrationPath)) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($script:registrationPath)) } else { $null }
    $desired = [ordered]@{ MTTFTestRecoveryGuardExecution = 'new-execute'; MTTFTestRecoveryGuard = 'new-scan' }
    if ($case -like 'Uninstall*') { $desired['MTTFTestRecoveryGuardExecution'] = $null; $desired['MTTFTestRecoveryGuard'] = $null }
    $script:calls = 0
    $script:failCalls = switch ($case) {
        'FirstWriteLost' { @(1) }
        'SecondWriteLost' { @(2) }
        'RollbackFailure' { @(2,3) }
        'UninstallFailure' { @(2) }
        default { @() }
    }
    $script:failRegistration = $case -eq 'RegistrationFailure'
    $keepMaintenance = $false; $failed = $false; $failureMessage = ''
    try { Invoke-GuardTaskTransaction $desired $previous $script:registrationPath 'new-registration' (Join-Path $folder 'evidence') ([ref]$keepMaintenance) $settingsPath ([Text.Encoding]::UTF8.GetBytes('new-settings')) }
    catch { $failed = $true; $failureMessage = $_.Exception.Message }
    $expectedFailure = $case -notin @('Install', 'Upgrade', 'Uninstall')
    if ($failed -ne $expectedFailure) { throw "$case wrong transaction outcome: $failureMessage" }
    if ($keepMaintenance -ne ($case -eq 'RollbackFailure')) { throw "$case wrong maintenance decision" }
    if ($failed) {
        if ($hadFixtureSettings) {
            if ([IO.File]::ReadAllText($settingsPath) -ne 'old-settings') { throw "$case did not restore settings" }
        } elseif ([IO.File]::Exists($settingsPath)) { throw "$case left newly created settings" }
        foreach ($name in $desired.Keys) {
            if ($previous.Contains($name)) { if ($script:tasks[$name] -ne $previous[$name]) { throw "$case did not restore $name" } }
            elseif ($script:tasks.ContainsKey($name)) { throw "$case left newly created task" }
        }
        $actualBytes = if ([IO.File]::Exists($script:registrationPath)) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($script:registrationPath)) } else { $null }
        if ($oldBytes -cne $actualBytes) { throw "$case did not restore exact registration bytes" }
    } else {
        if ([IO.File]::ReadAllText($settingsPath) -ne 'new-settings') { throw "$case did not commit settings" }
        foreach ($name in $desired.Keys) {
            if ($null -eq $desired[$name]) { if ($script:tasks.ContainsKey($name)) { throw "$case retained task" } }
            elseif ($script:tasks[$name] -ne $desired[$name]) { throw "$case wrong installed task" }
        }
        if ([IO.File]::ReadAllText($script:registrationPath) -ne 'new-registration') { throw "$case registration not committed" }
    }
    $record = Get-Content -LiteralPath (Get-ChildItem -LiteralPath (Join-Path $folder 'evidence') -Filter '*.json').FullName -Raw | ConvertFrom-Json
    $expectedState = if ($keepMaintenance) { 'RollbackFailed' } elseif ($failed) { 'RolledBack' } else { 'Committed' }
    if ($record.state -ne $expectedState) { throw "$case incorrect evidence state" }
    $results += [ordered]@{ name = $case; passed = $true; state = $record.state }
}
[ordered]@{ passed = $results.Count; cases = $results; evidence = $root; taskRegistrationPerformed = $false; scope = 'ProductionTransactionWithInjectedSchedulerFailures' } | ConvertTo-Json -Depth 6
