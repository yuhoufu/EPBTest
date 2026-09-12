#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Plan','Execute')][string]$Mode = 'Plan',
    [Parameter(Mandatory=$true)][string]$GuardPackageDirectory,
    [Parameter(Mandatory=$true)][string]$MainReleaseDirectory,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [string]$InstalledMainDirectory = 'C:\Program Files (x86)\MTTFTest\Current',
    [ValidateRange(1,15)][int]$WindowMinutes = 10
)
$ErrorActionPreference = 'Stop'
$guard = [IO.Path]::GetFullPath($GuardPackageDirectory)
$main = [IO.Path]::GetFullPath($MainReleaseDirectory)
$installed = [IO.Path]::GetFullPath($InstalledMainDirectory)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'CommissioningRequiresAdministrator' }

# Both modes validate immutable packages and the actual installed component set.
# This tool never promotes a package, installs a task, stops Main or changes intent.
& (Join-Path $guard 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Validate -SourceDirectory $guard | Out-Null
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -ReleaseDirectory $main | Out-Null
$identity = Get-Content (Join-Path $guard 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($identity.schemaVersion -ne 2 -or $identity.deliveryStage -ne 'ObserveOnlyCommissioning' -or
    $identity.builtFromVerifiedInputs -ne $true -or $identity.gitDirty -ne $false -or
    $identity.configuration -ne 'Release' -or $identity.supervisedCommissioningAvailable -ne $true) {
    throw 'CommissioningRequiresVerifiedObservePackage'
}
$mainIdentity = Get-Content (Join-Path $main 'build-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$identity.gitCommit) -or
    $identity.gitCommit.Trim() -cne ([string]$mainIdentity.gitCommit).Trim()) {
    throw 'CommissioningMainGuardCommitMismatch'
}
$bindings = [ordered]@{}
foreach ($name in @('build-identity.json','MTTFTest.exe','MTTFTest.RecoveryControl.dll',
    'MTTFTest.Watchdog.exe','MTTFTest.SessionAgent.exe','MTTFTest.SafetyAgent.exe')) {
    $expected = (Get-FileHash -LiteralPath (Join-Path $main $name)).Hash
    if ((Get-FileHash -LiteralPath (Join-Path $installed $name)).Hash -ne $expected) {
        throw "CommissioningInstalledComponentMismatch:$name"
    }
    $bindings[$name] = $expected
}
if ((Get-FileHash (Join-Path $guard 'MTTFTest.RecoveryControl.dll')).Hash -ne $bindings['MTTFTest.RecoveryControl.dll']) {
    throw 'CommissioningSharedComponentMismatch'
}
$bindings['guardExecutable'] = (Get-FileHash (Join-Path $guard 'MTTFTest.RecoveryGuard.exe')).Hash
$bindings['guardIdentity'] = (Get-FileHash (Join-Path $guard 'guard-identity.json')).Hash
$task = Get-ScheduledTask -TaskName MTTFTestRecoveryGuardExecution -ErrorAction SilentlyContinue
if ($null -ne $task -and $task.State -ne 'Disabled') { throw 'CommissioningRequiresAutomaticTaskDisabled' }
$raw = & (Join-Path $guard 'MTTFTest.RecoveryGuard.exe') --status
if ($LASTEXITCODE -ne 0) { throw 'CommissioningAuthorityUnreadable' }
$state = ($raw -join '') | ConvertFrom-Json
if ($null -eq $state.Intent -or $state.Intent.DesiredState -ne 0 -or [string]::IsNullOrWhiteSpace([string]$state.Intent.AuthorizationId)) {
    throw 'CommissioningRequiresExistingRunIntent'
}
if ([IO.Path]::GetFullPath([string]$state.MainExecutablePath) -ine (Join-Path $installed 'MTTFTest.exe')) {
    throw 'CommissioningAuthorityMainMismatch'
}
$planPath = Join-Path $evidence 'commissioning-plan.json'
if ($Mode -eq 'Plan') {
    if (Test-Path -LiteralPath $evidence) { throw 'CommissioningEvidenceDirectoryAlreadyExists' }
    [void](New-Item -ItemType Directory -Path $evidence)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl',
            'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $evidence -AclObject $acl
    [ordered]@{ schemaVersion=1; kind='RecoveryGuardSupervisedCommissioning'; productionAcceptance=$false;
        machineName=$env:COMPUTERNAME; guardDirectory=$guard; mainDirectory=$main; installedDirectory=$installed;
        installationId=$state.InstallationId; authorizationId=$state.Intent.AuthorizationId;
        intentVersion=$state.Intent.IntentVersion; bindings=$bindings;
        expiresUtc=[DateTime]::UtcNow.AddMinutes($WindowMinutes).ToString('O'); mode='RecoverExited'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $planPath -Encoding UTF8
    Write-Output "Commissioning plan only; no recovery dispatched: $planPath"
    return
}
$plan = Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($plan.schemaVersion -ne 1 -or $plan.kind -cne 'RecoveryGuardSupervisedCommissioning' -or
    $plan.productionAcceptance -ne $false -or $plan.mode -cne 'RecoverExited' -or
    $plan.machineName -ine $env:COMPUTERNAME -or $plan.guardDirectory -ine $guard -or
    $plan.mainDirectory -ine $main -or $plan.installedDirectory -ine $installed -or
    $plan.installationId -cne $state.InstallationId -or $plan.authorizationId -cne $state.Intent.AuthorizationId -or
    $plan.intentVersion -ne $state.Intent.IntentVersion) { throw 'CommissioningPlanScopeMismatch' }
foreach ($key in $bindings.Keys) {
    if ($plan.bindings.$key -ine $bindings[$key]) { throw "CommissioningPlanBinaryChanged:$key" }
}
$expiry = [DateTime]::Parse([string]$plan.expiresUtc, [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind)
if ($expiry.Kind -ne [DateTimeKind]::Utc -or $expiry -le [DateTime]::UtcNow -or
    $expiry -gt [DateTime]::UtcNow.AddMinutes(15)) { throw 'CommissioningPlanExpiredOrInvalid' }
# Claim before launching. An uncertain invocation cannot reuse this plan.
$claim = [IO.File]::Open((Join-Path $evidence 'execution-claimed'), [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $claim.Flush($true) } finally { $claim.Dispose() }
$settings = Get-Content (Join-Path $guard 'guard-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$settings.Mode = 1 # RecoverExited; validated again by the executable.
$settingsPath = Join-Path $evidence 'commissioning-settings.json'
$settings | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
& (Join-Path $guard 'MTTFTest.RecoveryGuard.exe') --execute --settings $settingsPath --journal $evidence `
    --commission-until-utc $expiry.ToString('O') --commission-installation $plan.installationId `
    --commission-authorization $plan.authorizationId --commission-intent-version ([string]$plan.intentVersion) |
    Tee-Object -FilePath (Join-Path $evidence 'execution-output.jsonl')
$result = $LASTEXITCODE
[ordered]@{ exitCode=$result; completedUtc=[DateTime]::UtcNow.ToString('O'); productionAcceptance=$false;
    note='Exit code and process presence do not prove physical safety or business recovery; reconcile durable evidence.'
} | ConvertTo-Json | Set-Content (Join-Path $evidence 'execution-result.json') -Encoding UTF8
if ($result -ne 0) { throw "CommissioningNeedsReconciliation:$result" }
