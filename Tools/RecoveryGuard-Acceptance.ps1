#requires -Version 5.1
# Pure validation functions. Loading this file performs no installation or device action.
function Assert-GuardAcceptanceReport {
    param(
        [Parameter(Mandatory=$true)]$Report,
        [Parameter(Mandatory=$true)][ValidateSet('RecoverExited','RecoverStalled')][string]$RequestedMode,
        [Parameter(Mandatory=$true)][string]$MainIdentitySha256,
        [Parameter(Mandatory=$true)][string]$GuardExecutableSha256,
        [Parameter(Mandatory=$true)][string]$RecoveryControlSha256,
        [Parameter(Mandatory=$true)][string]$GuardPackageIdentitySha256,
        [datetime]$NowUtc = [DateTime]::UtcNow
    )
    foreach ($hash in @($MainIdentitySha256, $GuardExecutableSha256, $RecoveryControlSha256, $GuardPackageIdentitySha256)) {
        if ($hash -notmatch '^[0-9a-fA-F]{64}$') { throw 'AcceptanceExpectedHashInvalid' }
    }
    if ($Report.schemaVersion -cne 1 -or $Report.kind -cne 'RecoveryGuardFieldAcceptance' -or
        $Report.testOnly -isnot [bool] -or $Report.testOnly -ne $false -or
        $Report.passed -isnot [bool] -or $Report.passed -ne $true -or
        $Report.physicalHardwareSafetyVerified -isnot [bool] -or $Report.physicalHardwareSafetyVerified -ne $true) {
        throw 'AcceptanceRequiresPassedPhysicalFieldReport'
    }
    if ($Report.productVersion -cne '3.0.0.0' -or
        [string]::IsNullOrWhiteSpace([string]$Report.benchId) -or
        [string]::IsNullOrWhiteSpace([string]$Report.machineName) -or
        [string]::IsNullOrWhiteSpace([string]$Report.operatorName)) { throw 'AcceptanceScopeMissing' }
    $completed = [DateTimeOffset]::MinValue
    if ($Report.completedUtc -is [datetime]) {
        # PowerShell 7 JSON readers may materialize a Z timestamp as DateTime.
        if ($Report.completedUtc.Kind -ne [DateTimeKind]::Utc) { throw 'AcceptanceTimestampMustBeUtc' }
        $completed = [DateTimeOffset]::new($Report.completedUtc)
    } else {
        if ([string]$Report.completedUtc -notmatch 'Z$') { throw 'AcceptanceTimestampMustBeUtc' }
        if (-not [DateTimeOffset]::TryParse([string]$Report.completedUtc,
                [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$completed)) {
            throw 'AcceptanceTimestampInvalid'
        }
    }
    if ($completed.UtcDateTime -gt $NowUtc.ToUniversalTime()) { throw 'AcceptanceTimestampInvalid' }
    # A report for a different binary set must never promote this package.
    foreach ($binding in @(
        @('mainIdentitySha256', $MainIdentitySha256),
        @('guardExecutableSha256', $GuardExecutableSha256),
        @('guardPackageIdentitySha256', $GuardPackageIdentitySha256),
        @('recoveryControlSha256', $RecoveryControlSha256))) {
        $actual = [string]$Report.($binding[0])
        if ($actual -notmatch '^[0-9a-fA-F]{64}$' -or $actual -ine $binding[1]) { throw ('AcceptanceBindingMismatch:' + $binding[0]) }
    }
    $modes = @($Report.approvedModes)
    if ($modes.Count -lt 1 -or @($modes | Sort-Object -Unique).Count -ne $modes.Count -or
        @($modes | Where-Object { $_ -cnotin @('RecoverExited','RecoverStalled') }).Count -ne 0 -or
        'RecoverExited' -cnotin $modes -or $RequestedMode -cnotin $modes) { throw 'AcceptanceModeNotApproved' }
    $required = @('independence', 'oldState', 'primaryBackupConflict', 'snapshotFreshness',
        'operatorPriority', 'persistenceFailure', 'singleInstance', 'sessionRollover',
        'repeatedFailures', 'cooldownAndExpiry', 'powerLossBoundary', 'panelStop',
        'transactionInterruption', 'coordinatorLoss', 'maintenanceAndClock', 'persistentBudget',
        'dataReconciliation', 'businessRecovery', 'exitedRecovery', 'physicalSafety')
    if ('RecoverStalled' -cin $modes) { $required += 'stalledRecovery' }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($check in @($Report.checks)) {
        if ($null -eq $check -or -not $seen.Add([string]$check.id) -or $check.id -cnotin $required -or
            $check.passed -isnot [bool] -or $check.passed -ne $true -or
            [string]::IsNullOrWhiteSpace([string]$check.evidencePath) -or
            [string]$check.evidenceSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'AcceptanceCheckInvalid' }
    }
    if ($seen.Count -ne $required.Count) { throw 'AcceptanceChecksIncomplete' }
    return [pscustomobject]@{ Mode=$RequestedMode; BenchId=[string]$Report.benchId;
        MachineName=[string]$Report.machineName; CompletedUtc=$completed.UtcDateTime; Checks=$seen.Count }
}

function Assert-GuardAcceptanceEvidenceFiles {
    param([Parameter(Mandatory=$true)]$Report, [Parameter(Mandatory=$true)][string]$EvidenceDirectory)
    $root = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\','/')
    foreach ($check in @($Report.checks)) {
        $relative = [string]$check.evidencePath
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
            $relative -match '(^|[\\/])\.\.([\\/]|$)' -or $relative.Contains(':')) { throw 'AcceptanceEvidencePathInvalid' }
        $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'AcceptanceEvidenceMissing' }
        # Reject links anywhere in the path, rather than hashing a file outside the evidence root.
        $item = Get-Item -LiteralPath $path -Force
        while ($null -ne $item) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'AcceptanceEvidenceLinkRejected' }
            if ($item.FullName.TrimEnd('\') -ieq $root) { break }
            $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine [string]$check.evidenceSha256) { throw 'AcceptanceEvidenceHashMismatch' }
    }
}
