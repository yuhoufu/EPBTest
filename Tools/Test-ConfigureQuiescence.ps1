param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$branch = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -eq '$Mode -eq ''Configure'''
}, $true)
if ($null -eq $branch) { throw 'Configure branch not found' }
$body = $branch.Clauses[0].Item2.Extent.Text
$body = [scriptblock]::Create('[CmdletBinding(SupportsShouldProcess=$true)]param()' +
    [Environment]::NewLine + $body.Substring(1, $body.Length - 2))
foreach ($failAt in @('Declined', 'Assert-RuntimeIdle', 'Stop-Supervisor', 'Stop-InstalledRuntimeTasks', 'Assert-RequiredProgramFiles')) {
    & {
        param($body, $failAt)
        $script:seen = @()
        $root = 'C:\FixtureOnly\MTTFTest'
        foreach ($name in @('Assert-InstalledMainStopped', 'Assert-RuntimeIdle',
            'Enter-DeploymentMaintenance', 'Stop-Supervisor', 'Stop-InstalledRuntimeTasks',
            'Assert-RequiredProgramFiles')) {
            Set-Item -Path ('Function:' + $name) -Value {
                $operation = $MyInvocation.MyCommand.Name
                $script:seen += $operation
                if ($operation -eq $failAt) { throw 'FixtureStop' }
            }
        }
        function Get-InstalledServiceStateStrict {
            $script:seen += 'Get-InstalledServiceStateStrict'
            [pscustomobject]@{ Existed=$true; WasRunning=$true }
        }
        function Get-DeploymentAclSnapshot {
            $script:seen += 'Get-DeploymentAclSnapshot'
            [pscustomobject]@{ Entries=@() }
        }
        function Restore-FailedInstallTransaction {}
        function Archive-MaintenanceInhibitForInstall {}
        $script:deploymentTaskBackup = $null
        $script:deploymentTaskPreparation = $null
        $caught = $null
        try { & $body -WhatIf:($failAt -eq 'Declined') -Confirm:$false } catch { $caught = $_.Exception.Message }
        if ($failAt -eq 'Declined') {
            if ($null -ne $caught -or $script:seen.Count -ne 0) { throw "Declined Configure performed operations: $caught; $($script:seen -join ',')" }
            Write-Output 'PASS Declined Configure performs no branch operations'
            return
        }
        if ($caught -ne 'FixtureStop') { throw "Unexpected result: $caught" }
        $sequence = @('Assert-InstalledMainStopped', 'Assert-RuntimeIdle',
            'Get-InstalledServiceStateStrict', 'Get-DeploymentAclSnapshot',
            'Enter-DeploymentMaintenance', 'Stop-Supervisor', 'Stop-InstalledRuntimeTasks',
            'Assert-RequiredProgramFiles')
        $expected = $sequence[0..([Array]::IndexOf($sequence, $failAt))] -join ','
        if (($script:seen -join ',') -ne $expected) { throw 'Configure order mismatch' }
        Write-Output "PASS Configure stops at $failAt before configuration mutation"
    } $body $failAt
}

# Exercise the actual promotion branch only with real WhatIf; the first operation
# is a trap so a missing guard cannot reach filesystem or service mutations.
$promotion = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -eq '$Mode -eq ''PromoteLastKnownGood'''
}, $true)
if ($null -eq $promotion) { throw 'Promotion branch not found' }
$promotionText = $promotion.Clauses[0].Item2.Extent.Text
$promotionBody = [scriptblock]::Create('[CmdletBinding(SupportsShouldProcess=$true)]param()' +
    [Environment]::NewLine + $promotionText.Substring(1, $promotionText.Length - 2))
& {
    param($promotionBody)
    $root = 'C:\FixtureOnly\MTTFTest'
    function Assert-InstalledMainStopped { throw 'WhatIf entered promotion operations' }
    & $promotionBody -WhatIf -Confirm:$false
    Write-Output 'PASS WhatIf promotion performs no branch operations'
} $promotionBody
