#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference='Stop'
# Reuse explicitly synthetic report fixtures; no installed state or tasks are touched.
. (Join-Path $PSScriptRoot 'Test-RecoveryGuard-Acceptance.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-MTTFTest-RecoveryGuard.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failure'}
foreach($name in @('Read-VerifiedPackage','Assert-GuardAcceptanceTarget')) {
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    if($null -eq $node){throw "Missing function $name"}
    . ([scriptblock]::Create($node.Extent.Text))
}
$folder=Join-Path $root 'SyntheticPackage'
[void](New-Item -ItemType Directory -Path $folder)
Get-ChildItem -LiteralPath $PackageDirectory -File | Copy-Item -Destination $folder
Copy-Item (Join-Path $folder 'guard-identity.json') (Join-Path $folder 'observe-base-identity.json')
# This is already an explicitly synthetic, non-installable target. Normalize
# source provenance in the fixture so the parser tests also accept dev inputs;
# never modify the source package or use this report for actual installation.
$fixtureBase=Get-Content (Join-Path $folder 'observe-base-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$fixtureBase.gitDirty=$false; $fixtureBase.configuration='Release'; $fixtureBase.builtFromVerifiedInputs=$true
$fixtureBase | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $folder 'observe-base-identity.json') -Encoding UTF8
Copy-Item (Join-Path $PSScriptRoot 'RecoveryGuard-Acceptance.ps1') $folder
[void](New-Item -ItemType Directory -Path (Join-Path $folder 'Acceptance'))
Copy-Item (Join-Path $root 'fixture.txt') (Join-Path $folder 'Acceptance\fixture.txt')
$report=New-Fixture
$report.guardExecutableSha256=(Get-FileHash (Join-Path $folder 'MTTFTest.RecoveryGuard.exe')).Hash
$report.recoveryControlSha256=(Get-FileHash (Join-Path $folder 'MTTFTest.RecoveryControl.dll')).Hash
$report.guardPackageIdentitySha256=(Get-FileHash (Join-Path $folder 'observe-base-identity.json')).Hash
# a*64 deliberately has no corresponding actual main package: this fixture cannot authorize a real installation.
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $folder 'acceptance-report.json') -Encoding UTF8
$id=Get-Content (Join-Path $folder 'guard-identity.json') -Raw | ConvertFrom-Json
$id.schemaVersion=3; $id.deliveryStage='AutomaticRecovery'; $id.automaticExecutionReady=$true
$id.gitDirty=$false; $id.configuration='Release'; $id.builtFromVerifiedInputs=$true
foreach($entry in @{mainIdentitySha256=('a'*64);approvedModes=@('RecoverExited');acceptanceBenchId='SYNTHETIC-FIXTURE';acceptanceMachineName='SYNTHETIC-FIXTURE'}.GetEnumerator()) {
    $id | Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value -Force
}
function Save-FixtureIdentity {
    $id.files=@(Get-ChildItem $folder -File -Recurse | Where-Object {$_.FullName -ne (Join-Path $folder 'guard-identity.json')} | ForEach-Object {
        @{name=$_.FullName.Substring($folder.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash}
    })
    $id | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $folder 'guard-identity.json') -Encoding UTF8
}
Save-FixtureIdentity
$verified=Read-VerifiedPackage $folder
$checks=1
$readmeBytes=[IO.File]::ReadAllBytes((Join-Path $folder 'README.md'))
[IO.File]::AppendAllText((Join-Path $folder 'README.md'),'Changed package content after acceptance')
Save-FixtureIdentity
try { Read-VerifiedPackage $folder | Out-Null; throw 'Expected rejection missing' } catch {if($_.Exception.Message -ne 'AcceptanceObserveBaseContentMismatch'){throw}}
$checks++
[IO.File]::WriteAllBytes((Join-Path $folder 'README.md'),$readmeBytes)
Save-FixtureIdentity
$id.acceptanceBenchId='WRONG'; Save-FixtureIdentity
try { Read-VerifiedPackage $folder | Out-Null; throw 'Expected rejection missing' } catch {if($_.Exception.Message -ne 'AcceptanceManifestScopeMismatch'){throw}}
$checks++
$id.acceptanceBenchId='SYNTHETIC-FIXTURE'; Save-FixtureIdentity
$report.checks[0].evidenceSha256='d'*64
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $folder 'acceptance-report.json') -Encoding UTF8
Save-FixtureIdentity
try { Read-VerifiedPackage $folder | Out-Null; throw 'Expected rejection missing' } catch {if($_.Exception.Message -ne 'AcceptanceEvidenceHashMismatch'){throw}}
$checks++
foreach($case in @('bench','machine','main')) {
    $bench='SYNTHETIC-FIXTURE';$machine='SYNTHETIC-FIXTURE'
    if($case -eq 'bench'){$bench='OTHER'}
    if($case -eq 'machine'){$machine='OTHER'}
    [IO.File]::WriteAllText((Join-Path $root 'build-identity.json'),'Synthetic mismatched main identity')
    try { Assert-GuardAcceptanceTarget $verified (Join-Path $root 'MTTFTest.exe') $bench $machine; throw 'Expected rejection missing' }
    catch {if($_.Exception.Message -ne 'AcceptanceInstallTargetMismatch'){throw}}
    $checks++
}
Write-Output "PASS AutomaticAdmission $checks/$checks; synthetic package reader and target checks only; no automatic ZIP, tasks or installation"
