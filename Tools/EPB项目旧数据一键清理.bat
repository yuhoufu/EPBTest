@echo off
setlocal
set "EPB_CLEANUP_SELF=%~f0"
set "EPB_CLEANUP_ARGS=%*"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$c=[IO.File]::ReadAllText($env:EPB_CLEANUP_SELF);$m='#__POWERSHELL__';$i=$c.LastIndexOf($m);if($i -lt 0){exit 1};$s=$c.Substring($i+$m.Length);&([scriptblock]::Create($s)) $env:EPB_CLEANUP_ARGS;exit $LASTEXITCODE"
exit /b %errorlevel%
#__POWERSHELL__
param([string]$RawArgs)

$ErrorActionPreference = 'Stop'
$ScriptVersion = '2.12.0.24'
$ScanOnly = ($RawArgs -split '\s+') -contains '/scanonly'
$ProjectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $env:EPB_CLEANUP_SELF)).TrimEnd('\')
$LogDirectory = Join-Path $ProjectRoot 'log'
$AuditPath = Join-Path $LogDirectory ('data-cleanup-{0}.log' -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
$FailureCount = 0
$ScannedBytes = [int64]0
$PlannedBytes = [int64]0
$DeletedBytes = [int64]0
$StartedAt = Get-Date
$Stopwatch = [Diagnostics.Stopwatch]::StartNew()

function Write-Audit([string]$Message) {
    $normalizedMessage = $Message.Replace('`t', [string][char]9)
    $line = "{0}`t{1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $normalizedMessage
    [IO.File]::AppendAllText($AuditPath, $line + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Fail-Validation([string]$Message) {
    try { Write-Audit ('VALIDATION_FAILED`t' + $Message) } catch { }
    Write-Host $Message -ForegroundColor Red
    exit 1
}

function Get-Normalized([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Test-InRoot([string]$Path) {
    $full = Get-Normalized $Path
    return $full.StartsWith($ProjectRoot + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Test-HasReparsePoint([string]$Path) {
    try {
        $rootItem = Get-Item -LiteralPath $Path -Force
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
        if ($rootItem.PSIsContainer) {
            return @(Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop |
                Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0
        }
        return $false
    } catch {
        Write-Audit ('SKIPPED`tReparseCheckFailed`t{0}`t{1}' -f $Path, $_.Exception.Message)
        return $true
    }
}

function Get-TargetBytes([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return [int64](Get-Item -LiteralPath $Path).Length }
    $sum = [int64]0
    Get-ChildItem -LiteralPath $Path -File -Force -Recurse -ErrorAction Stop |
        ForEach-Object { $sum += [int64]$_.Length }
    return $sum
}

function Remove-Target([string]$Path, [string]$Reason) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not (Test-InRoot $Path)) {
        Write-Audit ('SKIPPED`tOutsideRoot`t{0}' -f $Path)
        return
    }
    if (Test-HasReparsePoint $Path) {
        Write-Audit ('SKIPPED`tReparsePoint`t{0}' -f $Path)
        return
    }
    try { $bytes = Get-TargetBytes $Path } catch {
        Write-Audit ('SKIPPED`tSizeFailed`t{0}`t{1}' -f $Path, $_.Exception.Message)
        return
    }
    $script:ScannedBytes += $bytes
    $script:PlannedBytes += $bytes
    if ($ScanOnly) {
        Write-Audit ('PLANNED`t{0}`t{1}`t{2}' -f $bytes, $Reason, $Path)
        return
    }
    try {
        Remove-Item -LiteralPath $Path -Force -Recurse -ErrorAction Stop
        $script:DeletedBytes += $bytes
        Write-Audit ('DELETED`t{0}`t{1}`t{2}' -f $bytes, $Reason, $Path)
    } catch {
        $script:FailureCount++
        Write-Audit ('FAILED`t{0}`t{1}`t{2}`t{3}' -f $bytes, $Reason, $Path, $_.Exception.Message)
    }
}

function Test-LatestPackage([IO.DirectoryInfo]$Directory, [string]$ChannelName) {
    if ($Directory.Name -notmatch '^\d{8}_\d{6}_\d{3}-\d{6}$') { return $false }
    $prefix = $ChannelName.Replace('EPB0','EPB') + '_Cycle_'
    $csv = @(Get-ChildItem -LiteralPath $Directory.FullName -File -Filter '*.csv' |
        Where-Object { $_.BaseName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object BaseName)
    $bin = @(Get-ChildItem -LiteralPath $Directory.FullName -File -Filter '*.bin' |
        Where-Object { $_.BaseName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object BaseName)
    if ($csv.Count -eq 0 -or $csv.Count -ne $bin.Count) { return $false }
    return @($csv | Where-Object { $bin -notcontains $_ }).Count -eq 0
}

function Clean-AlarmSnapshots {
    $root = Join-Path $ProjectRoot 'AlarmSnapshots'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
    foreach ($event in Get-ChildItem -LiteralPath $root -Directory -Force) {
        if ($event.Name -notmatch '^\d{8}_\d{6}(-\d{3})?-EPB\d{2}.*$') {
            Write-Audit ('SKIPPED`tUnknownAlarmEvent`t{0}' -f $event.FullName); continue
        }
        $alarmDirs = @(Get-ChildItem -LiteralPath $event.FullName -Directory -Force |
            Where-Object { $_.Name -match '^EPB\d{2}_ALARM$' })
        if ($alarmDirs.Count -ne 1) {
            Write-Audit ('SKIPPED`tAlarmChannelDirectoryMissingOrAmbiguous`t{0}' -f $event.FullName); continue
        }
        Get-ChildItem -LiteralPath $event.FullName -Directory -Force |
            Where-Object { $_.Name -match '^EPB\d{1,2}$' } |
            ForEach-Object { Remove-Target $_.FullName 'AlarmAuxiliaryCycleEvidence' }
    }
}

function Clean-Latest {
    $root = Join-Path $ProjectRoot 'Latest'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
    foreach ($channel in Get-ChildItem -LiteralPath $root -Directory -Force) {
        if ($channel.Name -notmatch '^EPB(?:[1-9]|1[0-2])$') {
            Write-Audit ('SKIPPED`tUnknownLatestChannel`t{0}' -f $channel.FullName); continue
        }
        $valid = @()
        foreach ($package in Get-ChildItem -LiteralPath $channel.FullName -Directory -Force) {
            if (Test-LatestPackage $package $channel.Name) { $valid += $package }
            else { Write-Audit ('SKIPPED`tUnknownOrIncompleteLatestPackage`t{0}' -f $package.FullName) }
        }
        $valid | Sort-Object Name -Descending | Select-Object -Skip 10 |
            Sort-Object Name | ForEach-Object { Remove-Target $_.FullName 'LatestKeep10PerChannel' }
    }
}

function Test-WarningEvent([IO.DirectoryInfo]$Directory) {
    if ($Directory.Name -notmatch '^\d{8}_\d{9}-Cycle-?\d+-Streak\d+of\d+$') { return $false }
    return (Test-Path -LiteralPath (Join-Path $Directory.FullName 'warning-metadata.json') -PathType Leaf) -and
           (Test-Path -LiteralPath (Join-Path $Directory.FullName 'checksums.sha256') -PathType Leaf) -and
           @(Get-ChildItem -LiteralPath $Directory.FullName -File -Filter '*.csv').Count -gt 0 -and
           @(Get-ChildItem -LiteralPath $Directory.FullName -File -Filter '*.bin').Count -gt 0
}

function Clean-WarningSnapshots {
    $root = Join-Path $ProjectRoot 'WarningSnapshots'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
    foreach ($channel in Get-ChildItem -LiteralPath $root -Directory -Force) {
        if ($channel.Name -notmatch '^EPB\d{2}$') {
            Write-Audit ('SKIPPED`tUnknownWarningChannel`t{0}' -f $channel.FullName); continue
        }
        foreach ($category in Get-ChildItem -LiteralPath $channel.FullName -Directory -Force) {
            if ($category.Name -eq 'Archive') { continue }
            $valid = @()
            foreach ($event in Get-ChildItem -LiteralPath $category.FullName -Directory -Force) {
                if (Test-WarningEvent $event) { $valid += $event }
                else { Write-Audit ('SKIPPED`tUnknownOrIncompleteWarningEvent`t{0}' -f $event.FullName) }
            }
            $valid | Sort-Object Name -Descending | Select-Object -Skip 30 |
                Sort-Object Name | ForEach-Object { Remove-Target $_.FullName 'WarningKeep30PerChannelCode' }
            $archive = Join-Path $category.FullName 'Archive'
            if (Test-Path -LiteralPath $archive -PathType Container) {
                Get-ChildItem -LiteralPath $archive -File -Force |
                    Where-Object { $_.Extension -eq '.zip' -and $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-30) } |
                    ForEach-Object {
                        $zipPath = $_.FullName
                        Remove-Target $zipPath 'WarningArchiveOlderThan30Days'
                        $sha = $zipPath + '.sha256'
                        if (Test-Path -LiteralPath $sha) { Remove-Target $sha 'WarningArchiveChecksumPair' }
                    }
            }
        }
    }
}

function Keep-NewestDirectories([string]$Root, [string]$Pattern, [int]$Keep, [string]$Reason, [switch]$ByModified) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    $valid = @(Get-ChildItem -LiteralPath $Root -Directory -Force | Where-Object { $_.Name -match $Pattern })
    Get-ChildItem -LiteralPath $Root -Directory -Force |
        Where-Object { $_.Name -notmatch $Pattern } |
        ForEach-Object { Write-Audit ('SKIPPED`tUnknownDirectory`t{0}' -f $_.FullName) }
    if ($ByModified) { $valid = @($valid | Sort-Object LastWriteTimeUtc -Descending) }
    else { $valid = @($valid | Sort-Object Name -Descending) }
    $valid | Select-Object -Skip $Keep | ForEach-Object { Remove-Target $_.FullName $Reason }
}

function Clean-Historical {
    $root = Join-Path $ProjectRoot 'HistoricalSnapshots'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
    foreach ($channel in Get-ChildItem -LiteralPath $root -Directory -Force) {
        if ($channel.Name -notmatch '^EPB\d{2}$') {
            Write-Audit ('SKIPPED`tUnknownHistoricalChannel`t{0}' -f $channel.FullName); continue
        }
        $valid = @(Get-ChildItem -LiteralPath $channel.FullName -Directory -Force |
            Where-Object { $_.Name -match '^Cycle_(\d+)$' } |
            ForEach-Object { [pscustomobject]@{ Directory=$_; Number=[int64]$Matches[1] } })
        Get-ChildItem -LiteralPath $channel.FullName -Directory -Force |
            Where-Object { $_.Name -notmatch '^Cycle_\d+$' } |
            ForEach-Object { Write-Audit ('SKIPPED`tUnknownHistoricalCycle`t{0}' -f $_.FullName) }
        $valid | Sort-Object Number -Descending | Select-Object -Skip 12 |
            ForEach-Object { Remove-Target $_.Directory.FullName 'HistoricalKeep12PerChannel' }
    }
}

function Clean-Telemetry {
    $root = Join-Path $ProjectRoot 'PowerSupplyTelemetry'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
    $cutoff = (Get-Date).AddDays(-3)
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Force) {
        if ($file.Name -notmatch '^(\d{8}_\d{6})_[0-9a-fA-F]{32}\.csv$') {
            Write-Audit ('SKIPPED`tUnknownTelemetry`t{0}' -f $file.FullName); continue
        }
        $time = [DateTime]::ParseExact($Matches[1], 'yyyyMMdd_HHmmss', [Globalization.CultureInfo]::InvariantCulture)
        if ($time -lt $cutoff) { Remove-Target $file.FullName 'PowerTelemetryOlderThan3Days' }
    }
}

function Clean-Logs {
    if (-not (Test-Path -LiteralPath $LogDirectory -PathType Container)) { return }
    $today = (Get-Date).Date
    foreach ($file in Get-ChildItem -LiteralPath $LogDirectory -File -Force -Filter '*.log') {
        if ($file.Name -notmatch '^(run|warning|error|ui-info)\.(\d{8})\.\d{3}\.log$') { continue }
        $stem = $Matches[1]
        $archiveDate = [DateTime]::ParseExact($Matches[2], 'yyyyMMdd', [Globalization.CultureInfo]::InvariantCulture)
        $days = if ($stem -eq 'run') { 7 } else { 30 }
        if ($archiveDate -lt $today.AddDays(-$days)) {
            Remove-Target $file.FullName ('LogArchiveOlderThan{0}Days' -f $days)
        }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { Fail-Validation 'Project root is empty.' }
    $volumeRoot = [IO.Path]::GetPathRoot($ProjectRoot).TrimEnd('\')
    if ($ProjectRoot -eq $volumeRoot -or $ProjectRoot -match '^\\\\[^\\]+\\[^\\]+$') {
        Fail-Validation 'Refusing to run at a drive or share root.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot 'Config\TestConfig.xml') -PathType Leaf)) {
        Fail-Validation 'Config\TestConfig.xml is missing; this is not a valid EPB project root.'
    }
    $knownMarkers = @('index.db', 'Latest', 'HistoricalSnapshots', 'AlarmSnapshots', 'WarningSnapshots',
        'IncidentSnapshots', 'LearningCycles', 'PowerSupplyTelemetry')
    if (@($knownMarkers | Where-Object { Test-Path -LiteralPath (Join-Path $ProjectRoot $_) }).Count -eq 0) {
        Fail-Validation 'No index.db or recognized EPB data directory exists; refusing cleanup.'
    }
    if ((Test-Path -LiteralPath (Join-Path $ProjectRoot 'index.db-wal')) -or
        (Test-Path -LiteralPath (Join-Path $ProjectRoot 'index.db-shm'))) {
        Fail-Validation 'index.db-wal or index.db-shm exists; close all writers before cleanup.'
    }
    $writers = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -match '^(MTTFTest|EPBTest)$' })
    if ($writers.Count -gt 0) { Fail-Validation 'MTTFTest/EPBTest is still running on this computer.' }

    [IO.Directory]::CreateDirectory($LogDirectory) | Out-Null
    Write-Audit ('START`tVersion={0}`tMode={1}`tMachine={2}`tUser={3}`tRoot={4}`tStartedAt={5:o}' -f $ScriptVersion, $(if($ScanOnly){'SCANONLY'}else{'DELETE'}), $env:COMPUTERNAME, $env:USERNAME, $ProjectRoot, $StartedAt)
    Write-Audit 'RULE`tAlarm=KeepAlarmChannelAndCommonEvidence;Latest=10/Channel;Warning=30/ChannelCode;Incident=10;Learning=3/Modified;Telemetry=3Days;RunLog=7Days;OtherLogs=30Days;Historical=12/Channel'
    Write-Audit 'WARNING`tLearningCycles old format has no success/failure marker; keeping newest 3 by LastWriteTimeUtc.'

    Clean-AlarmSnapshots
    Clean-Latest
    Clean-WarningSnapshots
    Keep-NewestDirectories (Join-Path $ProjectRoot 'IncidentSnapshots') '^\d{8}_\d{6}_\d{3}-Dev\d+-[0-9a-fA-F]{32}$' 10 'IncidentKeep10'
    Keep-NewestDirectories (Join-Path $ProjectRoot 'LearningCycles') '^[0-9a-fA-F]{32}$' 3 'LearningKeep3ByModified' -ByModified
    Clean-Telemetry
    Clean-Logs
    Clean-Historical

    $Stopwatch.Stop()
    Write-Audit ('END`tFinishedAt={0:o}`tElapsedMs={1}`tScannedBytes={2}`tPlannedBytes={3}`tDeletedBytes={4}`tFailures={5}' -f (Get-Date), $Stopwatch.ElapsedMilliseconds, $ScannedBytes, $PlannedBytes, $DeletedBytes, $FailureCount)
    Write-Host ('Cleanup finished. Audit: {0}' -f $AuditPath)
    if ($FailureCount -gt 0) { exit 2 }
    exit 0
} catch {
    try { Write-Audit ('FATAL`t' + $_.Exception.ToString()) } catch { }
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
