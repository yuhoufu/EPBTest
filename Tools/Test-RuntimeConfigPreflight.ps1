# Isolated process/environment fixture; never load the installer entry point.
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$parseTokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'), [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'InstallerParseFailed' }
$found = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Initialize-RuntimeConfig'
}, $true))
if ($found.Count -ne 1) { throw 'ConfigFunctionMissing' }
. ([scriptblock]::Create($found[0].Extent.Text))
$publisher = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Publish-MissingRuntimeConfig'
}, $true))
if ($publisher.Count -ne 1) { throw 'ConfigPublisherMissing' }
. ([scriptblock]::Create($publisher[0].Extent.Text))
foreach ($name in @('Get-DeploymentFileSha256', 'Undo-InitializedRuntimeConfigs')) {
    $helper = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true))
    if ($helper.Count -ne 1) { throw "ConfigHelperMissing:$name" }
    . ([scriptblock]::Create($helper[0].Extent.Text))
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('EpbConfigTests-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'Source'
$install = Join-Path $fixture 'Install'
[void](New-Item -ItemType Directory -Path (Join-Path $source 'Config'), (Join-Path $install 'Current\Config'))
[void](New-Item -ItemType File -Path (Join-Path $source 'Config\a.xml') -Value 'source-a')
$runtimeConfigNames = @('a.xml', 'b.xml')
$savedProgramData = $env:ProgramData
try {
    # Only this child process sees the redirected environment.
    $env:ProgramData = Join-Path $fixture 'State'
    $target = Join-Path $env:ProgramData 'MTTFTest\Config'
    $rejected = $false
    try { Initialize-RuntimeConfig $source $install }
    catch { if ($_.Exception.Message -notlike '*b.xml') { throw }; $rejected = $true }
    if (-not $rejected -or (Test-Path -LiteralPath $target)) { throw 'MissingTemplateLeftPartialConfig' }
    Write-Output 'PASS missing later template produces no partial configuration'
    [void](New-Item -ItemType Directory -Path $target)
    [void](New-Item -ItemType File -Path (Join-Path $target 'a.xml') -Value 'user-a')
    [void](New-Item -ItemType File -Path (Join-Path $source 'Config\b.xml') -Value 'source-b')
    [void](New-Item -ItemType File -Path (Join-Path $install 'Current\Config\b.xml') -Value 'previous-b')
    Initialize-RuntimeConfig $source $install
    if ((Get-Content -LiteralPath (Join-Path $target 'a.xml') -Raw) -ne 'user-a' -or
        (Get-Content -LiteralPath (Join-Path $target 'b.xml') -Raw) -ne 'previous-b') { throw 'ExistingConfigOrPreviousPriorityLost' }
    Write-Output 'PASS user configuration preserved and previous template preferred'
    $env:ProgramData = Join-Path $fixture 'FreshState'
    Initialize-RuntimeConfig $source $install
    if ((Get-Content -LiteralPath (Join-Path $env:ProgramData 'MTTFTest\Config\a.xml') -Raw) -ne 'source-a') { throw 'SourceFallbackFailed' }
    Write-Output 'PASS source template fallback'
    $collisionTarget = Join-Path $fixture 'collision.xml'
    [void](New-Item -ItemType File -Path $collisionTarget -Value 'user-content')
    $blocked = $false
    try { Publish-MissingRuntimeConfig (Join-Path $source 'Config\a.xml') $collisionTarget }
    catch { $blocked = $true }
    $pending = @(Get-ChildItem -LiteralPath $fixture -File -Filter '.config-pending-*')
    if (-not $blocked -or (Get-Content -LiteralPath $collisionTarget -Raw) -ne 'user-content' -or
        $pending.Count -ne 1 -or (Get-Content -LiteralPath $pending[0].FullName -Raw) -ne 'source-a') { throw 'CollisionOverwroteConfigOrLostEvidence' }
    Write-Output 'PASS target collision preserves user content and complete pending file'
    $missingTarget = Join-Path $fixture 'must-not-exist.xml'
    $blocked = $false
    try { Publish-MissingRuntimeConfig (Join-Path $fixture 'missing-source') $missingTarget }
    catch { $blocked = $true }
    if (-not $blocked -or (Test-Path -LiteralPath $missingTarget)) { throw 'FailedReadPublishedTarget' }
    Write-Output 'PASS failed source read does not publish target'
    . ([scriptblock]::Create($publisher[0].Extent.Text.Replace('function Publish-MissingRuntimeConfig(', 'function Invoke-FixtureConfigPublisher(')))
    $script:publicationCount = 0
    $script:lateSource = Join-Path $install 'Current\Config\b.xml'
    function Publish-MissingRuntimeConfig([string]$Source, [string]$Target) {
        $receipt = Invoke-FixtureConfigPublisher $Source $Target
        $script:publicationCount++
        if ($script:publicationCount -eq 1) { [IO.File]::Move($script:lateSource, ($script:lateSource + '.held')) }
        return $receipt
    }
    $env:ProgramData = Join-Path $fixture 'InjectedFailureState'
    $failedConfigRoot = Join-Path $env:ProgramData 'MTTFTest\Config'
    $blocked = $false
    try { Initialize-RuntimeConfig $source $install }
    catch { $blocked = $true }
    $savedConfigs = @(Get-ChildItem -LiteralPath $failedConfigRoot -File -Filter '.config-rollback-*')
    if (-not $blocked -or (Test-Path -LiteralPath (Join-Path $failedConfigRoot 'a.xml')) -or
        (Test-Path -LiteralPath (Join-Path $failedConfigRoot 'b.xml')) -or $savedConfigs.Count -ne 1 -or
        (Get-Content -LiteralPath $savedConfigs[0].FullName -Raw) -ne 'source-a') { throw 'PartialPublicationNotReverted' }
    Write-Output 'PASS later publication failure withdraws earlier new config and preserves copy'
    $changed = Join-Path $fixture 'changed.xml'
    [void](New-Item -ItemType File -Path $changed -Value 'external-edit')
    $blocked = $false
    try { Undo-InitializedRuntimeConfigs $fixture @([pscustomobject]@{Target=$changed; Sha256=('0' * 64)}) }
    catch { if ($_.Exception.Message -notlike 'ConfigRollbackIncomplete:*') { throw }; $blocked = $true }
    if (-not $blocked -or (Get-Content -LiteralPath $changed -Raw) -ne 'external-edit') { throw 'RollbackMovedExternalEdit' }
    Write-Output 'PASS changed config rejected by rollback without moving it'
}
finally { $env:ProgramData = $savedProgramData }
Write-Output "PASS 7/7; InstallationPerformed=false; PreservedFixture=$fixture"
