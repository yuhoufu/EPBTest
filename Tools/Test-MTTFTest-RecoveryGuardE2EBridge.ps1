#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin = Join-Path $repo 'Tests/UnattendedRecoveryE2E/bin/Release'
[void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'MTTFTest.RecoveryControl.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'MTTFTest.exe'))
$type = $assembly.GetType('MTTFTest.UnattendedRecoveryTestMain.GuardTestRun', $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$constructor = $type.GetConstructors($flags)[0]
$commit = $type.GetMethod('TryCommit', $flags)
$stop = $type.GetMethod('Stop', $flags)
$sequence = $type.GetProperty('Sequence', $flags)
$process = [MTTFTest.RecoveryControl.RecoveryProcessProbe]::Current()
$root = Join-Path ([IO.Path]::GetTempPath()) ('GuardE2EBridge-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$passed = 0
foreach ($case in @('DurableCommit', 'LocalStop', 'OperatorStop', 'WriteFailure', 'RecoveryWithoutStartedLaunch')) {
    $folder = Join-Path $root $case
    [void][IO.Directory]::CreateDirectory($folder)
    $store = [MTTFTest.RecoveryControl.RecoveryControlStore]::new((Join-Path $folder 'control'))
    [void]$store.Register('E2EBridgeFixture', $process.ExecutablePath)
    $runId = [Guid]::NewGuid().ToString('N')
    $sessionId = [Guid]::NewGuid().ToString('N')
    $ledger = Join-Path $folder ('e2e-guard-commits-' + $runId + '.log')
    if ($case -eq 'RecoveryWithoutStartedLaunch') {
        $rejected = $false
        try { [void]$constructor.Invoke([object[]]@($store, [string]$runId, [string]$sessionId, [string]$folder, $true)) }
        catch { $rejected = $_.Exception.GetBaseException().Message -eq 'E2EGuardStartedLaunchMissing' }
        if (-not $rejected -or $null -ne $store.Read().Intent) { throw 'Recovery created unauthorized intent' }
    } else {
        $bridge = $constructor.Invoke([object[]]@($store, [string]$runId, [string]$sessionId, [string]$folder, $false))
        if ($case -eq 'DurableCommit') {
            if (-not $commit.Invoke($bridge, @()) -or -not $commit.Invoke($bridge, @())) { throw 'Commit missing' }
            $snapshot = $store.ReadSnapshot()
            if ([IO.File]::ReadAllLines($ledger).Length -ne 2 -or $snapshot.Channels[0].PersistedSequence -ne 2 -or
                $snapshot.RunId -ne $runId -or $store.Read().Intent.WatchdogSessionId -ne $sessionId) { throw 'Durable progress mismatch' }
        } elseif ($case -eq 'LocalStop') {
            [void]$stop.Invoke($bridge, @())
            if ($commit.Invoke($bridge, @()) -or (Test-Path -LiteralPath $ledger)) { throw 'Local stop did not fence commit' }
        } elseif ($case -eq 'OperatorStop') {
            $intent = $store.Read().Intent
            [void]$store.SetOperatorIntent($intent.AuthorizationId, $intent.IntentVersion,
                [MTTFTest.RecoveryControl.RecoveryDesiredState]::Stopped, 'FixtureOperatorStop')
            $rejected = $false
            try { [void]$commit.Invoke($bridge, @()) } catch { $rejected = $true }
            if (-not $rejected -or (Test-Path -LiteralPath $ledger)) { throw 'Operator stop did not fence commit' }
        } else {
            [void][IO.Directory]::CreateDirectory($ledger)
            $rejected = $false
            try { [void]$commit.Invoke($bridge, @()) } catch { $rejected = $true }
            if (-not $rejected -or $sequence.GetValue($bridge) -ne 0 -or $null -ne $store.ReadSnapshot()) {
                throw 'Failed write published progress'
            }
        }
    }
    $passed++
    Write-Output "PASS $case"
}
Write-Output "PASS $passed/5 E2E fixture bridge; no services, tasks or hardware exercised. Evidence: $root"
