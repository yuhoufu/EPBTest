param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-DeploymentResult'
}, $true)
if ($null -eq $definition) { throw 'Result function missing' }
$calls = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Write-DeploymentResult'
}, $true))
if ($calls.Count -ne 2) { throw 'Expected install and uninstall result calls' }
foreach ($call in $calls) {
    $pipeline = $call.Parent
    $statements = @($pipeline.Parent.Statements)
    $index = [Array]::IndexOf($statements, $pipeline)
    if ($index -lt 0 -or $index + 1 -ge $statements.Count -or
        $statements[$index + 1].PipelineElements[0].GetCommandName() -ne 'Write-Host') {
        throw 'Completion announcement must immediately follow durable result publication'
    }
    $sequence = [scriptblock]::Create($pipeline.Extent.Text + [Environment]::NewLine + $statements[$index + 1].Extent.Text)
    & {
        $script:completionAnnounced = $false
        function Write-DeploymentResult { throw 'InjectedPublicationFailure' }
        function Write-Host { $script:completionAnnounced = $true }
        $caught = $null
        try { & $sequence } catch { $caught = $_ }
        if ($null -eq $caught -or $caught.Exception.Message -ne 'InjectedPublicationFailure' -or $script:completionAnnounced) {
            throw 'Publication failure incorrectly announced completion'
        }
    }
}
Write-Output 'PASS install and uninstall publication failure suppresses completion announcement'
. ([scriptblock]::Create($definition.Extent.Text))
function Get-Service { [CmdletBinding()]param($Name) }
function Get-ScheduledTask { [CmdletBinding()]param($TaskName) }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('epb-deploy-result-' + [Guid]::NewGuid().ToString('N'))
$originalProgramData = $env:ProgramData
try {
    $env:ProgramData = $fixture
    Write-DeploymentResult 'Install' 'fixture-1' 'C:\FixtureOnly\MTTFTest'
    $resultRoot = Join-Path $fixture 'MTTFTest\DeploymentLogs'
    $resultPath = Join-Path $resultRoot 'last-deployment-result.json'
    $before = (Get-FileHash $resultPath).Hash
    $locked = [IO.File]::Open($resultPath, 'Open', 'Read', 'None')
    $caught = $null
    try { Write-DeploymentResult 'Repair' 'fixture-2' 'C:\FixtureOnly\MTTFTest' } catch { $caught = $_ }
    finally { $locked.Dispose() }
    if ($null -eq $caught -or (Get-FileHash $resultPath).Hash -ne $before) { throw 'Locked result not preserved' }
    $pending = @(Get-ChildItem -LiteralPath $resultRoot -Filter '*.pending-*')
    if ($pending.Count -ne 1) { throw 'Failed result evidence missing' }
    Write-DeploymentResult 'Repair' 'fixture-3' 'C:\FixtureOnly\MTTFTest'
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    $backups = @(Get-ChildItem -LiteralPath $resultRoot -Filter '*.previous-*')
    if ($result.version -ne 'fixture-3' -or $backups.Count -ne 1 -or
        (Get-FileHash $backups[0].FullName).Hash -ne $before -or -not [IO.File]::Exists($pending[0].FullName)) {
        throw 'Result retry or history preservation failed'
    }
    Write-Output "PASS deployment result create, locked rejection, retry and history; Evidence=$fixture"
} finally { $env:ProgramData = $originalProgramData }
