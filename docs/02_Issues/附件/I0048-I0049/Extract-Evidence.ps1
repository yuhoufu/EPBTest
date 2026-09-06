param(
    [string]$BackupRoot = 'D:\EPB_Data\Backups',
    [string]$Repository = 'D:\Github\wanxiang\EPBTest'
)
$ErrorActionPreference = 'Stop'
$output = $PSScriptRoot
$utf8 = [System.Text.UTF8Encoding]::new($false)
function Write-Json($name, $value) {
    [IO.File]::WriteAllText((Join-Path $output $name), (ConvertTo-Json -InputObject $value -Depth 15), $utf8)
}
$roots = [ordered]@{
    I0048 = Join-Path $BackupRoot '10243-028_V2.17.2.0_I0048_0906_2157'
    I0049 = Join-Path $BackupRoot '10243-028_V2.17.2.0_I0049_0906_2221'
    Export = Join-Path $BackupRoot 'EPB-V217-Evidence-20260906-222129'
    Old = Join-Path $BackupRoot '10243-028_V2.14.2.11_I0034_0905_0726'
}
$manifest = [Collections.Generic.List[object]]::new()
function Record-Input($path) {
    $manifest.Add([ordered]@{Path=$path;Bytes=(Get-Item -LiteralPath $path).Length;SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash})
}
foreach($name in @('evidence-index.json','evidence-summary.json','processes.json','application-events.json',
    '00013-safety-authority-4a3dbab0711e5f60df8cea6e6ec7d3c1.v6.json')) {
    Record-Input (Join-Path $roots.Export $name)
}
foreach($label in @('I0048','I0049')) {
    Record-Input (Join-Path $roots[$label] 'backup-manifest.json')
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $roots[$label] 'WatchdogSessions') -File) {
        if($file.Name -match '^session-(b9622b65|cb1bfc82).*\.(safety-handoff|application-exit|relaunch|replacement[^.]*)\.json$') {
            Record-Input $file.FullName
        }
    }
}
Record-Input (Join-Path $roots.I0048 'session-b9622b65c70b4b2fbbeec07f1ca8500b.safety-handoff.json')
Record-Input (Join-Path $roots.I0049 'Recovery\unattended-run-checkpoint.json')
$exportIndex = Get-Content -LiteralPath (Join-Path $roots.Export 'evidence-index.json') -Raw | ConvertFrom-Json
$validation = @($exportIndex | ForEach-Object {
    if ($_.Copied) {
        $path = Join-Path $roots.Export $_.File
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        [pscustomobject]@{File=$_.File;Matches=[string]::Equals($actual,$_.SHA256,[StringComparison]::OrdinalIgnoreCase)}
    }
})
Write-Json 'export-validation.json' ([ordered]@{
    Checked=$validation.Count;Mismatches=@($validation | Where-Object {-not $_.Matches});
    Omitted=@($exportIndex | Where-Object {-not $_.Copied})
})
$selected = [Collections.Generic.List[object]]::new()
foreach ($label in @('I0048','I0049')) {
    foreach ($file in @('run.log','warning.log','error.log','ui-info.log')) {
        $path = Join-Path $roots[$label] ('log\' + $file)
        Record-Input $path
        $lineNumber=0
        foreach ($line in [IO.File]::ReadLines($path)) {
            $lineNumber++
            $inWindow = if ($label -eq 'I0048') {$line -match '2026-09-06 21:(27|28):'} else {$line -match '2026-09-06 22:(17|18):'}
            $wanted = $line -match '持久化积压|持久化队列已恢复|StaleExecutionPermit|complete owner contract|State=SystemFault|DAQ_RECOVERY Result=Cancelled|OrphanPauseDetected|STOP_PERSISTENCE|FieldMetric DAQ Phase=Running|HostRuntime|StopSafetyPending|ApplicationExit|WatchdogTransport|拒绝作废非当前圈|ProjectLogShutdown'
            if ($inWindow -and $wanted) {
                $selected.Add([ordered]@{Source="$label/log/$file";Line=$lineNumber;Text=$line})
            }
        }
    }
}
Write-Json 'log-excerpts.json' @($selected.ToArray())
$events = [Collections.Generic.List[object]]::new()
foreach ($session in @('b9622b65c70b4b2fbbeec07f1ca8500b','cb1bfc8241cd4c018975b0c05ad2974f')) {
    foreach ($source in @('client','sidecar')) {
        $path=Join-Path $roots.I0049 "WatchdogSessions\session-$session.$source-events.jsonl"
        Record-Input $path
        $n=0
        foreach ($line in [IO.File]::ReadLines($path)) {
            $n++
            $j=$line | ConvertFrom-Json
            $utcText=[regex]::Match($line,'"Utc":"([^"]+)"').Groups[1].Value
            $utc=[DateTimeOffset]::Parse($utcText)
            $events.Add([ordered]@{Session=$session;Source=$source;Line=$n;Sequence=$j.EventSequence;Utc=$utcText;Local=$utc.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd HH:mm:ss.fff');Event=$j.EventType;Reason=$j.Reason})
        }
    }
}
Write-Json 'watchdog-timeline.json' @($events.ToArray())
$authorityPath=Join-Path $roots.Export '00002-safety-authority-2d571ee498df8bb4a9c9f82e94bcda0c.v6.json'
$buildPath=Join-Path $roots.Export '00001-build-identity.json'
Record-Input $authorityPath
Record-Input $buildPath
$authority=Get-Content -LiteralPath $authorityPath -Raw | ConvertFrom-Json
$build=Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
$receiptHash=$authority.Receipt.SafetyAgentExecutableSha256
$component=@($build.componentIdentities | Where-Object name -eq 'MTTFTest.SafetyAgent.exe')[0]
$failureEvents=@($events | Where-Object {$_.Session -like 'cb1bfc*' -and $_.Event -eq 'SafetyHandoffWorkerFailed'})
Write-Json 'findings.json' ([ordered]@{
    BuildVersion=$build.productVersion;BuildCommit=$build.gitCommit;MainSha256=$build.mainExecutableSha256;
    ReceiptSafetyAgentHash=$receiptHash;BuildIdentitySafetyAgentHash=$component.sha256;
    RequestHashFormat=$receiptHash.ToUpperInvariant();
    OrdinalComparison=[string]::Equals($receiptHash,$receiptHash.ToUpperInvariant(),[StringComparison]::Ordinal);
    DigestComparison=[string]::Equals($receiptHash,$component.sha256,[StringComparison]::OrdinalIgnoreCase);
    WorkerFailureCount=$failureEvents.Count;FirstWorkerFailure=$failureEvents[0];LastWorkerFailure=$failureEvents[-1]
})
$oldStarts=[Collections.Generic.List[object]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $roots.Old 'log') -Filter 'run*.log') {
    Record-Input $file.FullName
    $n=0
    foreach($line in [IO.File]::ReadLines($file.FullName)) {
        $n++
        if($line -match '启动构建身份.*ProductVersion=') {$oldStarts.Add([ordered]@{Source=$file.Name;Line=$n;Text=$line})}
    }
}
Write-Json 'old-version-starts.json' @($oldStarts.ToArray())
$sourceSpec=[ordered]@{
    'Controller/EpbManager.cs'=@(@(1977,1988),@(2006,2040),@(2075,2089),@(4307,4316),@(6830,6848),@(7459,7477),@(8240,8261))
    'Controller/EpbManager.PauseResume.cs'=@(@(2322,2354))
    'Controller/ChannelExecutionFence.cs'=@(@(86,109))
    'Controller/ChannelRuntimeState.cs'=@(@(540,552))
    'Controller/RecoveryIncidentCoordinator.cs'=@(@(480,510),@(527,541))
    'MTTFTest.Watchdog/SupervisorServiceHost.cs'=@(@(1234,1239))
    'MTTFTest.Watchdog/SupervisorSafetyAgentLaunchClient.cs'=@(@(75,83))
    'Watchdog.Protocol/SupervisorProtocol.cs'=@(@(65,77))
    'Watchdog.Protocol/DurableJsonFileStore.cs'=@(@(755,760))
    'MTTFTest.Watchdog/WatchdogHost.cs'=@(@(2390,2407),@(6757,6779),@(7093,7144))
    'MTTfTest/WatchdogRuntime.cs'=@(@(2730,2733),@(3446,3461),@(3772,3801))
    'MTTfTest/FrmEpbMainMonitor.cs'=@(@(3011,3041),@(3478,3496))
    'MTTfTest/FrmEpbMainMonitor.CloseOverlay.cs'=@(@(17,24),@(94,108))
    'MTTfTest/Main_Frm.WatchdogUi.cs'=@(@(186,198))
}
$sourceText=[Collections.Generic.List[string]]::new()
$sourceText.Add('# 现场基线源码摘录：1185a09（不含工作区未完成修复）')
foreach($file in $sourceSpec.Keys) {
    $lines=@(& git -C $Repository show "1185a09:$file")
    if($LASTEXITCODE -ne 0){throw "git show failed: $file"}
    $sourceText.Add("`n## $file`n")
    $ranges=$sourceSpec[$file]
    if($ranges[0] -is [int]) {$ranges=, $ranges}
    foreach($range in $ranges) {
        $sourceText.Add('```text')
        for($n=$range[0];$n -le $range[1];$n++) {$sourceText.Add(('{0}: {1}' -f $n,$lines[$n-1]))}
        $sourceText.Add('```')
    }
}
[IO.File]::WriteAllLines((Join-Path $output 'baseline-source.md'),$sourceText,$utf8)
$diff=@(& git -C $Repository diff 2.14.2.11 1185a09 -- Controller/ChannelExecutionFence.cs MTTfTest/App.config)
$old=@(& git -C $Repository show '2.14.2.11:Controller/EpbManager.cs')
$diff += "`n2.14.2.11 EpbManager.cs:2066-2080"
for($n=2066;$n -le 2080;$n++) {$diff += ('{0}: {1}' -f $n,$old[$n-1])}
[IO.File]::WriteAllLines((Join-Path $output 'old-version-diff.txt'),$diff,$utf8)
Write-Json 'input-hashes.json' @($manifest.ToArray())
Write-Output "Export=$($validation.Count); mismatch=$(@($validation | Where-Object {-not $_.Matches}).Count); excerpt=$($selected.Count); watchdogEvents=$($events.Count); workerFailures=$($failureEvents.Count)"
