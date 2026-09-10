param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText($InstallerPath), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Backup-InstalledRuntimeTasks' }, $true)
if ($null -eq $definition) { throw 'Backup function missing' }
. ([scriptblock]::Create($definition.Extent.Text))
$pathDefinition = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-DeploymentEvidenceDirectory' }, $true)
if ($null -eq $pathDefinition) { throw 'Path guard missing' }
. ([scriptblock]::Create($pathDefinition.Extent.Text))
$reader = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Read-DeploymentTaskBackup' }, $true)
if ($null -eq $reader) { throw 'Backup reader missing' }
. ([scriptblock]::Create($reader.Extent.Text))
$entryValidator = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-DeploymentTaskBackupEntries' }, $true)
if ($null -eq $entryValidator) { throw 'Entry validator missing' }
. ([scriptblock]::Create($entryValidator.Extent.Text))
# Non-elevated local fixture: ACL creation is verified separately on JXCQ.
function Initialize-ProtectedDeploymentDirectory {
    param($Directory)
    Assert-DeploymentEvidenceDirectory $Directory
    [void](New-Item -ItemType Directory -Path $Directory -Force)
    Assert-DeploymentEvidenceDirectory $Directory
}
$autoStartTaskName = 'FixtureAuto'
$taskName = 'FixtureAgent'
$healthTaskName = 'FixtureHealth'
$fixtureXml = '<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Settings><Enabled>true</Enabled></Settings></Task>'
function Get-InstalledTaskSecurityDescriptor {
    param($Name)
    if ($Name -ne 'FixtureAuto') { throw 'Wrong security identity' }
    if ($scenario -eq 'security-error') { throw 'FixtureSecurity' }
    return 'O:SYG:SYD:(A;;FA;;;SY)'
}
function Get-InstalledTaskRunningState {
    param($Name)
    if ($Name -ne 'FixtureAuto') { throw 'Wrong running-state identity' }
    return $true
}
function Export-ScheduledTask {
    [CmdletBinding()]param($TaskName, $TaskPath)
    if ($TaskName -ne 'FixtureAuto' -or $TaskPath -ne '\') { throw 'Wrong export identity' }
    if ($scenario -eq 'error') { throw 'FixtureExport' }
    if ($scenario -eq 'invalid') { return '<Unexpected />' }
    if ($scenario -eq 'empty') { return '' }
    if ($scenario -eq 'dtd-export') { return '<!DOCTYPE Task [<!ENTITY x "fixture">]><Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">&x;</Task>' }
    return $fixtureXml
}
$original = $env:ProgramData
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('epb-task-backup-' + [Guid]::NewGuid().ToString('N'))
try {
    $env:ProgramData = $fixture
    $tasks = @([pscustomobject]@{ TaskName = 'FixtureAuto'; TaskPath = '\' })
    foreach ($scenario in @('valid', 'error', 'invalid', 'empty', 'duplicate', 'security-error', 'dtd-export')) {
        $caught = $null
        $inputs = if ($scenario -eq 'duplicate') { @($tasks[0], $tasks[0]) } else { $tasks }
        try { $reference = Backup-InstalledRuntimeTasks 'C:\FixtureOnly\MTTFTest' $inputs } catch { $caught = $_ }
        if (($null -ne $caught) -ne ($scenario -ne 'valid')) { throw "Wrong backup outcome: $scenario" }
        $files = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'MTTFTestDeploymentEvidence\TaskBackups') -File)
        if ($files.Count -ne 1) { throw 'Invalid export published a backup or left a pending file' }
        $saved = Get-Content $files[0].FullName -Raw | ConvertFrom-Json
        if ($saved.schema -ne 4 -or $saved.tasks[0].securityDescriptor -cne 'O:SYG:SYD:(A;;FA;;;SY)' -or
            $saved.tasks[0].securityInformation -ne 7 -or $saved.tasks.Count -ne 3 -or $saved.tasks[0].xml -cne $fixtureXml -or
            -not $saved.tasks[0].existed -or -not $saved.tasks[0].wasRunning -or
            $saved.tasks[1].existed -or $saved.tasks[1].wasRunning -or
            $saved.tasks[2].existed -or $saved.tasks[2].wasRunning) { throw 'Backup contents incorrect' }
        Write-Output "PASS task XML backup: $scenario"
        if ($scenario -eq 'valid') {
            $hash = $reference.Sha256
            if ($reference.Path -cne $files[0].FullName -or $hash -cne (Get-FileHash -LiteralPath $files[0].FullName).Hash -or
                $reference.TransactionId -cne $saved.transactionId) { throw 'Publisher reference mismatch' }
            $verified = Read-DeploymentTaskBackup $files[0].FullName 'C:\FixtureOnly\MTTFTest' $hash
            if ($verified.machineName -ine $env:COMPUTERNAME) { throw 'Machine binding lost' }
            foreach ($bad in @('hash', 'root')) {
                $caughtRead = $null
                try {
                    if ($bad -eq 'hash') { Read-DeploymentTaskBackup $files[0].FullName 'C:\FixtureOnly\MTTFTest' ('0' * 64) | Out-Null }
                    else { Read-DeploymentTaskBackup $files[0].FullName 'C:\OtherInstall' $hash | Out-Null }
                } catch { $caughtRead = $_ }
                $expectedError = if ($bad -eq 'hash') { 'TaskBackupHashMismatch' } else { 'TaskBackupIdentityMismatch' }
                if ($null -eq $caughtRead -or $caughtRead.Exception.Message -ne $expectedError) { throw "Wrong reader rejection: $bad $caughtRead" }
            }
            Write-Output 'PASS backup reader: matched identity, changed hash and other install'
            foreach ($invalidEntry in @('duplicate', 'path', 'boolean', 'running', 'absence', 'sddl', 'xml', 'dtd')) {
                $candidate = $saved | ConvertTo-Json -Depth 8 | ConvertFrom-Json
                switch ($invalidEntry) {
                    'duplicate' { $candidate.tasks[1].name = $candidate.tasks[0].name }
                    'path' { $candidate.tasks[0].path = '\Other\' }
                    'boolean' { $candidate.tasks[0].existed = 'true' }
                    'running' { $candidate.tasks[0].wasRunning = 'true' }
                    'absence' { $candidate.tasks[1].xml = '<Task />' }
                    'sddl' { $candidate.tasks[0].securityDescriptor = '' }
                    'xml' { $candidate.tasks[0].xml = '<Other />' }
                    'dtd' { $candidate.tasks[0].xml = '<!DOCTYPE Task [<!ENTITY x "fixture">]><Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">&x;</Task>' }
                }
                $rejected = $null
                try { Assert-DeploymentTaskBackupEntries $candidate } catch { $rejected = $_ }
                if ($null -eq $rejected) { throw "Invalid entry accepted: $invalidEntry" }
            }
            Write-Output 'PASS backup entries: eight malformed definition cases rejected'
        }
    }
    Backup-InstalledRuntimeTasks 'C:\FixtureOnly\MTTFTest' @()
    $allFiles = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'MTTFTestDeploymentEvidence\TaskBackups') -File)
    $newFiles = @($allFiles | Where-Object { $_.FullName -ne $files[0].FullName })
    if ($allFiles.Count -ne 2 -or $newFiles.Count -ne 1) { throw 'Missing absence backup' }
    $absence = Get-Content $newFiles[0].FullName -Raw | ConvertFrom-Json
    if ($absence.tasks.Count -ne 3 -or @($absence.tasks | Where-Object { $_.existed -or $null -ne $_.xml }).Count -ne 0) {
        throw 'All-absent backup must explicitly retain three absence records'
    }
    Write-Output 'PASS task XML backup: all absent'
    $move = '[IO.File]::Move($pending, $path)'
    if (-not $definition.Extent.Text.Contains($move)) { throw 'Publication injection point missing' }
    $injected = $definition.Extent.Text.Replace($move, $move + [Environment]::NewLine +
        '[IO.File]::AppendAllText($path, "corrupted")')
    . ([scriptblock]::Create($injected))
    $caught = $null
    try { Backup-InstalledRuntimeTasks 'C:\FixtureOnly\MTTFTest' @() } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Message -notlike 'TaskBackupReadbackMismatch:*') {
        throw "Published corruption was not rejected: $caught"
    }
    $retained = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'MTTFTestDeploymentEvidence\TaskBackups') -File)
    if ($retained.Count -ne 3) { throw 'Corrupt publication evidence was not retained' }
    Write-Output 'PASS task XML backup: published corruption rejected and retained'
    . ([scriptblock]::Create($definition.Extent.Text))
    $redirectRoot = Join-Path $fixture 'redirected'
    $outside = Join-Path $fixture 'must-stay-empty'
    [void](New-Item -ItemType Directory -Path $redirectRoot, $outside)
    [void](New-Item -ItemType Junction -Path (Join-Path $redirectRoot 'MTTFTestDeploymentEvidence') -Target $outside)
    $env:ProgramData = $redirectRoot
    $caught = $null
    try { Backup-InstalledRuntimeTasks 'C:\FixtureOnly\MTTFTest' @() } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Message -notlike 'DeploymentEvidencePathRedirectedOrNotDirectory:*' -or
        @(Get-ChildItem -LiteralPath $outside -Force).Count -ne 0) {
        throw 'Redirected backup path was followed or not rejected'
    }
    Write-Output 'PASS task XML backup: ancestor junction rejected before target mutation'
    & {
        function Test-Path {
            [CmdletBinding()]param($LiteralPath)
            Write-Error 'InjectedPathQueryFailure'
            return $false
        }
        function New-Item { throw 'MutationAfterPathQueryFailure' }
        $caught = $null
        try { Backup-InstalledRuntimeTasks 'C:\FixtureOnly\MTTFTest' @() } catch { $caught = $_ }
        if ($null -eq $caught -or $caught.Exception.Message -notlike '*InjectedPathQueryFailure*') {
            throw "Path lookup failure was suppressed: $caught"
        }
        Write-Output 'PASS task XML backup: nonterminating path query error blocks mutation'
    }
    Write-Output "Evidence: $fixture"
} finally { $env:ProgramData = $original }
