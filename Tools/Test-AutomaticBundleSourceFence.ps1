# Isolated Git fixture only; no package generation, source commits or installation.
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'New-AutomaticRecoveryBundle.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'PublisherParseFailed' }
$function = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-AutomaticBundleSourceUnchanged' }, $true))
if ($function.Count -ne 1) { throw 'SourceFenceMissing' }
. ([scriptblock]::Create($function[0].Extent.Text))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('BundleSourceFence-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $fixture)
& git -C $fixture init --quiet
if ($LASTEXITCODE -ne 0) { throw 'FixtureInitFailed' }
function Commit-Fixture {
    & git -C $fixture -c user.name=Fixture -c user.email=fixture@example.invalid commit --allow-empty --no-gpg-sign --quiet -m 'test: fixture'
    if ($LASTEXITCODE -ne 0) { throw 'FixtureCommitFailed' }
}
function Reject-Fence([string]$expected) {
    try { Assert-AutomaticBundleSourceUnchanged $fixture $expected; throw 'ExpectedRejectionMissing' }
    catch { if ($_.Exception.Message -ne 'AutomaticBundleSourceChanged') { throw } }
}
Commit-Fixture
$original = ([string](& git -C $fixture rev-parse HEAD)).Trim()
Assert-AutomaticBundleSourceUnchanged $fixture $original
Write-Output 'PASS unchanged clean source accepted'
Reject-Fence 'invalid'
Write-Output 'PASS invalid expected source rejected'
Commit-Fixture
Reject-Fence $original
Write-Output 'PASS changed HEAD rejected'
$current = ([string](& git -C $fixture rev-parse HEAD)).Trim()
[void](New-Item -ItemType File -Path (Join-Path $fixture 'untracked.txt'))
Reject-Fence $current
Write-Output 'PASS untracked source change rejected'
Write-Output "PASS 4/4; isolated fixture retained: $fixture"
