[CmdletBinding()]
param([string]$InstallerPath = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'
}
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    [IO.Path]::GetFullPath($InstallerPath), [ref]$tokens, [ref]$errors)
if (@($errors).Count) { throw 'InstallerIdentityTestParseFailed' }
foreach ($functionName in @('Resolve-SafeDirectory', 'Read-Utf8JsonFile',
        'Assert-RequiredProgramFiles', 'Get-VerifiedDeploymentIdentity',
        'Test-CurrentSlotReplacementRequired', 'Assert-InstalledPackageMatchesSource',
        'Ensure-V3BaselineLastKnownGood',
        'Get-InstalledEngineHosts', 'Confirm-InstalledEngineRetirement',
        'Stop-VerifiedInstalledEngineHosts')) {
    $definition = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq $functionName
    }, $true))
    if ($definition.Count -ne 1) { throw "InstallerFunctionMissing:$functionName" }
    Invoke-Expression $definition[0].Extent.Text
}
function Assert-Throws([scriptblock]$Action, [string]$Label) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw "ExpectedRejection:$Label" }
}
function Write-FixtureIdentity([string]$Directory, [string]$Commit, [bool]$Approved = $false) {
    $files = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object {
        $_.Name -ne 'build-identity.json'
    } | ForEach-Object {
        [ordered]@{ name=$_.Name; bytes=$_.Length;
            sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [IO.File]::WriteAllText((Join-Path $Directory 'build-identity.json'),
        ([ordered]@{ gitCommit=$Commit; packageContentSha256=('a' * 64); files=$files;
            deploymentApproved=$Approved;watchdogSchema=7;
            recoveryArchitectureGeneration='EPB-RecoveryKernel-V3' } |
            ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
}
$temporary = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) `
    ('EPBTest-InstallerIdentity-' + [Guid]::NewGuid().ToString('N'))))
$savedProgramData = $env:ProgramData
try {
    $source = Join-Path $temporary 'Source'
    $root = Join-Path $temporary 'MTTFTest'
    $current = Join-Path $root 'Current'
    [void](New-Item -ItemType Directory -Path $source,$current -Force)
    $binary = Join-Path $PSScriptRoot '..\MTTfTest\bin\Release\MTTFTest.exe'
    Copy-Item -LiteralPath $binary -Destination (Join-Path $source 'MTTFTest.exe')
    foreach ($name in @('MTTFTest.EngineHost.exe','MTTFTest.Recovery.Kernel.dll',
            'MTTFTest.Watchdog.exe','MTTFTest.SessionAgent.exe','MTTFTest.SafetyAgent.exe',
            'MTTFTest.Watchdog.Protocol.dll')) {
        [IO.File]::WriteAllText((Join-Path $source $name), "fixture:$name")
    }
    Copy-Item -LiteralPath $binary -Destination (Join-Path $source 'MTTFTest.EngineHost.exe') -Force
    Write-FixtureIdentity $source ('1' * 40)
    Get-ChildItem -LiteralPath $source | Copy-Item -Destination $current
    if (Test-CurrentSlotReplacementRequired $source $root) { throw 'IdenticalPackageReplaced' }
    [void](Assert-InstalledPackageMatchesSource $source $root)
    $lkg = Join-Path $root 'LastKnownGood'
    [void](New-Item -ItemType Directory -Path $lkg -Force)
    Get-ChildItem -LiteralPath $current | Copy-Item -Destination $lkg
    Write-FixtureIdentity $lkg ('2' * 40)
    Ensure-V3BaselineLastKnownGood $root
    if ((Get-VerifiedDeploymentIdentity $lkg).GitCommit -ne ('1' * 40)) {
        throw 'UnapprovedOldCandidateLkgWasKept'
    }
    Write-FixtureIdentity $lkg ('3' * 40) $true
    Ensure-V3BaselineLastKnownGood $root
    if ((Get-VerifiedDeploymentIdentity $lkg).GitCommit -ne ('3' * 40)) {
        throw 'ApprovedCompatibleLkgWasReplaced'
    }
    Write-Output 'PASS V3CandidateBaselineDoesNotMasqueradeAsApprovedLkg 2/2'
    Write-FixtureIdentity $current ('2' * 40)
    if (-not (Test-CurrentSlotReplacementRequired $source $root)) { throw 'SameVersionPatchSkipped' }
    Assert-Throws { Assert-InstalledPackageMatchesSource $source $root } 'OldCurrentMustNotPass'
    Copy-Item -LiteralPath (Join-Path $source 'build-identity.json') -Destination $current -Force
    [IO.File]::AppendAllText((Join-Path $current 'MTTFTest.EngineHost.exe'), 'tamper')
    if (-not (Test-CurrentSlotReplacementRequired $source $root)) { throw 'CorruptCurrentNotRepaired' }
    Assert-Throws { Assert-InstalledPackageMatchesSource $source $root } 'CorruptCurrentMustNotPass'
    [IO.File]::AppendAllText((Join-Path $source 'MTTFTest.EngineHost.exe'), 'tamper')
    Assert-Throws { Test-CurrentSlotReplacementRequired $source $root } 'CorruptSourceMustNotInstall'
    if (-not (Test-Path -LiteralPath (Join-Path $current 'MTTFTest.exe'))) {
        throw 'ReadOnlyIdentityChecksModifiedCurrent'
    }
    Write-Output 'PASS V3SameVersionPackageIdentity 7/7'

    $env:ProgramData = Join-Path $temporary 'ProgramData'
    & {
        $script:fixtureProcess = [pscustomobject]@{
            ProcessId=12345;CreationDate='2026-09-04T00:00:00Z';
            ExecutablePath=(Join-Path $current 'MTTFTest.EngineHost.exe')
        }
        function Get-CimInstance { return $script:fixtureProcess }
        function Read-Host { return 'CANCEL' }
        Assert-Throws { Confirm-InstalledEngineRetirement $root $false } 'IsolationNotConfirmed'
        $approved = @(Confirm-InstalledEngineRetirement $root $true)
        if ($approved.Count -ne 1) { throw 'EngineRetirementIdentityMissing' }
        $script:fixtureProcess = [pscustomobject]@{
            ProcessId=12345;CreationDate='2026-09-04T00:01:00Z';
            ExecutablePath=(Join-Path $current 'MTTFTest.EngineHost.exe')
        }
        $script:unexpectedKill = $false
        function Stop-Process { $script:unexpectedKill = $true; throw 'UnexpectedProcessKill' }
        Assert-Throws { Stop-VerifiedInstalledEngineHosts $root $approved } 'PidReuseRejected'
        if ($script:unexpectedKill) { throw 'PidReuseReachedKillOperation' }
        $script:fixtureProcess.ExecutablePath = 'C:\Foreign\MTTFTest.EngineHost.exe'
        Assert-Throws { Get-InstalledEngineHosts $root } 'ForeignEngineRejected'
        $script:fixtureProcess.ExecutablePath = ''
        Assert-Throws { Get-InstalledEngineHosts $root } 'UnknownEngineRejected'
    }
    Write-Output 'PASS V3EngineRetirementSafetyPreflight 5/5'
    $text = [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8)
    $retire = $text.LastIndexOf('    Stop-VerifiedInstalledEngineHosts $root $approvedEngines')
    $copy = $text.LastIndexOf('        [void](Install-CurrentSlot $source $root)')
    if ($retire -lt 0 -or $retire -ge $copy -or
        -not $text.Contains('Disable-ScheduledTask') -or
        -not $text.Contains('$installedIdentity = Assert-InstalledPackageMatchesSource $Source $Root')) {
        throw 'InstallerRetirementOrPostInstallVerificationMissing'
    }
    Write-Output 'PASS V3DeploymentPassRequiresInstalledIdentity 1/1'
}
finally {
    $env:ProgramData = $savedProgramData
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\EPBTest-InstallerIdentity-'
    if ($temporary.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $temporary)) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
