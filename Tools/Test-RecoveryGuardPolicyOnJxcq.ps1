param(
    [string]$SeedDirectory = 'D:\EPB_Validation\main-policy-wip-20260909-115024\payload',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$IncludeCycleLifecycle,
    [switch]$IncludeStopProductionSeam,
    [switch]$IncludeRecoveryResume,
    [switch]$IncludeRecoveryProductionSeam,
    [switch]$IncludeHydraulicCoordination
)

# Development policy tests only. Never installs or starts the trial application.
function Get-PolicyInputFiles([string]$SourceDirectory) {
    Get-ChildItem -LiteralPath $SourceDirectory -File | Where-Object Extension -ne '.pdb'
    Get-ChildItem -LiteralPath (Join-Path $SourceDirectory 'x86'), (Join-Path $SourceDirectory 'x64') -File -Filter 'SQLite.Interop.dll'
    Get-ChildItem -LiteralPath (Join-Path $SourceDirectory 'Config') -File -Filter '*.xml'
}

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $repo ('Tests\AdaptiveControlTests\bin\' + $Configuration)
$files = @(Get-PolicyInputFiles $source)
if (-not ($files.Name -contains 'AdaptiveControlTests.exe')) { throw 'Missing built test executable.' }
$manifest = @($files | ForEach-Object {
    [pscustomobject]@{ Name = $_.FullName.Substring($source.Length).TrimStart('\'); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$session = $null
try {
    $session = New-PSSession -ComputerName 'MT-20251206JXCQ' -SessionOption (New-PSSessionOption -OpenTimeout 10000)
    $root = Invoke-Command -Session $session -ArgumentList $SeedDirectory -ScriptBlock {
        param($seed)
        if ($env:COMPUTERNAME -ne 'MT-20251206JXCQ') { throw 'Wrong validation host.' }
        $prefix = 'D:\EPB_Validation\'
        $resolved = [IO.Path]::GetFullPath($seed)
        if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $resolved -PathType Container)) { throw 'Invalid isolated seed directory.' }
        $target = $prefix + 'policy-incremental-' + [Guid]::NewGuid().ToString('N')
        [void](New-Item -ItemType Directory -Path $target)
        # Copy seed files, not directories, services, links or installation state.
        Get-ChildItem -LiteralPath $resolved -File | Where-Object { $_.Name -ne 'README-validation.md' -and $_.Extension -ne '.log' } |
            Copy-Item -Destination $target
        foreach ($architecture in @('x86', 'x64')) {
            $native = Join-Path $resolved $architecture
            if (Test-Path -LiteralPath $native -PathType Container) { Copy-Item -LiteralPath $native -Destination $target -Recurse }
        }
        $target
    }
    $existing = @(Invoke-Command -Session $session -ArgumentList $root -ScriptBlock {
        param($path)
        Get-ChildItem -LiteralPath $path -File -Recurse | ForEach-Object {
            [pscustomobject]@{ Name = $_.FullName.Substring($path.Length).TrimStart('\'); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        }
    })
    if (@($existing | Where-Object { $_.Name -notin $manifest.Name }).Count) { throw 'Unexpected seed file; no deletion performed.' }
    $changed = @($manifest | Where-Object {
        $entry = $_
        -not @($existing | Where-Object { $_.Name -eq $entry.Name -and $_.Length -eq $entry.Length -and $_.Sha256 -eq $entry.Sha256 }).Count
    })
    foreach ($entry in $changed) {
        Invoke-Command -Session $session -ArgumentList $root, $entry.Name -ScriptBlock {
            param($path, $name)
            [void](New-Item -ItemType Directory -Path (Split-Path (Join-Path $path $name) -Parent) -Force)
        }
        Copy-Item -LiteralPath (Join-Path $source $entry.Name) -Destination (Join-Path $root $entry.Name) -ToSession $session
    }
    $evidence = Invoke-Command -Session $session -ArgumentList $root, $manifest, ([bool]$IncludeStopProductionSeam), ([bool]$IncludeRecoveryResume), ([bool]$IncludeHydraulicCoordination), ([bool]$IncludeRecoveryProductionSeam), ([bool]$IncludeCycleLifecycle) -ScriptBlock {
        param($path, $expected, $includeProduction, $includeResume, $includeHydraulic, $includeRecoveryProduction, $includeCycle)
        $actual = @(Get-ChildItem -LiteralPath $path -File -Recurse)
        if ($actual.Count -ne $expected.Count) { throw 'File count mismatch.' }
        foreach ($entry in $expected) {
            $file = Get-Item -LiteralPath (Join-Path $path $entry.Name)
            if ($file.Length -ne $entry.Length -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $entry.Sha256) {
                throw ('Hash mismatch: ' + $entry.Name)
            }
        }
        $results = @()
        $suites = @('guard-live-stop', 'safety-feedback')
        if ($includeProduction) { $suites += 'stop-production-seam' }
        if ($includeResume) { $suites += @('recovery-coordination', 'recovery-lifecycle') }
        if ($includeRecoveryProduction) { $suites += 'recovery-production-seam' }
        if ($includeHydraulic) { $suites += 'hydraulic-coordination' }
        if ($includeCycle) { $suites += @('cycle-lifecycle', 'v2171-recovery-closure') }
        foreach ($suite in $suites) {
            $log = Join-Path $path ($suite + '.log')
            & (Join-Path $path 'AdaptiveControlTests.exe') ('--' + $suite) *> $log
            $code = $LASTEXITCODE
            $results += [pscustomobject]@{ Suite = $suite; ExitCode = $code; Log = $log; Tail = @(Get-Content -LiteralPath $log -Tail 2) }
            if ($code -ne 0) { break }
        }
        [pscustomobject]@{ Computer = $env:COMPUTERNAME; Root = $path; HashVerifiedFiles = $expected.Count;
            DeployablePackage = $false; PhysicalHardwareVerified = $false; Results = $results }
    }
    $localLogs = Join-Path $repo 'artifacts\guard-physical-safety'
    [void](New-Item -ItemType Directory -Path $localLogs -Force)
    foreach ($result in $evidence.Results) {
        Copy-Item -FromSession $session -LiteralPath $result.Log -Destination (
            Join-Path $localLogs ((Split-Path $root -Leaf) + '-' + $result.Suite + '-jxcq.log'))
    }
    # Remote execution proves the captured manifest, not a subsequently rebuilt local tree.
    # Keep returned logs even on mismatch, but never report this as current-input validation.
    $finalLocalNames = @(Get-PolicyInputFiles $source | ForEach-Object {
        $_.FullName.Substring($source.Length).TrimStart('\')
    })
    if ($finalLocalNames.Count -ne $manifest.Count -or
        @($finalLocalNames | Where-Object { $_ -notin $manifest.Name }).Count) {
        throw 'Local input file set changed during JXCQ validation.'
    }
    foreach ($entry in $manifest) {
        $localFile = Get-Item -LiteralPath (Join-Path $source $entry.Name)
        if ($localFile.Length -ne $entry.Length -or
            (Get-FileHash -LiteralPath $localFile.FullName -Algorithm SHA256).Hash -ne $entry.Sha256) {
            throw ('Local input changed during JXCQ validation: ' + $entry.Name)
        }
    }
    [pscustomobject]@{ Configuration = $Configuration; LocalInputsReverified = $true;
        InputManifest = $manifest; TransferredFiles = $changed.Count; TransferredBytes = [long]($changed | Measure-Object Length -Sum).Sum;
        Validation = $evidence } | ConvertTo-Json -Depth 6
    $expectedSuites = if ($IncludeStopProductionSeam) { 3 } else { 2 }
    if ($IncludeRecoveryResume) { $expectedSuites += 2 }
    if ($IncludeRecoveryProductionSeam) { $expectedSuites++ }
    if ($IncludeHydraulicCoordination) { $expectedSuites++ }
    if ($IncludeCycleLifecycle) { $expectedSuites += 2 }
    if ($evidence.Results.Count -ne $expectedSuites -or @($evidence.Results | Where-Object ExitCode -ne 0).Count) {
        throw 'JXCQ development policy test failed; inspect returned logs.'
    }
}
finally { if ($session) { Remove-PSSession $session } }
