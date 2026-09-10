# Local, isolated COM shortcut tests. Never invoke the installer entry point.
$ErrorActionPreference = 'Stop'
$errorsFound = $null
$tokensFound = $null
$source = Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokensFound, [ref]$errorsFound)
if ($errorsFound.Count -ne 0) { throw 'InstallerParseFailed' }
foreach ($name in @('Test-ShortcutOwnedByInstallation','Backup-ShortcutBeforeChange','Assert-ShortcutUnchangedSinceBackup','Write-PreparedShortcutReceipt','Write-CreatedShortcutReceipt','Write-ShortcutTransactionPlan','Complete-ShortcutTransaction','Restore-ShortcutTransaction','Install-Shortcuts','Remove-Shortcuts')) {
    $functions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($functions.Count -ne 1) { throw "FunctionMissingOrAmbiguous:$name" }
    . ([scriptblock]::Create($functions[0].Extent.Text))
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ShortcutBackupFixture-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $fixture 'Install'
$current = Join-Path $install 'Current'
$desktop = Join-Path $fixture 'IsolatedDesktop'
[void](New-Item -ItemType Directory -Path $current, $desktop)
[void](New-Item -ItemType File -Path (Join-Path $current 'MTTFTest.exe'), (Join-Path $current 'MTTFTest.Watchdog.exe'))
function Get-ShortcutPaths([string]$Name = 'new.lnk') { return @(Join-Path $desktop $Name) }
$shell = New-Object -ComObject WScript.Shell
function New-TestLink([string]$Path, [string]$Target) {
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.Arguments = '--original-argument'
    $link.WorkingDirectory = $fixture
    $link.IconLocation = "$Target,0"
    $link.Description = 'SYNTHETIC shortcut only'
    $link.Save()
}
$owned = Join-Path $desktop 'MT EPB 试验系统 V2.14.lnk'
$foreign = Join-Path $desktop 'MT EPB 试验系统 V2.17.lnk'
$replacement = Join-Path $desktop 'new.lnk'
New-TestLink $owned (Join-Path $current 'MTTFTest.exe')
New-TestLink $foreign (Join-Path $fixture 'unrelated.exe')
$ownedHash = (Get-FileHash -LiteralPath $owned).Hash
$foreignHash = (Get-FileHash -LiteralPath $foreign).Hash
New-TestLink $replacement (Join-Path $fixture 'another-install.exe')
try { Install-Shortcuts $install; throw 'ExpectedCollisionRejection' }
catch { if ($_.Exception.Message -notlike 'ShortcutNameOwnedByAnotherTarget:*') { throw } }
if (-not (Test-Path -LiteralPath $owned)) { throw 'CollisionDeletedOldLink' }
Write-Output 'PASS foreign destination collision preserves old entry'
# Replace only this fixture-owned link, never a real desktop file.
New-TestLink $replacement (Join-Path $current 'MTTFTest.Watchdog.exe')
try { Install-Shortcuts $install; throw 'ExpectedLauncherArgumentRejection' }
catch { if ($_.Exception.Message -notlike 'ShortcutNameOwnedByAnotherTarget:*') { throw } }
if (-not (Test-Path -LiteralPath $owned)) { throw 'LauncherCollisionDeletedOldLink' }
Write-Output 'PASS same Supervisor with unrelated arguments is not installation ownership'
$link = $shell.CreateShortcut($replacement)
$link.Arguments = "--launch-main --main-executable `"$(Join-Path $current 'MTTFTest.exe')`""
$link.Save()
$replacementHash = (Get-FileHash -LiteralPath $replacement).Hash
Install-Shortcuts $install
if (Test-Path -LiteralPath $owned) { throw 'OwnedLegacyNotRetired' }
if ((Get-FileHash -LiteralPath $foreign).Hash -cne $foreignHash) { throw 'ForeignLegacyChanged' }
Write-Output 'PASS foreign legacy link is preserved'
$records = @(Get-ChildItem -LiteralPath (Join-Path $install 'DeploymentShortcutBackups') -Filter shortcut-backup.json -Recurse -File)
if ($records.Count -ne 2) { throw 'BackupCountMismatch' }
foreach ($entry in @(@($owned, $ownedHash), @($replacement, $replacementHash))) {
    $matched = @($records | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).originalPath -ceq $entry[0] })
    if ($matched.Count -ne 1) { throw 'BackupIdentityMissing' }
    $record = Get-Content -LiteralPath $matched[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.sha256 -cne $entry[1] -or
        (Get-FileHash -LiteralPath (Join-Path $matched[0].DirectoryName $record.backupFile)).Hash -cne $entry[1]) {
        throw 'OriginalLinkBytesNotPreserved'
    }
}
Write-Output 'PASS deleted and overwritten link bytes preserved with original paths and hashes'
$plans = @(Get-ChildItem -LiteralPath (Join-Path $install 'DeploymentShortcutTransactions') -Filter plan.json -Recurse -File)
if ($plans.Count -ne 1) { throw 'ShortcutPlanCountMismatch' }
$plan = Get-Content -LiteralPath $plans[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
if (@($plan.records).Count -ne 2 -or @($plan.records | Where-Object action -eq 'Publish').Count -ne 1 -or
    @($plan.records | Where-Object action -eq 'Retire').Count -ne 1) { throw 'ShortcutPlanScopeMismatch' }
foreach ($item in $plan.records) {
    $snapshot = Get-Content -LiteralPath $item.manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($snapshot.transactionId -cne $plan.transactionId -or $snapshot.originalPath -cne $item.path) {
        throw 'ShortcutTransactionBindingMismatch'
    }
}
Write-Output 'PASS publish and retire evidence bound to one durable prepared transaction'
$published = Get-Content -LiteralPath (Join-Path $plans[0].DirectoryName 'published.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($published.state -cne 'ShortcutsPublished' -or $published.transactionId -cne $plan.transactionId -or
    $published.recordCount -ne 2 -or $published.planSha256 -cne (Get-FileHash -LiteralPath $plans[0].FullName).Hash) {
    throw 'ShortcutCompletionEvidenceInvalid'
}
Write-Output 'PASS group completion binds validated entries to exact prepared plan'
$firstBackup = Join-Path (Split-Path -Parent $plan.records[0].manifest) 'original.lnk'
$savedBackup = [IO.File]::ReadAllBytes($firstBackup)
try {
    [IO.File]::WriteAllBytes($firstBackup, [byte[]]@(1, 2, 3))
    try { Complete-ShortcutTransaction $install $plans[0].FullName; throw 'ExpectedBackupCorruptionRejection' }
    catch { if ($_.Exception.Message -notlike 'ShortcutRollbackBackupInvalid:*') { throw } }
} finally { [IO.File]::WriteAllBytes($firstBackup, $savedBackup) }
Write-Output 'PASS damaged rollback backup prevents publication confirmation'
$savedPlan = [IO.File]::ReadAllBytes($plans[0].FullName)
$script:shortcutPlanMutationPath = $plans[0].FullName
$script:shortcutPlanMutationPending = $true
function Get-FileHash {
    param([string]$LiteralPath, [string]$Algorithm = 'SHA256')
    if ($script:shortcutPlanMutationPending -and $LiteralPath -ceq $script:shortcutPlanMutationPath) {
        $script:shortcutPlanMutationPending = $false
        [IO.File]::AppendAllText($LiteralPath, ' ')
    }
    Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $LiteralPath -Algorithm $Algorithm
}
try {
    try { Complete-ShortcutTransaction $install $plans[0].FullName; throw 'ExpectedPlanMutationRejection' }
    catch { if ($_.Exception.Message -cne 'ShortcutPlanChangedDuringCompletion') { throw } }
} finally {
    $script:shortcutPlanMutationPending = $false
    [IO.File]::WriteAllBytes($plans[0].FullName, $savedPlan)
}
Write-Output 'PASS plan mutation during validation cannot become the committed plan hash'
$actual = $shell.CreateShortcut($replacement)
if ($actual.TargetPath -ine (Join-Path $current 'MTTFTest.Watchdog.exe') -or
    $actual.WorkingDirectory -ine $current -or $actual.Arguments -notlike '--launch-main --main-executable *') {
    throw 'NewLinkBindingInvalid'
}
Write-Output 'PASS new link binds Supervisor, main and working directory'
$firstCreatedBytes = [IO.File]::ReadAllBytes($replacement)
Remove-Shortcuts $install
if ((Test-Path -LiteralPath $replacement) -or (Get-FileHash -LiteralPath $foreign).Hash -cne $foreignHash) {
    throw 'UninstallShortcutOwnershipViolation'
}
Write-Output 'PASS uninstall removes owned entry and preserves unrelated legacy entry'
Install-Shortcuts $install
$absenceRecords = @(Get-ChildItem -LiteralPath (Join-Path $install 'DeploymentShortcutBackups') -Filter shortcut-backup.json -Recurse -File |
    Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).originalExists -eq $false })
if ($absenceRecords.Count -ne 1) { throw 'MissingOriginalAbsenceRecord' }
$absence = Get-Content -LiteralPath $absenceRecords[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
$created = Get-Content -LiteralPath (Join-Path $absenceRecords[0].DirectoryName 'created-link.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($absence.originalPath -cne $replacement -or $null -ne $absence.backupFile -or
    $created.path -cne $replacement -or $created.sha256 -cne (Get-FileHash -LiteralPath $replacement).Hash) {
    throw 'CreatedLinkEvidenceInvalid'
}
Write-Output 'PASS previously absent link records absence and exact created bytes'
$secondCreatedBytes = [IO.File]::ReadAllBytes($replacement)
$changeManifest = Backup-ShortcutBeforeChange $replacement $install
Assert-ShortcutUnchangedSinceBackup $replacement $changeManifest
New-TestLink $replacement (Join-Path $fixture 'user-changed.exe')
$changedHash = (Get-FileHash -LiteralPath $replacement).Hash
try { Assert-ShortcutUnchangedSinceBackup $replacement $changeManifest; throw 'ExpectedChangedLinkRejection' }
catch { if ($_.Exception.Message -notlike 'ShortcutChangedAfterBackup:*') { throw } }
try { Assert-ShortcutUnchangedSinceBackup $replacement $absenceRecords[0].FullName; throw 'ExpectedAppearedLinkRejection' }
catch { if ($_.Exception.Message -notlike 'ShortcutAppearedAfterBackup:*') { throw } }
if ((Get-FileHash -LiteralPath $replacement).Hash -cne $changedHash) { throw 'UserChangeNotPreserved' }
try { Complete-ShortcutTransaction $install $plans[0].FullName; throw 'ExpectedCompletionConflict' }
catch { if ($_.Exception.Message -notlike 'ShortcutPublicationChanged:*') { throw } }
Write-Output 'PASS changed or newly appeared entry rejects mutation and preserves bytes'
if (@(Get-ChildItem -LiteralPath $desktop -Filter '.epb-shortcut-stage-*.lnk' -File).Count -ne 0) {
    throw 'SuccessfulPublicationLeftStagedLinks'
}
Write-Output 'PASS successful replacement and creation consume staged links'
try { Restore-ShortcutTransaction $install $plans[0].FullName @($replacement, $owned); throw 'ExpectedRollbackConflict' }
catch { if ($_.Exception.Message -cne 'ShortcutRollbackStateConflict') { throw } }
if ((Test-Path -LiteralPath $owned) -or (Get-FileHash -LiteralPath $replacement).Hash -cne $changedHash) {
    throw 'RollbackConflictMutatedGroup'
}
Write-Output 'PASS rollback conflict preserves modified link and leaves whole group untouched'
[IO.File]::WriteAllBytes($replacement, $firstCreatedBytes)
try { Restore-ShortcutTransaction $install $plans[0].FullName @($replacement); throw 'ExpectedRollbackScopeRejection' }
catch { if ($_.Exception.Message -cne 'ShortcutRollbackTargetNotAllowed') { throw } }
if (Test-Path -LiteralPath $owned) { throw 'RollbackScopeRejectionMutatedGroup' }
Write-Output 'PASS rollback rejects targets outside explicit allowed paths'
Restore-ShortcutTransaction $install $plans[0].FullName @($replacement, $owned)
if ((Get-FileHash -LiteralPath $replacement).Hash -cne $replacementHash -or
    (Get-FileHash -LiteralPath $owned).Hash -cne $ownedHash) { throw 'RollbackOriginalBytesMismatch' }
$quarantines = @(Get-ChildItem -LiteralPath $desktop -Filter '.epb-shortcut-rollback-*.lnk' -File)
if ($quarantines.Count -ne 1) { throw 'RollbackReplacementNotRetained' }
Restore-ShortcutTransaction $install $plans[0].FullName @($replacement, $owned)
if (@(Get-ChildItem -LiteralPath $desktop -Filter '.epb-shortcut-rollback-*.lnk' -File).Count -ne 1) { throw 'RollbackNotIdempotent' }
Write-Output 'PASS rollback restores replaced and retired originals and repeated rollback is unchanged'
$absencePlan = Join-Path $install ('DeploymentShortcutTransactions\' + $absence.transactionId + '\plan.json')
[IO.File]::WriteAllBytes($replacement, $secondCreatedBytes)
Restore-ShortcutTransaction $install $absencePlan @($replacement)
if (Test-Path -LiteralPath $replacement) { throw 'RollbackNewEntryStillPublished' }
$retained = @(Get-ChildItem -LiteralPath $desktop -Filter '.epb-shortcut-rollback-*.lnk' -File |
    Where-Object { (Get-FileHash -LiteralPath $_.FullName).Hash -ceq $created.sha256 })
if ($retained.Count -lt 1 -or (Get-FileHash -LiteralPath $foreign).Hash -cne $foreignHash) { throw 'RollbackNewEntryNotPreserved' }
Write-Output 'PASS rollback quarantines newly created entry without deleting bytes or changing foreign entry'
$completeImplementation = (Get-Command Complete-ShortcutTransaction).ScriptBlock
function Complete-ShortcutTransaction([string]$Root, [string]$PlanPath) {
    & $completeImplementation $Root $PlanPath
    throw 'InjectedFailureAfterShortcutPublication'
}
try { Install-Shortcuts $install; throw 'ExpectedInstallationPublicationFailure' }
catch { if ($_.Exception.Message -cne 'InjectedFailureAfterShortcutPublication') { throw } }
if ((Test-Path -LiteralPath $replacement) -or (Get-FileHash -LiteralPath $owned).Hash -cne $ownedHash -or
    (Get-FileHash -LiteralPath $foreign).Hash -cne $foreignHash) { throw 'AutomaticShortcutRollbackFailed' }
Write-Output 'PASS installation publication failure restores old entry and retracts new entry automatically'
function Complete-ShortcutTransaction([string]$Root, [string]$PlanPath) { & $completeImplementation $Root $PlanPath }
function Write-CreatedShortcutReceipt([string]$Path, [string]$Manifest) {
    $prepared = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $Manifest) 'prepared-link.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($prepared.sha256 -cne (Get-FileHash -LiteralPath $Path).Hash) { throw 'PreparedEvidenceNotPublishedBytes' }
    throw 'InjectedFailureBeforeCreatedReceipt'
}
try { Install-Shortcuts $install; throw 'ExpectedPreReceiptFailure' }
catch { if ($_.Exception.Message -cne 'InjectedFailureBeforeCreatedReceipt') { throw } }
if ((Test-Path -LiteralPath $replacement) -or (Get-FileHash -LiteralPath $owned).Hash -cne $ownedHash) {
    throw 'PreReceiptRollbackFailed'
}
Write-Output 'PASS prepared evidence permits rollback after publication before created receipt'
Write-Output "PASS 19/19; FixtureDirectory=$fixture; no real desktop or installation actions"
