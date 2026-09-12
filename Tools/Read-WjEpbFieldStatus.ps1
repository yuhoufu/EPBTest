[CmdletBinding()]
param(
    [string]$EvidenceDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\field-deploy-20260907\monitor')
)
$ErrorActionPreference = 'Stop'
# Reads production state only. All evidence writes are local to this computer.
$session = $null
$job = $null
try {
    $session = Get-WjEpbSession
    $job = Invoke-Command -Session $session -AsJob -ScriptBlock {
        $ErrorActionPreference = 'Stop'
        if ($env:COMPUTERNAME -ne 'MT-20250724OGLX') { throw 'Unexpected field host' }
        function Read-SharedText([string]$Path, [int]$MaxBytes = 1048576) {
            $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try {
                $size = $stream.Length
                $offset = [Math]::Max(0, $size - $MaxBytes)
                [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin)
                $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
                try { $value = $reader.ReadToEnd() } finally { $reader.Dispose() }
                if ($offset -gt 0) {
                    $newline = $value.IndexOf("`n")
                    $value = if ($newline -ge 0) { $value.Substring($newline + 1) } else { '' }
                }
                [pscustomobject]@{ Path=$Path; Size=$size; Offset=$offset; Partial=($offset -gt 0); Text=$value }
            } finally { $stream.Dispose() }
        }
        $project = 'D:\EPB_Data\10243-028'
        $installed = 'C:\Program Files (x86)\MTTFTest\Current'
        $result = [ordered]@{ Utc=[DateTime]::UtcNow.ToString('o'); Host=$env:COMPUTERNAME;
            Project=$project; Version=(Get-Item "$installed\MTTFTest.exe").VersionInfo.FileVersion;
            ReadErrors=@() }
        $processes = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'MTTFTest%'" | ForEach-Object {
            [pscustomobject]@{ Name=$_.Name; Pid=$_.ProcessId; SessionId=$_.SessionId;
                StartUtc=$_.CreationDate.ToUniversalTime().ToString('o'); Path=$_.ExecutablePath; CommandLine=$_.CommandLine }
        })
        $result.Processes = $processes
        $result.Service = (Get-Service 'MTTFTestSupervisor').Status.ToString()
        $result.Maintenance = Test-Path 'C:\ProgramData\MTTFTest\maintenance-inhibit.json'
        $result.Tasks = @(Get-ScheduledTask | Where-Object TaskName -match '^MTTFTest' | ForEach-Object {
            $info = Get-ScheduledTaskInfo -TaskName $_.TaskName
            [pscustomobject]@{ Name=$_.TaskName; State=$_.State.ToString(); LastRun=$info.LastRunTime.ToString('o'); Result=$info.LastTaskResult }
        })
        Add-Type -Path "$installed\MTTFTest.Watchdog.Protocol.dll"
        $pipes = @('MTTFTest.Health.Supervisor.V1')
        foreach ($proc in $processes) {
            if ($proc.Name -eq 'MTTFTest.SessionAgent.exe') { $pipes += "MTTFTest.Health.Agent.V1.$($proc.SessionId)" }
            if ($proc.CommandLine -match '--session-host') { $pipes += "MTTFTest.Health.Host.V1.$($proc.Pid)" }
        }
        $healthType = 'MTTFTest.Watchdog.Protocol.RecoveryHealthEndpoint' -as [type]
        $result.Health = @(foreach ($pipe in $pipes) {
            if ($null -eq $healthType) {
                [pscustomobject]@{ Pipe=$pipe; Healthy=$null; Unsupported=$true; Detail='Installed legacy version has no V1 health endpoint; use process, session and business evidence.' }
                continue
            }
            try {
                $health = [MTTFTest.Watchdog.Protocol.RecoveryHealthEndpoint]::Probe($pipe, 1500)
                $proc = Get-Process -Id $health.ProcessId -ErrorAction Stop
                [pscustomobject]@{ Pipe=$pipe; Healthy=$health.Matches($proc.Id,$proc.StartTime.ToUniversalTime().Ticks,[DateTime]::UtcNow,20);
                    Pid=$health.ProcessId; StartTicks=$health.ProcessStartUtcTicks; ProgressTicks=$health.ProgressUtcTicks;
                    Stage=$health.Stage; Detail=$health.Detail }
            } catch { [pscustomobject]@{ Pipe=$pipe; Healthy=$false; Error=$_.Exception.Message } }
        })
        try {
            [xml]$config = (Read-SharedText "$project\Config\TestConfig.xml").Text
            $result.Channels = @($config.SelectNodes('//Record') | Where-Object Enabled -eq 'True' | ForEach-Object {
                [pscustomobject]@{ Channel=[int]$_.Id; RunCount=[int]$_.RunCount; Mechanical=[int]$_.MechanicalCycleCount;
                    Remaining=([int]$_.TotalCount-[int]$_.MechanicalCycleCount); Status=[string]$_.Status }
            })
            $result.Checkpoint = (Read-SharedText "$project\Recovery\unattended-run-checkpoint.json").Text | ConvertFrom-Json
        } catch { $result.ReadErrors += $_.Exception.Message }
        try {
            Add-Type -Path "$installed\System.Data.SQLite.dll"
            $connection = New-Object System.Data.SQLite.SQLiteConnection("Data Source=$project\index.db;Read Only=True;FailIfMissing=True;Pooling=False;Default Timeout=2;")
            try {
                $connection.Open()
                $command = $connection.CreateCommand()
                $command.CommandTimeout = 2
                $command.CommandText = @'
SELECT r.epb_id, COUNT(*) AS receipt_count,
SUM(r.mechanical) + COALESCE((SELECT legacy_offset FROM mechanical_baselines b WHERE b.epb_id=r.epb_id),0) AS mechanical,
MAX(r.cycle_number) AS max_cycle, datetime(MAX(julianday(r.completed_at))) AS latest_utc
FROM cycle_receipts r WHERE r.epb_id IN (4,5,7,8,9,12) GROUP BY r.epb_id;
'@
                $result.DurableSource = 'cycle_receipts + mechanical_baselines'
                if ([version]$result.Version -lt [version]'2.17.0.0') {
                    # The legacy writer updates epb_cycles, not the newer receipt table.
                    $result.DurableSource = 'epb_cycles (legacy writer)'
                    $command.CommandText = @'
SELECT epb_id,COUNT(*) AS receipt_count,SUM(mechanical_completed) AS mechanical,
MAX(cycle_number) AS max_cycle,datetime(MAX(julianday(mechanical_completed_at))) AS latest_utc
FROM epb_cycles WHERE epb_id IN (4,5,7,8,9,12) GROUP BY epb_id;
'@
                }
                $reader = $command.ExecuteReader()
                try {
                    $table = New-Object Data.DataTable
                    $table.Load($reader)
                    $result.Durable = @($table | Select-Object epb_id,receipt_count,mechanical,max_cycle,latest_utc)
                } finally { $reader.Dispose() }
                $command.CommandText = 'SELECT id,epb_id,cycle_number,start_time,end_time,sample_count,status,last_complete_sequence,mechanical_completed FROM epb_cycles ORDER BY id DESC LIMIT 36;'
                $reader = $command.ExecuteReader()
                try {
                    $recent = New-Object Data.DataTable
                    $recent.Load($reader)
                    $result.RecentCycles = @($recent | Select-Object id,epb_id,cycle_number,start_time,end_time,sample_count,status,last_complete_sequence,mechanical_completed)
                } finally { $reader.Dispose(); $command.Dispose() }
            } finally { $connection.Dispose() }
        } catch { $result.ReadErrors += ('SQLite: ' + $_.Exception.Message) }
        $result.Logs = @(foreach ($name in @('error.log','warning.log','ui-info.log','run.log')) {
            $path = Join-Path "$project\log" $name
            if (Test-Path -LiteralPath $path) { Read-SharedText $path 65536 }
        })
        $result.Manifests = @()
        try {
        $result.Manifests = @(Get-ChildItem "$project\WatchdogSessions" -File -Filter 'session-*.json' |
            Where-Object Name -match '^session-[0-9a-f]{32}\.json$' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
                try { (Read-SharedText $_.FullName).Text | ConvertFrom-Json }
                catch { $result.ReadErrors += $_.Exception.Message }
            })
        } catch { $result.ReadErrors += ('WatchdogSessions: ' + $_.Exception.Message) }
        $result.FreeBytes = (Get-PSDrive D).Free
        $result | ConvertTo-Json -Depth 14 -Compress
    }
    if (-not (Wait-Job $job -Timeout 70)) { throw 'Read-only field check timed out after 70 seconds' }
    $json = Receive-Job $job -ErrorAction Stop
    $snapshot = $json | ConvertFrom-Json
} catch {
    $snapshot = [pscustomobject]@{ Utc=[DateTime]::UtcNow.ToString('o'); MonitorUnavailable=$true; Error=$_.Exception.Message }
} finally {
    if ($job) { if ($job.State -eq 'Running') { Stop-Job $job }; Remove-Job $job -Force }
    if ($session) { Remove-PSSession $session }
}
[void](New-Item -ItemType Directory -Path $EvidenceDirectory -Force)
$path = Join-Path $EvidenceDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
$snapshot | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $path -Encoding UTF8
[pscustomobject]@{ Evidence=$path; Utc=$snapshot.Utc; MonitorUnavailable=$snapshot.MonitorUnavailable;
    Error=$snapshot.Error; Version=$snapshot.Version; Service=$snapshot.Service; Maintenance=$snapshot.Maintenance;
    Processes=$snapshot.Processes; Health=$snapshot.Health; Channels=$snapshot.Channels; Durable=$snapshot.Durable;
    ReadErrors=$snapshot.ReadErrors; Tasks=$snapshot.Tasks } | ConvertTo-Json -Depth 8
