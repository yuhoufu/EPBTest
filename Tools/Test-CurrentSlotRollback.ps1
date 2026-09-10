# Isolated directory transaction tests. Never execute the installer entry point.
param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$parseTokens = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'InstallerParseFailed' }
foreach ($name in @('Install-CurrentSlot', 'Mark-CurrentSlotPublicationRolledBack',
        'Set-CurrentSlotJournalPhase',
        'Assert-CurrentSlotTransactionReference', 'Complete-CurrentSlotTransaction',
        'Restore-CurrentSlotTransaction', 'Resolve-SafeDirectory', 'Read-Utf8JsonFile',
        'Backup-PendingInstallJournal', 'Get-DeploymentFileSha256')) {
    $found = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true))
    if ($found.Count -ne 1) { throw "FunctionMissingOrAmbiguous:$name" }
    . ([scriptblock]::Create($found[0].Extent.Text))
}
# Synthetic identity stubs: these are not package acceptance or deployment evidence.
function Get-VerifiedDeploymentIdentity([string]$Directory) {
    if ($script:failStagingVerification -and [IO.Path]::GetFileName($Directory).StartsWith('.current-staging-')) {
        throw 'InjectedStagingVerificationFailure'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'fixture.txt') -PathType Leaf)) { throw 'FixtureMissing' }
}
function Assert-InstalledPackageMatchesSource([string]$Source, [string]$Root) {
    if ($script:hideRetiredBeforeRollback) {
        $old = @(Get-ChildItem -LiteralPath $Root -Directory -Filter '.retired-*')
        if ($old.Count -ne 1) { throw 'FixtureRetiredMissing' }
        [IO.Directory]::Move($old[0].FullName, (Join-Path $Root 'held-old-evidence'))
        throw 'InjectedPublishedSlotVerificationFailure'
    }
    if ($script:failSlotVerification) { throw 'InjectedPublishedSlotVerificationFailure' }
    if ((Get-Content -LiteralPath (Join-Path $Root 'Current\fixture.txt') -Raw) -ne 'new') { throw 'WrongPublishedFixture' }
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('EpbCurrentSlotTests-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $fixture)
$passed = 0
foreach ($variant in @('upgrade-failure', 'first-install-failure', 'success')) {
    $caseRoot = Join-Path $fixture $variant
    $source = Join-Path $caseRoot 'Source'
    $install = Join-Path $caseRoot 'Install'
    [void](New-Item -ItemType Directory -Path $source, $install)
    [void](New-Item -ItemType File -Path (Join-Path $source 'fixture.txt') -Value 'new')
    if ($variant -eq 'upgrade-failure') {
        [void](New-Item -ItemType Directory -Path (Join-Path $install 'Current'))
        [void](New-Item -ItemType File -Path (Join-Path $install 'Current\fixture.txt') -Value 'old')
    }
    $script:failSlotVerification = $variant -ne 'success'
    $rejected = $false
    try { Install-CurrentSlot $source $install }
    catch {
        if ($_.Exception.Message -notlike '*InjectedPublishedSlotVerificationFailure*') { throw }
        $rejected = $true
    }
    if ($rejected -ne $script:failSlotVerification) { throw "WrongOutcome:$variant" }
    $failed = @(Get-ChildItem -LiteralPath $install -Directory -Filter '.failed-current-*')
    if ($script:failSlotVerification) {
        if ($failed.Count -ne 1 -or (Get-Content -LiteralPath (Join-Path $failed[0].FullName 'fixture.txt') -Raw) -ne 'new') { throw 'FailedSlotNotPreserved' }
        if (-not (Test-Path -LiteralPath (Join-Path $install 'install-transaction.json'))) { throw 'FailureJournalLost' }
        if ($variant -eq 'upgrade-failure') {
            if ((Get-Content -LiteralPath (Join-Path $install 'Current\fixture.txt') -Raw) -ne 'old') { throw 'OldSlotNotRestored' }
        } elseif (Test-Path -LiteralPath (Join-Path $install 'Current')) { throw 'FailedFirstInstallStillCurrent' }
    } else {
        if ($failed.Count -ne 0 -or (Test-Path -LiteralPath (Join-Path $install 'install-transaction.json'))) { throw 'SuccessfulTransactionNotCompleted' }
    }
    if ((Get-Content -LiteralPath (Join-Path $source 'fixture.txt') -Raw) -ne 'new') { throw 'SourceChanged' }
    $passed++
    Write-Output "PASS $variant"
    if ($variant -ne 'success') {
        $previousHash = (Get-FileHash -LiteralPath (Join-Path $install 'install-transaction.json')).Hash
        $script:failSlotVerification = $false
        Install-CurrentSlot $source $install
        $history = @(Get-ChildItem -LiteralPath $install -File -Filter '.install-transaction-history-*.json')
        if ($history.Count -ne 1 -or (Get-FileHash -LiteralPath $history[0].FullName).Hash -cne $previousHash) { throw 'PriorJournalEvidenceLost' }
        $replaced = @(Get-ChildItem -LiteralPath $install -File -Filter '.install-journal-replaced-*.json')
        if ($replaced.Count -ne 1 -or (Get-FileHash -LiteralPath $replaced[0].FullName).Hash -cne $previousHash) {
            throw 'AtomicReplacementLostOriginalJournal'
        }
        if (@(Get-ChildItem -LiteralPath $install -File -Filter '.install-journal-pending-*').Count -ne 0) {
            throw 'SuccessfulJournalPublicationLeftPendingFile'
        }
        if ((Get-Content -LiteralPath (Join-Path $install 'Current\fixture.txt') -Raw) -ne 'new') { throw 'RetryDidNotPublish' }
        if (@(Get-ChildItem -LiteralPath $install -Directory -Filter '.failed-current-*').Count -ne 1) { throw 'RetryLostFailedSlot' }
        $passed++
        Write-Output "PASS $variant retry preserves exact journal and failed slot"
    }
}

$deferredRoot = Join-Path $fixture 'deferred-install-transaction'
$deferredSource = Join-Path $deferredRoot 'Source'
$deferredInstall = Join-Path $deferredRoot 'Install'
[void](New-Item -ItemType Directory -Path $deferredSource, (Join-Path $deferredInstall 'Current') -Force)
[IO.File]::WriteAllText((Join-Path $deferredSource 'fixture.txt'), 'new')
[IO.File]::WriteAllText((Join-Path $deferredInstall 'Current\fixture.txt'), 'old')
$script:failSlotVerification = $false
$deferred = Install-CurrentSlot $deferredSource $deferredInstall -DeferCommit
if ((Get-Content (Join-Path $deferredInstall 'Current\fixture.txt') -Raw) -ne 'new' -or `
    -not (Test-Path -LiteralPath $deferred.Journal) -or `
    -not (Test-Path -LiteralPath $deferred.Retired)) { throw 'DeferredCurrentPublicationMissing' }
[void](Restore-CurrentSlotTransaction $deferred $deferredInstall)
if ((Get-Content (Join-Path $deferredInstall 'Current\fixture.txt') -Raw) -ne 'old' -or `
    (Test-Path -LiteralPath $deferred.Journal) -or `
    @(Get-ChildItem $deferredInstall -Directory -Filter '.failed-current-*').Count -ne 1) {
    throw 'DeferredCurrentRollbackIncomplete'
}
Write-Output 'PASS deferred Current publication rolls back after later install failure'
$passed++

$deferredCommit = Install-CurrentSlot $deferredSource $deferredInstall -DeferCommit
$completion = Complete-CurrentSlotTransaction $deferredCommit $deferredInstall
if ((Get-Content (Join-Path $deferredInstall 'Current\fixture.txt') -Raw) -ne 'new' -or `
    (Test-Path -LiteralPath $deferredCommit.Journal) -or `
    -not (Test-Path -LiteralPath $completion -PathType Leaf)) {
    throw 'DeferredCurrentCommitIncomplete'
}
Write-Output 'PASS deferred Current publication commits only after outer transaction success'
$passed++
$invalidRoot = Join-Path $fixture 'invalid-journal'
$invalidSource = Join-Path $invalidRoot 'Source'
$invalidInstall = Join-Path $invalidRoot 'Install'
[void](New-Item -ItemType Directory -Path $invalidSource, $invalidInstall)
[void](New-Item -ItemType File -Path (Join-Path $invalidSource 'fixture.txt') -Value 'new')
$invalidJournal = Join-Path $invalidInstall 'install-transaction.json'
$invalidBody = @{ current = (Join-Path $fixture 'UnrelatedCurrent'); retired = (Join-Path $invalidInstall '.retired-fixture') } | ConvertTo-Json
[void](New-Item -ItemType File -Path $invalidJournal -Value $invalidBody)
$journalHash = (Get-FileHash -LiteralPath $invalidJournal).Hash
$blocked = $false
try { Install-CurrentSlot $invalidSource $invalidInstall }
catch { if ($_.Exception.Message -ne '换包恢复路径越界。') { throw }; $blocked = $true }
if (-not $blocked -or (Get-FileHash -LiteralPath $invalidJournal).Hash -cne $journalHash -or
    @(Get-ChildItem -LiteralPath $invalidInstall -Force).Count -ne 1) { throw 'InvalidJournalWasMutated' }
$passed++
Write-Output 'PASS mismatched Current journal rejected without mutations'
foreach ($targetKind in @('File', 'Junction')) {
    $caseRoot = Join-Path $fixture ('invalid-retired-' + $targetKind)
    $source = Join-Path $caseRoot 'Source'
    $install = Join-Path $caseRoot 'Install'
    $untouched = Join-Path $caseRoot 'Untouched'
    [void](New-Item -ItemType Directory -Path $source, $install, $untouched)
    [void](New-Item -ItemType File -Path (Join-Path $source 'fixture.txt') -Value 'new')
    $untouchedFile = Join-Path $untouched 'keep.txt'
    [void](New-Item -ItemType File -Path $untouchedFile -Value 'unchanged')
    $retiredTarget = Join-Path $install '.retired-fixture'
    if ($targetKind -eq 'File') {
        [void](New-Item -ItemType File -Path $retiredTarget -Value 'not-a-directory')
    } else {
        [void](New-Item -ItemType Junction -Path $retiredTarget -Target $untouched)
    }
    $journal = Join-Path $install 'install-transaction.json'
    $body = @{ current = (Join-Path $install 'Current'); retired = $retiredTarget } | ConvertTo-Json
    [void](New-Item -ItemType File -Path $journal -Value $body)
    $before = (Get-FileHash -LiteralPath $journal).Hash
    $blocked = $false
    try { Install-CurrentSlot $source $install }
    catch { if ($_.Exception.Message -ne 'RetiredRecoveryTargetInvalid') { throw }; $blocked = $true }
    if (-not $blocked -or (Get-FileHash -LiteralPath $journal).Hash -cne $before -or
        (Get-Content -LiteralPath $untouchedFile -Raw) -ne 'unchanged' -or
        (Test-Path -LiteralPath (Join-Path $install 'Current')) -or
        @(Get-ChildItem -LiteralPath $install -Force).Count -ne 2) { throw "RetiredRejectionMutatedState:$targetKind" }
    $passed++
    Write-Output "PASS retired $targetKind rejected with journal and target preserved"
}
$caseRoot = Join-Path $fixture 'staging-verification-failure'
$source = Join-Path $caseRoot 'Source'
$install = Join-Path $caseRoot 'Install'
[void](New-Item -ItemType Directory -Path $source, (Join-Path $install 'Current'))
[void](New-Item -ItemType File -Path (Join-Path $source 'fixture.txt') -Value 'new')
[void](New-Item -ItemType File -Path (Join-Path $install 'Current\fixture.txt') -Value 'old')
$script:failStagingVerification = $true
$blocked = $false
try { Install-CurrentSlot $source $install }
catch { if ($_.Exception.Message -ne 'InjectedStagingVerificationFailure') { throw }; $blocked = $true }
finally { $script:failStagingVerification = $false }
$pending = @(Get-ChildItem -LiteralPath $install -Directory -Filter '.current-staging-*')
if (-not $blocked -or $pending.Count -ne 1 -or
    (Get-Content -LiteralPath (Join-Path $pending[0].FullName 'fixture.txt') -Raw) -ne 'new' -or
    (Get-Content -LiteralPath (Join-Path $install 'Current\fixture.txt') -Raw) -ne 'old' -or
    (Test-Path -LiteralPath (Join-Path $install 'install-transaction.json')) -or
    @(Get-ChildItem -LiteralPath $install -Directory -Filter '.retired-*').Count -ne 0) {
    throw 'StagingFailureChangedOldSlotOrLostEvidence'
}
$passed++
Write-Output 'PASS staging rejection preserves candidate and leaves old slot untouched'
foreach ($kind in @('file', 'junction')) {
    $caseRoot = Join-Path $fixture ("invalid-current-$kind")
    $source = Join-Path $caseRoot 'Source'
    $install = Join-Path $caseRoot 'Install'
    [void](New-Item -ItemType Directory -Path $source, $install)
    [void](New-Item -ItemType File -Path (Join-Path $source 'fixture.txt') -Value 'new')
    $current = Join-Path $install 'Current'
    if ($kind -eq 'file') {
        [void](New-Item -ItemType File -Path $current -Value 'old-file')
    } else {
        $target = Join-Path $caseRoot 'LinkedOld'
        [void](New-Item -ItemType Directory -Path $target)
        [void](New-Item -ItemType File -Path (Join-Path $target 'fixture.txt') -Value 'old-linked')
        [void](New-Item -ItemType Junction -Path $current -Target $target)
    }
    $blocked = $false
    try { Install-CurrentSlot $source $install }
    catch { if ($_.Exception.Message -ne 'CurrentOriginalTargetInvalid') { throw }; $blocked = $true }
    if (-not $blocked -or @(Get-ChildItem -LiteralPath $install -Directory -Filter '.retired-*').Count -ne 0) {
        throw 'InvalidCurrentWasRetired'
    }
    if ($kind -eq 'file') {
        if ((Get-Content -LiteralPath $current -Raw) -ne 'old-file') { throw 'OriginalFileChanged' }
    } else {
        if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or
            (Get-Content -LiteralPath (Join-Path $target 'fixture.txt') -Raw) -ne 'old-linked') {
            throw 'OriginalJunctionOrTargetChanged'
        }
    }
    $pending = @(Get-ChildItem -LiteralPath $install -Directory -Filter '.current-staging-*')
    if ($pending.Count -ne 1 -or (Get-Content -LiteralPath (Join-Path $pending[0].FullName 'fixture.txt') -Raw) -ne 'new' -or
        -not (Test-Path -LiteralPath (Join-Path $install 'install-transaction.json'))) {
        throw 'InvalidCurrentLostCandidateOrJournal'
    }
    $passed++
    Write-Output "PASS invalid Current $kind rejected with evidence preserved"
}
$caseRoot = Join-Path $fixture 'retired-missing'
$source = Join-Path $caseRoot 'Source'
$install = Join-Path $caseRoot 'Install'
[void](New-Item -ItemType Directory -Path $source, (Join-Path $install 'Current'))
[void](New-Item -ItemType File -Path (Join-Path $source 'fixture.txt') -Value 'new')
[void](New-Item -ItemType File -Path (Join-Path $install 'Current\fixture.txt') -Value 'old')
$script:hideRetiredBeforeRollback = $true
$caught = $null
try { Install-CurrentSlot $source $install }
catch { $caught = $_.Exception.Message }
finally { $script:hideRetiredBeforeRollback = $false }
$failed = @(Get-ChildItem -LiteralPath $install -Directory -Filter '.failed-current-*')
if ($caught -notlike '*Publication=InjectedPublishedSlotVerificationFailure*Rollback=CurrentRetiredMissing*' -or
    (Test-Path -LiteralPath (Join-Path $install 'Current')) -or $failed.Count -ne 1 -or
    (Get-Content -LiteralPath (Join-Path $failed[0].FullName 'fixture.txt') -Raw) -ne 'new' -or
    (Get-Content -LiteralPath (Join-Path $install 'held-old-evidence\fixture.txt') -Raw) -ne 'old' -or
    -not (Test-Path -LiteralPath (Join-Path $install 'install-transaction.json'))) {
    throw "RetiredMissingWasNotSafelyReported:$caught"
}
$passed++
Write-Output 'PASS missing retired reports both errors and preserves all evidence'
& {
    param($fixture)
    $caseRoot = Join-Path $fixture 'rollback-occupied'
    $source = Join-Path $caseRoot 'Source'
    $install = Join-Path $caseRoot 'Install'
    [void](New-Item -ItemType Directory -Path $source, (Join-Path $install 'Current'))
    [void](New-Item -ItemType File -Path (Join-Path $source 'fixture.txt') -Value 'new')
    [void](New-Item -ItemType File -Path (Join-Path $install 'Current\fixture.txt') -Value 'old')
    function Write-Warning {
        param([string]$Message)
        if ($Message -like '*新程序槽校验失败*') {
            [void][IO.Directory]::CreateDirectory((Join-Path $install 'Current'))
            [IO.File]::WriteAllText((Join-Path $install 'Current\fixture.txt'), 'external')
        }
    }
    $script:failSlotVerification = $true
    $caught = $null
    try { Install-CurrentSlot $source $install }
    catch { $caught = $_.Exception.Message }
    finally { $script:failSlotVerification = $false }
    $old = @(Get-ChildItem -LiteralPath $install -Directory -Filter '.retired-*')
    $failed = @(Get-ChildItem -LiteralPath $install -Directory -Filter '.failed-current-*')
    if ($caught -notlike '*Publication=InjectedPublishedSlotVerificationFailure*Rollback=CurrentRollbackDestinationOccupied*' -or
        $old.Count -ne 1 -or $failed.Count -ne 1 -or
        (Get-Content -LiteralPath (Join-Path $install 'Current\fixture.txt') -Raw) -ne 'external' -or
        (Get-Content -LiteralPath (Join-Path $old[0].FullName 'fixture.txt') -Raw) -ne 'old' -or
        (Get-Content -LiteralPath (Join-Path $failed[0].FullName 'fixture.txt') -Raw) -ne 'new' -or
        -not (Test-Path -LiteralPath (Join-Path $install 'install-transaction.json'))) {
        throw "OccupiedRollbackLostEvidence:$caught"
    }
    Write-Output 'PASS occupied rollback preserves unrelated content and both slots'
} $fixture
$passed++
$caseRoot = Join-Path $fixture 'resume-missing-current'
$source = Join-Path $caseRoot 'Source'
$install = Join-Path $caseRoot 'Install'
$saved = Join-Path $install '.retired-interrupted'
[void](New-Item -ItemType Directory -Path $source, $saved)
[void](New-Item -ItemType File -Path (Join-Path $source 'fixture.txt') -Value 'new')
[void](New-Item -ItemType File -Path (Join-Path $saved 'fixture.txt') -Value 'old')
$journal = Join-Path $install 'install-transaction.json'
$body = @{ retired = $saved; current = (Join-Path $install 'Current') } | ConvertTo-Json
[IO.File]::WriteAllText($journal, $body, [Text.Encoding]::UTF8)
$before = (Get-FileHash -LiteralPath $journal).Hash
$script:failStagingVerification = $true
$caught = $null
try { Install-CurrentSlot $source $install }
catch { $caught = $_.Exception.Message }
finally { $script:failStagingVerification = $false }
if ($caught -ne 'InjectedStagingVerificationFailure' -or
    (Get-Content -LiteralPath (Join-Path $install 'Current\fixture.txt') -Raw) -ne 'old' -or
    (Test-Path -LiteralPath $saved) -or (Get-FileHash -LiteralPath $journal).Hash -cne $before) {
    throw "InterruptedSlotWasNotRestored:$caught"
}
$passed++
Write-Output 'PASS missing Current restored from pending journal before new candidate validation'
Write-Output "PASS $passed/16; SyntheticOnly=true; InstallationPerformed=false; PreservedFixture=$fixture"
