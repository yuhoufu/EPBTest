param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

# Development-only isolated process tests; not a deployment or hardware acceptance.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $repo ('Tests\RecoveryGuardTests\bin\' + $Configuration)
$names = @('RecoveryGuardTests.exe','MTTFTest.RecoveryGuard.exe','MTTFTest.RecoveryControl.dll')
$manifest = @($names | ForEach-Object {
    $file = Get-Item -LiteralPath (Join-Path $source $_)
    [pscustomobject]@{Name=$file.Name; Length=$file.Length; Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}
})
$session = $null
$timings = [ordered]@{}
$phaseTimer = [Diagnostics.Stopwatch]::StartNew()
try {
    $session = New-PSSession -ComputerName 'MT-20251206JXCQ' -SessionOption (New-PSSessionOption -OpenTimeout 10000)
    $remoteRoot = Invoke-Command -Session $session -ScriptBlock {
        if ($env:COMPUTERNAME -ne 'MT-20251206JXCQ') { throw 'WrongValidationHost' }
        $path = 'D:\EPB_Validation\independent-guard-retirement-' + [Guid]::NewGuid().ToString('N')
        [void](New-Item -ItemType Directory -Path $path)
        $path
    }
    $timings.ConnectAndCreateDirectoryMs = $phaseTimer.ElapsedMilliseconds
    $phaseTimer.Restart()
    foreach ($entry in $manifest) {
        Copy-Item -LiteralPath (Join-Path $source $entry.Name) -Destination (Join-Path $remoteRoot $entry.Name) -ToSession $session
    }
    $timings.TransferMs = $phaseTimer.ElapsedMilliseconds
    $phaseTimer.Restart()
    $result = Invoke-Command -Session $session -ArgumentList $remoteRoot, $manifest -ScriptBlock {
        param($path, $expected)
        if ($env:COMPUTERNAME -ne 'MT-20251206JXCQ' -or
            -not [IO.Path]::GetFullPath($path).StartsWith('D:\EPB_Validation\independent-guard-retirement-', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'ValidationScopeInvalid'
        }
        if (@(Get-ChildItem -LiteralPath $path -File).Count -ne $expected.Count) { throw 'ValidationFileCountMismatch' }
        foreach ($entry in $expected) {
            $file = Get-Item -LiteralPath (Join-Path $path $entry.Name)
            if ($file.Length -ne $entry.Length -or (Get-FileHash -LiteralPath $file.FullName).Hash -cne $entry.Sha256) {
                throw ('ValidationHashMismatch:' + $entry.Name)
            }
        }
        $log = Join-Path $path 'exact-main-retirement.log'
        & (Join-Path $path 'RecoveryGuardTests.exe') --exact-main-retirement *> $log
        [pscustomobject]@{Machine=$env:COMPUTERNAME; Root=$path; ExitCode=$LASTEXITCODE;
            Files=$expected; Log=$log; Tail=@(Get-Content -LiteralPath $log -Tail 5);
        DeployablePackage=$false; InstallationPerformed=$false; PhysicalHardwareVerified=$false}
    }
    $timings.VerifyAndRunRemoteTestsMs = $phaseTimer.ElapsedMilliseconds
    $phaseTimer.Restart()
    $logs = Join-Path $repo 'artifacts\guard-physical-safety'
    Copy-Item -FromSession $session -LiteralPath $result.Log -Destination (Join-Path $logs ((Split-Path $remoteRoot -Leaf) + '.log'))
    $timings.FetchLogMs = $phaseTimer.ElapsedMilliseconds
    $result | Add-Member -NotePropertyName ClientPhaseTimings -NotePropertyValue $timings
    $result | Add-Member -NotePropertyName Configuration -NotePropertyValue $Configuration
    $result | ConvertTo-Json -Depth 6
    foreach ($entry in $manifest) {
        if ((Get-FileHash -LiteralPath (Join-Path $source $entry.Name)).Hash -cne $entry.Sha256) { throw 'LocalInputsChangedDuringValidation' }
    }
    if ($result.ExitCode -ne 0 -or 'PASS 4/4' -cnotin $result.Tail) { throw 'IndependentGuardRetirementValidationFailed' }
}
finally { if ($session) { Remove-PSSession $session } }
