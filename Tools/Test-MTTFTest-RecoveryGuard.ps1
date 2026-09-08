[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Configuration -notin @('Debug', 'Release')) { throw 'Configuration must be Debug or Release.' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repo 'artifacts' }
$testRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('recoveryguard-isolation-中文 路径-' + [Guid]::NewGuid().ToString('N'))
$isolatedPackage = Join-Path $testRoot 'IndependentGuard'
$isolatedAuthority = Join-Path $testRoot 'ControlState'
$missingBusiness = Join-Path $testRoot 'MissingBusiness\MTTFTest.exe'
[void](New-Item -ItemType Directory -Path $isolatedPackage -Force)
foreach ($name in @('MTTFTest.RecoveryGuard.exe', 'MTTFTest.RecoveryControl.dll')) {
    $source = Join-Path $repo "MTTFTest.RecoveryGuard\bin\$Configuration\$name"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Build required: $source" }
    Copy-Item -LiteralPath $source -Destination $isolatedPackage
}
$testExe = Join-Path $isolatedPackage 'MTTFTest.RecoveryGuard.exe'
$results = New-Object 'System.Collections.Generic.List[object]'

function Invoke-IsolatedGuard([string]$Case, [string[]]$Arguments, [int]$ExpectedExit) {
    $processInfo = New-Object Diagnostics.ProcessStartInfo
    $processInfo.FileName = $testExe
    # Windows argv quoting, including paths containing spaces or trailing slashes.
    $quoted = @($Arguments | ForEach-Object {
        '"' + ([regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1')) + '"'
    })
    $processInfo.Arguments = $quoted -join ' '
    $processInfo.WorkingDirectory = $isolatedPackage
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $processInfo
    try {
        if (-not $process.Start()) { throw 'Isolated guard did not start.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            $process.Kill()
            [void]$process.WaitForExit(3000)
            throw "Isolated case exceeded 15 seconds: $Case"
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExpectedExit) { throw "$Case exit=$($process.ExitCode) stdout=$stdout stderr=$stderr" }
        $result = $stdout | ConvertFrom-Json
        $results.Add([pscustomobject]@{ case=$Case; exitCode=$process.ExitCode; response=$result })
        return $result
    }
    finally { $process.Dispose() }
}

$registered = Invoke-IsolatedGuard 'register-with-business-absent' @('--register', '--root', $isolatedAuthority, '--bench', 'isolated-test', '--main', $missingBusiness) 0
if (Test-Path -LiteralPath $missingBusiness) { throw 'Business absence precondition violated.' }
$check = Invoke-IsolatedGuard 'check-with-no-business-dlls' @('--check', '--root', $isolatedAuthority) 0
if ($check.Decision.Code -ne 'Unarmed') { throw 'Missing business must not invent an armed trial.' }
$status = Invoke-IsolatedGuard 'status-with-business-absent' @('--status', '--root', $isolatedAuthority) 0
if ($status.InstallationId -ne $registered.InstallationId) { throw 'Registration changed while checking.' }
if ($status.MainExecutablePath -cne $missingBusiness) { throw 'CLI corrupted the Unicode business path.' }
$retry = Invoke-IsolatedGuard 'register-idempotent' @('--register', '--root', $isolatedAuthority, '--bench', 'isolated-test', '--main', $missingBusiness) 0
if ($retry.Revision -ne $status.Revision) { throw 'Installation retry rewrote existing authority.' }
$journal = Join-Path $testRoot 'journal'
$journalPath = Join-Path $journal 'guard-events.jsonl'
[void](Invoke-IsolatedGuard 'durable-observation-journal' @('--check', '--root', $isolatedAuthority, '--journal', $journal) 0)
$event = Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($event.Decision.Code -ne 'Unarmed' -or -not $event.ObservedUtc) { throw 'Observation journal lost decision or time.' }
$fill = [IO.File]::Open($journalPath, [IO.FileMode]::Open, [IO.FileAccess]::Write)
try { $fill.SetLength(8 * 1024 * 1024) } finally { $fill.Dispose() }
[void](Invoke-IsolatedGuard 'bounded-journal-rotation' @('--check', '--root', $isolatedAuthority, '--journal', $journal) 0)
if ((Get-Item -LiteralPath (Join-Path $journal 'guard-events.previous.jsonl')).Length -ne 8 * 1024 * 1024 -or
    (Get-Item -LiteralPath $journalPath).Length -ge 8 * 1024 * 1024) { throw 'Journal rotation did not bound active history.' }
$neverRegistered = Join-Path $testRoot 'MustRemainUnregistered'
[void](Invoke-IsolatedGuard 'settings-validation-without-registration' @('--validate-settings', '--root', $neverRegistered) 0)
if (Test-Path -LiteralPath $neverRegistered) { throw 'Configuration validation created an authority directory.' }
$invalidSettings = Join-Path $testRoot 'invalid-settings.json'
[void](Invoke-IsolatedGuard 'observe-execution-no-action' @('--execute', '--root', $isolatedAuthority) 0)
$executionSettings = Join-Path $testRoot 'execution-settings.json'
[IO.File]::WriteAllText($executionSettings, '{"Mode":1}', (New-Object Text.UTF8Encoding($false)))
[void](Invoke-IsolatedGuard 'execution-test-root-cannot-contact-production' @('--execute', '--root', $isolatedAuthority, '--settings', $executionSettings) 2)
$beforeCommissioning = (Get-FileHash (Join-Path $isolatedAuthority 'control-state.json')).Hash
$scope = @('--commission-until-utc', [DateTime]::UtcNow.AddMinutes(1).ToString('O'),
    '--commission-installation', $registered.InstallationId, '--commission-authorization', 'not-authorized',
    '--commission-intent-version', '1')
$rejected = Invoke-IsolatedGuard 'commissioning-unarmed-refused' (@('--execute', '--root', $isolatedAuthority,
    '--settings', $executionSettings) + $scope) 2
if ($rejected.Error -ne 'CommissioningScopeRevoked') { throw 'Unarmed commissioning rejection missing' }
$rejected = Invoke-IsolatedGuard 'commissioning-check-refused' (@('--check', '--root', $isolatedAuthority) + $scope) 2
if ($rejected.Error -ne 'CommissioningRequiresExecuteAndCompleteScope') { throw 'Commissioning scan must not register an executor' }
$rejected = Invoke-IsolatedGuard 'commissioning-partial-scope-refused' @('--execute', '--root', $isolatedAuthority,
    '--commission-authorization', 'not-authorized') 2
if ($rejected.Error -ne 'CommissioningRequiresExecuteAndCompleteScope') { throw 'Partial commissioning scope accepted' }
if ((Get-FileHash (Join-Path $isolatedAuthority 'control-state.json')).Hash -ne $beforeCommissioning) {
    throw 'Rejected commissioning mutated authority'
}
[IO.File]::WriteAllText($invalidSettings, '{"SchemaVersion":99}', (New-Object Text.UTF8Encoding($false)))
[void](Invoke-IsolatedGuard 'unsupported-settings-refused' @('--check', '--root', $isolatedAuthority, '--settings', $invalidSettings) 2)
[IO.File]::WriteAllText((Join-Path $isolatedAuthority 'control-state.json'), '{broken', (New-Object Text.UTF8Encoding($false)))
[void](Invoke-IsolatedGuard 'corrupt-authority-refused' @('--check', '--root', $isolatedAuthority) 2)
[void](Invoke-IsolatedGuard 'authority-failure-journaled' @('--check', '--root', $isolatedAuthority, '--journal', $journal) 2)
$lastEvent = Get-Content -LiteralPath $journalPath -Encoding UTF8 -Tail 1 | ConvertFrom-Json
if (-not $lastEvent.Error -or $lastEvent.AutomaticActionPerformed -ne $false) { throw 'Failed check lacks a durable failure outcome.' }
$report = [pscustomobject]@{
    schemaVersion=1
    testedUtc=[DateTime]::UtcNow.ToString('O')
    configuration=$Configuration
    businessExecutableAbsent=(-not (Test-Path -LiteralPath $missingBusiness))
    copiedFiles=@(Get-ChildItem -LiteralPath $isolatedPackage -File | ForEach-Object {
        @{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    passed=$results.Count
    cases=@($results.ToArray())
}
$reportPath = Join-Path $testRoot 'results.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{ passed=$results.Count; evidence=$reportPath } | ConvertTo-Json
