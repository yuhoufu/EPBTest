#requires -Version 5.1
# Pure validation only. The caller must read the live checkpoint, verify the
# installed image and process exit, and hold deployment exclusion before use.
function Convert-LegacyEvidenceUtc($Value) {
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}
function Assert-LegacyStopEvidence {
    param(
        [Parameter(Mandatory=$true)]$Checkpoint,
        [Parameter(Mandatory=$true)]$Receipt,
        [Parameter(Mandatory=$true)]$Session,
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)][string]$ExpectedExecutablePath,
        [Parameter(Mandatory=$true)][string]$ExpectedExecutableSha256,
        [Parameter(Mandatory=$true)][bool]$ProcessExited,
        [datetime]$NowUtc = [DateTime]::UtcNow
    )
    if (-not $ProcessExited) { throw 'LegacyStopProcessExitUnproven' }
    if ($Checkpoint.SchemaVersion -notin @(5,6) -or
        $Checkpoint.Armed -isnot [bool] -or $Checkpoint.Armed -or
        $Checkpoint.GracefulPaused -isnot [bool] -or $Checkpoint.GracefulPaused) {
        throw 'LegacyStopCheckpointNotStopped'
    }
    if ($ExpectedExecutableSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $Checkpoint.ExecutableSha256 -ine $ExpectedExecutableSha256 -or
        [string]::IsNullOrWhiteSpace([string]$Session.ExecutablePath) -or
        [IO.Path]::GetFullPath([string]$Session.ExecutablePath) -ine [IO.Path]::GetFullPath($ExpectedExecutablePath)) {
        throw 'LegacyStopExecutableMismatch'
    }
    foreach ($id in @($Request.RunId,$Receipt.SessionId,$Receipt.StopSafetyTransactionId)) {
        if ([string]$id -notmatch '^[0-9a-fA-F]{32}$') { throw 'LegacyStopIdentityMissing' }
    }
    $active = $Session.LastVerifiedActiveRun
    if ($Receipt.SessionId -cne $Session.SessionId -or $Receipt.StopRunId -cne $Checkpoint.RunId -or
        $Receipt.StopRunId -cne $Request.RunId -or $Receipt.StopRunId -cne $Session.RunId -or
        $Receipt.StopRunId -cne $active.RunId -or $Receipt.StopRunEpoch -ne $Checkpoint.RunEpoch -or
        $Receipt.StopRunEpoch -ne $active.RunEpoch -or [long]$Receipt.StopRunEpoch -lt 1 -or
        [int]$Request.Pid -lt 1 -or [long]$Request.StartTicks -lt 1 -or
        $Request.Pid -ne $Session.CurrentPid -or $Request.StartTicks -ne $Session.CurrentProcessStartUtcTicks -or
        $Request.Pid -ne $active.ProcessId -or $Request.StartTicks -ne $active.ProcessStartUtcTicks) {
        throw 'LegacyStopIdentityMismatch'
    }
    foreach ($flag in @('FinalSafetyResultCommitted','MotorsOff','PowerOff','PressureSafe',
        'PersistenceDrained','LogicalQuiescent','DataContinuityVerified')) {
        if ($Receipt.$flag -isnot [bool] -or $Receipt.$flag -ne $true) { throw "LegacyStopIncomplete:$flag" }
    }
    if ($Receipt.SchemaVersion -notin @(3,4,5,6,7) -or $Receipt.State -ne 2 -or $Receipt.SafetyStage -ne 5 -or
        $Receipt.CloseIntent -cne 'ManualStopIntent' -or
        [long]$Receipt.StopSafetyBoundaryGeneration -lt 1 -or [long]$Receipt.StateVersion -lt 1 -or
        $Receipt.RelaunchDisposition -ne 1 -or [long]$Receipt.RelaunchPermitGeneration -ne 0 -or
        -not [string]::IsNullOrWhiteSpace([string]$Receipt.RelaunchPermitId) -or
        -not [string]::IsNullOrWhiteSpace([string]$Receipt.RelaunchPermitNonceSha256) -or
        $Session.ManualStopRequested -isnot [bool] -or -not $Session.ManualStopRequested) {
        throw 'LegacyStopNotTerminalOperatorStop'
    }
    $requested = [long]$Request.RequestUtcTicks
    $completed = [long]$Receipt.UpdatedUtcTicks
    $checkpointUtc = (Convert-LegacyEvidenceUtc $Checkpoint.UpdatedUtc).Ticks
    if ($NowUtc.Kind -ne [DateTimeKind]::Utc -or $requested -le [long]$Request.StartTicks -or
        $completed -lt $requested -or $completed -gt $NowUtc.Ticks -or
        $checkpointUtc -lt $requested -or $checkpointUtc -gt $NowUtc.Ticks -or
        ($checkpointUtc -gt $completed -and $Checkpoint.LastReason -cne 'MonitorClosing') -or
        $NowUtc.Ticks - $requested -gt [TimeSpan]::FromMinutes(10).Ticks) {
        throw 'LegacyStopEvidenceExpiredOrClockInvalid'
    }
    return [pscustomobject]@{ EvidenceKind='TerminalOperatorStop'; SessionId=$Receipt.SessionId;
        RunId=$Receipt.StopRunId; TransactionId=$Receipt.StopSafetyTransactionId;
        ProcessId=$Request.Pid; ProcessStartUtcTicks=$Request.StartTicks;
        PhysicalIsolationClaimed=$false; AuthorizationMigrated=$false }
}

# Read evidence from the current project's own journal, never from an imported
# acceptance report. Called only after the installer has stopped launch owners.
function Get-LiveLegacyStopEvidence {
    param([string]$Root, [string]$CheckpointPath, $Checkpoint, [string]$ArchiveRoot)
    $exe = Join-Path $Root 'Current\MTTFTest.exe'
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256 -ErrorAction Stop).Hash
    if (@(Get-CimInstance Win32_Process -Filter "Name='MTTFTest.exe'" -ErrorAction Stop).Count -ne 0) {
        throw 'LegacyStopProcessStillPresent'
    }
    $store = [IO.Path]::GetFullPath([string]$Checkpoint.StoreDir).TrimEnd('\') + '\'
    $project = [IO.Path]::GetFullPath((Join-Path $store ([string]$Checkpoint.TestName)))
    if (-not $project.StartsWith($store,[StringComparison]::OrdinalIgnoreCase)) { throw 'LegacyStopProjectPathInvalid' }
    $journal = Join-Path $project 'WatchdogSessions'
    $matches = @(foreach ($file in @(Get-ChildItem -LiteralPath $journal -Filter 'session-*.json' -File -ErrorAction Stop)) {
        if ($file.Name -notmatch '^session-[0-9a-fA-F]{32}\.json$') { continue }
        $record = Read-Utf8JsonFile $file.FullName 'Legacy session'
        if ($record.RunId -ceq $Checkpoint.RunId) { [pscustomobject]@{Path=$file.FullName;Record=$record} }
    })
    if ($matches.Count -ne 1) { throw 'LegacyStopSessionMissingOrAmbiguous' }
    $session = $matches[0].Record
    $receiptPath = Join-Path $journal ('session-' + $session.SessionId + '.closing.json')
    $receipt = Read-Utf8JsonFile $receiptPath 'Legacy terminal receipt'
    # The journal's last verified active observation is the freshness origin.
    # Its general UpdatedUtc may advance after Stop and must not renew evidence.
    $observed = Convert-LegacyEvidenceUtc $session.LastVerifiedActiveRun.CapturedUtc
    $request = [pscustomobject]@{RequestUtcTicks=$observed.Ticks;Pid=$session.CurrentPid;
        StartTicks=$session.CurrentProcessStartUtcTicks;RunId=$session.RunId}
    $result = Assert-LegacyStopEvidence -Checkpoint $Checkpoint -Receipt $receipt -Session $session `
        -Request $request -ExpectedExecutablePath $exe -ExpectedExecutableSha256 $hash -ProcessExited $true
    $fresh = Read-Utf8JsonFile $CheckpointPath 'Live legacy checkpoint'
    if (($fresh | ConvertTo-Json -Depth 32 -Compress) -cne ($Checkpoint | ConvertTo-Json -Depth 32 -Compress)) {
        throw 'LegacyStopCheckpointChanged'
    }
    if (@(Get-CimInstance Win32_Process -Filter "Name='MTTFTest.exe'" -ErrorAction Stop).Count -ne 0) {
        throw 'LegacyStopProcessRestarted'
    }
    Copy-Item -LiteralPath $matches[0].Path -Destination (Join-Path $ArchiveRoot 'stop-session.json') -ErrorAction Stop
    Copy-Item -LiteralPath $receiptPath -Destination (Join-Path $ArchiveRoot 'stop-terminal-receipt.json') -ErrorAction Stop
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArchiveRoot 'stop-validation.json') -Encoding UTF8
    return $result
}
