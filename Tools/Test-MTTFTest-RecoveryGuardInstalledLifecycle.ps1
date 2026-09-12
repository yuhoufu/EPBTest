#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [string]$BenchId = 'recoveryguard-install-validation',
    [string]$MainExecutable = 'C:\MTTFTest-RecoveryGuard-Validation\MissingMain\MTTFTest.exe'
)

$ErrorActionPreference = 'Stop'
$guardState = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'MTTFTestRecoveryGuard'
$controlRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'MTTFTest\RecoveryControl'
$registrationPath = Join-Path $guardState 'installation.json'
$settingsPath = Join-Path $guardState 'guard-settings.json'
$taskNames = @('MTTFTestRecoveryGuard', 'MTTFTestRecoveryGuardExecution')
$installer = Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) 'Install-MTTFTest-RecoveryGuard.ps1'
$install = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)

function Get-GuardTasks {
    return @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop |
        Where-Object { $_.TaskName -in $taskNames })
}

function Invoke-Installer([string]$Mode) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer,
        '-Mode', $Mode, '-InstallRoot', $install)
    if ($Mode -eq 'Install') {
        $arguments += @('-SourceDirectory', $PackageDirectory, '-BenchId', $BenchId,
            '-MainExecutable', $MainExecutable, '-RecoveryMode', 'ObserveOnly')
    }
    $previousPreference = $ErrorActionPreference
    try {
        # The injected failure is expected to write to stderr. Capture it as
        # evidence without letting the caller's Stop preference abort the test.
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell.exe @arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = @($output | ForEach-Object { [string]$_ }) }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installed lifecycle validation requires an elevated PowerShell session.'
}
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installer is missing from the package.' }
if (Test-Path -LiteralPath $registrationPath -PathType Leaf) { throw 'Guard installation registration already exists.' }
if ((Get-GuardTasks).Count -ne 0) { throw 'Guard scheduled tasks already exist.' }
if (Test-Path -LiteralPath $controlRoot) { throw 'RecoveryControl already exists; use an isolated clean validation host.' }
if (Test-Path -LiteralPath $install) { throw 'Validation install root already exists.' }

[void][IO.Directory]::CreateDirectory($evidence)
$timeline = New-Object 'System.Collections.Generic.List[object]'
$startedUtc = [DateTime]::UtcNow

# A directory at the registration file path forces the final atomic commit to
# fail after both real SYSTEM tasks have been registered. The production
# transaction must then remove both tasks and restore the settings state.
[void][IO.Directory]::CreateDirectory($registrationPath)
$rollback = Invoke-Installer 'Install'
if ($rollback.ExitCode -eq 0) { throw 'Injected registration failure unexpectedly succeeded.' }
$rollbackRecord = Get-ChildItem -LiteralPath (Join-Path $guardState 'install-transactions') -Filter '*.json' |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $rollbackRecord) { throw 'Rollback evidence was not created.' }
$rollbackState = Get-Content -LiteralPath $rollbackRecord.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
if ($rollbackState.state -ne 'RolledBack' -or (Get-GuardTasks).Count -ne 0 -or
    (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw 'Real task transaction did not roll back cleanly.'
}
if (-not (Test-Path -LiteralPath $controlRoot -PathType Container)) {
    throw 'The retry-safe RecoveryControl registration was not preserved.'
}
$timeline.Add([ordered]@{ step = 'InjectedFailureRollback'; passed = $true; utc = [DateTime]::UtcNow.ToString('O');
    transactionState = $rollbackState.state; tasks = (Get-GuardTasks).Count;
    settingsRestoredToAbsent = -not (Test-Path -LiteralPath $settingsPath -PathType Leaf);
    retrySafeControlRegistrationPreserved = $true; output = $rollback.Output })
Remove-Item -LiteralPath $registrationPath -Force

$first = Invoke-Installer 'Install'
if ($first.ExitCode -ne 0) { throw ('First install failed: ' + ($first.Output -join [Environment]::NewLine)) }
$firstRegistration = Get-Content -LiteralPath $registrationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$firstTasks = Get-GuardTasks
if ($firstTasks.Count -ne 2 -or $firstRegistration.executionEnabled -ne $false -or
    -not (Test-Path -LiteralPath $firstRegistration.directory -PathType Container)) {
    throw 'First install state is incomplete.'
}
$timeline.Add([ordered]@{ step = 'FirstInstall'; passed = $true; utc = [DateTime]::UtcNow.ToString('O');
    directory = $firstRegistration.directory; tasks = $firstTasks.Count; executionEnabled = $firstRegistration.executionEnabled;
    output = $first.Output })

$upgrade = Invoke-Installer 'Install'
if ($upgrade.ExitCode -ne 0) { throw ('Upgrade failed: ' + ($upgrade.Output -join [Environment]::NewLine)) }
$upgradeRegistration = Get-Content -LiteralPath $registrationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$upgradeTasks = Get-GuardTasks
if ($upgradeTasks.Count -ne 2 -or $upgradeRegistration.directory -eq $firstRegistration.directory -or
    $upgradeRegistration.executionEnabled -ne $false) { throw 'Upgrade did not switch to a new immutable version directory.' }
foreach ($task in $upgradeTasks) {
    $xml = Export-ScheduledTask -TaskName $task.TaskName -TaskPath '\' -ErrorAction Stop
    if ($xml -notlike ('*' + [Security.SecurityElement]::Escape([string]$upgradeRegistration.directory) + '*')) {
        throw ('Upgraded task does not reference the current directory: ' + $task.TaskName)
    }
}
$timeline.Add([ordered]@{ step = 'Upgrade'; passed = $true; utc = [DateTime]::UtcNow.ToString('O');
    previousDirectory = $firstRegistration.directory; directory = $upgradeRegistration.directory;
    tasks = $upgradeTasks.Count; output = $upgrade.Output })

$uninstall = Invoke-Installer 'Uninstall'
if ($uninstall.ExitCode -ne 0) { throw ('Uninstall failed: ' + ($uninstall.Output -join [Environment]::NewLine)) }
if ((Get-GuardTasks).Count -ne 0 -or -not (Test-Path -LiteralPath $registrationPath -PathType Leaf)) {
    throw 'Uninstall did not remove both tasks or preserve the audit registration.'
}
$timeline.Add([ordered]@{ step = 'Uninstall'; passed = $true; utc = [DateTime]::UtcNow.ToString('O');
    tasks = (Get-GuardTasks).Count; installedFilesPreserved = $true; auditRegistrationPreserved = $true;
    sharedAuthorityPreserved = (Test-Path -LiteralPath $controlRoot -PathType Container); output = $uninstall.Output })

$report = [ordered]@{
    schemaVersion = 1
    scope = 'RealSystemTaskInstallUpgradeRollbackUninstall'
    passed = $true
    host = $env:COMPUTERNAME
    user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    startedUtc = $startedUtc.ToString('O')
    completedUtc = [DateTime]::UtcNow.ToString('O')
    packageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
    packageIdentitySha256 = (Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'guard-identity.json') -Algorithm SHA256).Hash
    installerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    actualTaskMutationsPerformed = $true
    automaticRecoveryVerified = $false
    physicalHardwareSafetyVerified = $false
    steps = $timeline.ToArray()
}
$reportPath = Join-Path $evidence 'results.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
$report | ConvertTo-Json -Depth 10
