param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Read-Utf8JsonFile', 'ConvertFrom-SavedCurrentTransaction', 'Resolve-PendingInstallTransaction')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "Missing function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$oldProgramData = $env:ProgramData
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('epb-install-recovery-' + [Guid]::NewGuid().ToString('N'))
$env:ProgramData = $fixture
$directory = Join-Path $fixture 'MTTFTestDeploymentEvidence\TaskPreparations'
[void](New-Item -ItemType Directory -Path $directory -Force)
$root = 'C:\FixtureOnly\MTTFTest'
$script:records = @{}
$script:operations = @()
function Assert-DeploymentEvidenceDirectory {}
function Read-DeploymentInstallTransaction { param($Path, $Root) return $script:records[$Path] }
function Restore-FailedInstallTransaction { $script:operations += 'restore' }
function Set-DeploymentInstallTransactionPhase {
    param($Path, $Root, $ExpectedPhase, $NewPhase, $CurrentTransaction)
    $script:operations += "phase:$NewPhase"
}
function Archive-MaintenanceInhibitForInstall { $script:operations += 'archive' }
function Complete-CurrentSlotTransaction { $script:operations += 'complete-current' }

function Add-FixtureRecord([string]$Id, [string]$Phase, $Current = $null) {
    $path = Join-Path $directory ($Id + '.json')
    [IO.File]::WriteAllText($path, (@{
        schema=2; machineName=$env:COMPUTERNAME; installRoot=$root
    } | ConvertTo-Json), [Text.Encoding]::UTF8)
    $script:records[$path] = [pscustomobject]@{
        schema=2; phase=$Phase; transactionId=$Id; machineName=$env:COMPUTERNAME
        installRoot=$root; backupPath='fixture-backup.json'; backupSha256=('A' * 64)
        serviceExistedBefore=$true; serviceWasRunning=$true; currentTransaction=$Current
    }
    return $path
}

try {
    $rollbackPath = Add-FixtureRecord ('a' * 32) 'TaskBackupPrepared'
    Resolve-PendingInstallTransaction $root
    if (($script:operations -join ',') -cne 'restore,phase:RolledBack,archive') {
        throw "Rollback recovery order invalid: $($script:operations -join ',')"
    }
    Remove-Item -LiteralPath $rollbackPath -Force
    Write-Output 'PASS install transaction recovery: uncommitted transaction rolls back'

    $script:operations = @()
    $journal = Join-Path $fixture 'current-journal.json'
    [IO.File]::WriteAllText($journal, '{}')
    $current = [pscustomobject]@{
        Journal=$journal; TransactionId=('c' * 32); Retired=(Join-Path $fixture '.retired-fixture')
        OldCurrentExisted=$true
    }
    $authorizedPath = Add-FixtureRecord ('b' * 32) 'CommitAuthorized' $current
    Resolve-PendingInstallTransaction $root
    if (($script:operations -join ',') -cne 'complete-current,phase:Completed,archive') {
        throw "Authorized recovery order invalid: $($script:operations -join ',')"
    }
    Remove-Item -LiteralPath $authorizedPath -Force
    Write-Output 'PASS install transaction recovery: authorized transaction completes'

    $script:operations = @()
    $first = Add-FixtureRecord ('d' * 32) 'TaskBackupPrepared'
    $second = Add-FixtureRecord ('e' * 32) 'CurrentPublished'
    $ambiguous = $null
    try { Resolve-PendingInstallTransaction $root } catch { $ambiguous = $_ }
    if ($null -eq $ambiguous -or $ambiguous.Exception.Message -cne 'InstallTransactionRecoveryAmbiguous' -or
        $script:operations.Count -ne 0) { throw 'Ambiguous recovery was not rejected before mutation' }
    Write-Output 'PASS install transaction recovery: ambiguity rejected before mutation'
    Write-Output 'PASS install transaction recovery 3/3'
}
finally {
    $env:ProgramData = $oldProgramData
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
