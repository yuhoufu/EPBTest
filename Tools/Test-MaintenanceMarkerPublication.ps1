param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Enter-DeploymentMaintenance'
}, $true)
if ($null -eq $definition) { throw 'Maintenance function missing' }
. ([scriptblock]::Create($definition.Extent.Text))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('epb-maintenance-' + [Guid]::NewGuid().ToString('N'))
$originalProgramData = $env:ProgramData
try {
    $env:ProgramData = $fixture
    $Mode = 'Repair'
    $root = 'C:\FixtureOnly\MTTFTest'
    Enter-DeploymentMaintenance
    $state = Join-Path $fixture 'MTTFTest'
    $marker = Join-Path $state 'maintenance-inhibit.json'
    $receipt = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($receipt.Mode -ne $Mode -or $receipt.InstallRoot -ne $root -or
        [string]::IsNullOrWhiteSpace($receipt.StartedUtc)) { throw 'Invalid published marker' }
    if (@(Get-ChildItem -LiteralPath $state -Filter '*.pending-*').Count -ne 0) {
        throw 'Successful publication left pending file'
    }
    $firstHash = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
    $Mode = 'Configure'
    Enter-DeploymentMaintenance
    if ((Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -ne $firstHash) {
        throw 'Existing maintenance owner overwritten'
    }
    Write-Output 'PASS complete maintenance JSON publication and existing owner preservation'
    # Force the existence check to observe an absent marker while the destination
    # already exists. The actual File.Move must reject, not overwrite, that owner.
    function Test-Path {
        param([string]$LiteralPath)
        if ($LiteralPath -eq $marker) { return $false }
        Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath
    }
    $publicationError = $null
    try { Enter-DeploymentMaintenance } catch { $publicationError = $_ }
    if ($null -eq $publicationError) { throw 'Conflicting marker publication succeeded' }
    if ((Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -ne $firstHash) {
        throw 'Conflicting publication changed existing marker'
    }
    $pending = @(Get-ChildItem -LiteralPath $state -Filter '*.pending-*')
    if ($pending.Count -ne 1) { throw 'Failed publication did not retain one pending file' }
    $candidate = Get-Content -LiteralPath $pending[0].FullName -Raw | ConvertFrom-Json
    if ($candidate.Mode -ne 'Configure' -or $candidate.InstallRoot -ne $root) {
        throw 'Retained candidate is incomplete'
    }
    Write-Output 'PASS conflicting publication rejects and retains both original and complete candidate'
    Write-Output "Evidence: $fixture"
} finally {
    $env:ProgramData = $originalProgramData
}
