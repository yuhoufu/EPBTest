param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @(
        'Read-Utf8JsonFile', 'Write-DeploymentTaskPreparation',
        'Read-DeploymentInstallTransaction', 'Set-DeploymentInstallTransactionPhase')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "Missing function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$oldProgramData = $env:ProgramData
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('epb-install-journal-' + [Guid]::NewGuid().ToString('N'))
$env:ProgramData = $fixture
$root = 'C:\FixtureOnly\MTTFTest'
$transactionId = 'a' * 32
$reference = [pscustomobject]@{
    Path = Join-Path $fixture 'backup.json'; Sha256 = 'B' * 64
    TransactionId = $transactionId; InstallRoot = $root; MachineName = $env:COMPUTERNAME
}
function Initialize-ProtectedDeploymentDirectory { param($Directory) [void](New-Item -ItemType Directory -Path $Directory -Force) }
function Assert-DeploymentEvidenceDirectory { param($Directory) }
function Read-DeploymentTaskBackup {
    param($Path, $Root, $ExpectedSha256)
    if ($Path -cne $reference.Path -or $Root -cne $root -or $ExpectedSha256 -cne $reference.Sha256) {
        throw 'Wrong backup publisher reference'
    }
    return [pscustomobject]@{
        transactionId=$transactionId; installRoot=$root; machineName=$env:COMPUTERNAME
    }
}
function Assert-CurrentSlotTransactionReference { param($Reference, $Root) return [pscustomobject]@{ phase='CurrentPublishedPendingInstallCommit' } }

try {
    $path = Write-DeploymentTaskPreparation $reference $root $true $true 'Repair'
    $prepared = Read-DeploymentInstallTransaction $path $root
    if ($prepared.schema -ne 2 -or $prepared.phase -cne 'TaskBackupPrepared' -or
        -not $prepared.serviceExistedBefore -or -not $prepared.serviceWasRunning -or
        $prepared.operation -cne 'Repair') { throw 'Prepared journal fields invalid' }

    $current = [pscustomobject]@{
        Journal=Join-Path $root 'install-transaction.json'; TransactionId=('c' * 32)
        Retired=Join-Path $root '.retired-fixture'; OldCurrentExisted=$true
    }
    [void](Set-DeploymentInstallTransactionPhase $path $root @('TaskBackupPrepared') 'CurrentPublished' $current)
    $shortcut = [pscustomobject]@{
        PlanPath=Join-Path $root 'DeploymentShortcutTransactions\fixture\plan.json'
        AllowedPaths=@('C:\FixtureOnly\Desktop\MTTFTest.lnk')
    }
    [void](Set-DeploymentInstallTransactionPhase $path $root @('CurrentPublished') 'CurrentPublished' $null $shortcut)
    $marker = [pscustomobject]@{ PlanPath=Join-Path $root 'DeploymentMarkerTransactions\fixture\plan.json' }
    $config = [pscustomobject]@{ Entries=@(
        [pscustomobject]@{ Target='C:\FixtureOnly\Config\a.xml'; Sha256=('D' * 64) }) }
    [void](Set-DeploymentInstallTransactionPhase $path $root @('CurrentPublished') 'CurrentPublished' `
        $null $null $marker $config)
    [void](Set-DeploymentInstallTransactionPhase $path $root @('CurrentPublished') 'CommitAuthorized')
    [void](Set-DeploymentInstallTransactionPhase $path $root @('CommitAuthorized') 'Completed')
    $completed = Read-DeploymentInstallTransaction $path $root
    if ($completed.phase -cne 'Completed' -or $completed.currentTransaction.TransactionId -cne $current.TransactionId -or
        $completed.shortcutTransaction.PlanPath -cne $shortcut.PlanPath -or
        $completed.markerTransaction.PlanPath -cne $marker.PlanPath -or
        $completed.configTransaction.Entries[0].Sha256 -cne ('D' * 64)) {
        throw 'Journal transition or Current binding invalid'
    }
    Write-Output 'PASS install transaction journal: durable phase transitions and Current binding'

    $mismatch = $null
    try { Set-DeploymentInstallTransactionPhase $path $root @('TaskBackupPrepared') 'RolledBack' | Out-Null }
    catch { $mismatch = $_ }
    if ($null -eq $mismatch -or $mismatch.Exception.Message -cne 'InstallTransactionPhaseMismatch') {
        throw 'Out-of-order transition accepted'
    }
    $invalid = $null
    try { Write-DeploymentTaskPreparation $reference $root $false $true 'Install' | Out-Null }
    catch { $invalid = $_ }
    if ($null -eq $invalid -or $invalid.Exception.Message -cne 'InstallTransactionServiceStateInvalid') {
        throw 'Impossible service state accepted'
    }
    Write-Output 'PASS install transaction journal: invalid state and transition rejected'
    Write-Output 'PASS install transaction journal 2/2'
}
finally {
    $env:ProgramData = $oldProgramData
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
