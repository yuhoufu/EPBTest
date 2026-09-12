#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GuardPackageDirectory,
    [Parameter(Mandatory=$true)][string]$MainReleaseDirectory,
    [Parameter(Mandatory=$true)][string]$AcceptanceReportPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (@(git -C $repo status --porcelain).Count -ne 0 -or $LASTEXITCODE -ne 0) { throw 'AutomaticPublishRequiresCleanSource' }
$head = [string](& git -C $repo rev-parse HEAD)
$source = [IO.Path]::GetFullPath($GuardPackageDirectory)
$main = [IO.Path]::GetFullPath($MainReleaseDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (Test-Path $output) { throw 'AutomaticOutputAlreadyExists' }
if (Test-Path ($output + '.zip')) { throw 'AutomaticArchiveAlreadyExists' }
$base = Get-Content (Join-Path $source 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$baseIdentityBytes=[IO.File]::ReadAllBytes((Join-Path $source 'guard-identity.json'))
$baseIdentityHash=(Get-FileHash (Join-Path $source 'guard-identity.json')).Hash
if ($base.schemaVersion -ne 2 -or $base.deliveryStage -ne 'ObserveOnlyCommissioning' -or
    $base.configuration -ne 'Release' -or $base.builtFromVerifiedInputs -ne $true -or
    $base.gitDirty -ne $false -or $base.gitCommit.Trim() -cne $head.Trim()) { throw 'AutomaticPublishRequiresMatchingVerifiedGuard' }
if ((Get-FileHash (Join-Path $source 'Install-MTTFTest-RecoveryGuard.ps1')).Hash -ne
    (Get-FileHash (Join-Path $PSScriptRoot 'Install-MTTFTest-RecoveryGuard.ps1')).Hash) { throw 'AutomaticInstallerSourceMismatch' }
& (Join-Path $source 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Validate -SourceDirectory $source | Out-Null
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -ReleaseDirectory $main | Out-Null
. (Join-Path $PSScriptRoot 'RecoveryGuard-Acceptance.ps1')
. (Join-Path $PSScriptRoot 'RecoveryGuard-Archive.ps1')
$reportFile = [IO.Path]::GetFullPath($AcceptanceReportPath)
$reportBytes = [IO.File]::ReadAllBytes($reportFile)
$report = [Text.Encoding]::UTF8.GetString($reportBytes).TrimStart([char]0xfeff) | ConvertFrom-Json
$mainHash = (Get-FileHash (Join-Path $main 'build-identity.json')).Hash
$guardHash = (Get-FileHash (Join-Path $source 'MTTFTest.RecoveryGuard.exe')).Hash
$coreHash = (Get-FileHash (Join-Path $source 'MTTFTest.RecoveryControl.dll')).Hash
if ((Get-FileHash (Join-Path $main 'MTTFTest.RecoveryControl.dll')).Hash -ne $coreHash) { throw 'AutomaticSharedCoreMismatch' }
[void](Assert-GuardAcceptanceReport $report RecoverExited $mainHash $guardHash $coreHash $baseIdentityHash)
$evidenceRoot = Split-Path $reportFile -Parent
Assert-GuardAcceptanceEvidenceFiles $report $evidenceRoot
$parent = Split-Path $output -Parent
[void](New-Item -ItemType Directory -Path $parent -Force)
$staging = Join-Path $parent ('.automatic-staging-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $staging)
Get-ChildItem $source -File | Copy-Item -Destination $staging
Copy-Item (Join-Path $PSScriptRoot 'RecoveryGuard-Acceptance.ps1') $staging
[IO.File]::WriteAllBytes((Join-Path $staging 'acceptance-report.json'), $reportBytes)
[IO.File]::WriteAllBytes((Join-Path $staging 'observe-base-identity.json'), $baseIdentityBytes)
foreach ($relative in @($report.checks.evidencePath | Sort-Object -Unique)) {
    $target = Join-Path (Join-Path $staging 'Acceptance') $relative
    [void](New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force)
    Copy-Item -LiteralPath (Join-Path $evidenceRoot $relative) -Destination $target
}
Assert-GuardAcceptanceEvidenceFiles $report (Join-Path $staging 'Acceptance')
$files = @(Get-ChildItem $staging -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $staging 'guard-identity.json') } | ForEach-Object {
    [ordered]@{name=$_.FullName.Substring($staging.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}
})
$base.schemaVersion=3; $base.deliveryStage='AutomaticRecovery'; $base.automaticExecutionReady=$true
$base.files=$files
foreach ($entry in @{
    mainIdentitySha256=$mainHash; approvedModes=@($report.approvedModes); acceptanceBenchId=$report.benchId;
    acceptanceMachineName=$report.machineName; automaticPublisherSha256=(Get-FileHash $PSCommandPath).Hash
}.GetEnumerator()) { $base | Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value -Force }
$base | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $staging 'guard-identity.json') -Encoding UTF8
& (Join-Path $staging 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Validate -SourceDirectory $staging -RecoveryMode RecoverExited | Out-Null
if ($head.Trim() -cne ([string](& git -C $repo rev-parse HEAD)).Trim() -or @(git -C $repo status --porcelain).Count -ne 0) {
    throw 'AutomaticPublishSourceChanged'
}
# Recheck the live main identity; staged evidence and Guard files were revalidated above.
if ((Get-FileHash (Join-Path $main 'build-identity.json')).Hash -ne $mainHash) { throw 'AutomaticMainIdentityChanged' }
if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($reportFile)) -cne [Convert]::ToBase64String($reportBytes)) {
    throw 'AutomaticAcceptanceReportChanged'
}
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -ReleaseDirectory $main | Out-Null
if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($staging)) -ine $parent -or
    [IO.Path]::GetDirectoryName($output) -ine $parent) { throw 'AutomaticPublishMoveOutsideParent' }
$published=Publish-GuardArchive $staging $output
[ordered]@{directory=$output;archive=($output+'.zip');sha256=$published.Sha256;
    approvedModes=$report.approvedModes;benchId=$report.benchId;machineName=$report.machineName} | ConvertTo-Json
