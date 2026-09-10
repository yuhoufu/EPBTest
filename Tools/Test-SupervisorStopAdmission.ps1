param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-Supervisor'
}, $true)
if ($null -eq $definition) { throw 'Stop function missing' }
foreach ($scenario in @('missing', 'query-error', 'stopped', 'running', 'stop-error', 'wait-error')) {
    & {
        param($definition, $scenario)
        . ([scriptblock]::Create($definition))
        $serviceName = 'FixtureSupervisor'
        $script:stopped = $false
        $script:waited = $false
        function Get-Service {
            [CmdletBinding()]param($Name)
            if ($scenario -eq 'missing') {
                $PSCmdlet.ThrowTerminatingError([Management.Automation.ErrorRecord]::new(
                    [InvalidOperationException]::new('FixtureMissing'), 'NoServiceFoundForGivenName',
                    [Management.Automation.ErrorCategory]::ObjectNotFound, $Name))
            }
            if ($scenario -eq 'query-error') { Write-Error 'FixtureQueryError'; return }
            $service = [pscustomobject]@{ Status = $(if ($scenario -eq 'stopped') { 'Stopped' } else { 'Running' }) }
            $service | Add-Member ScriptMethod WaitForStatus {
                param($status, $timeout)
                $script:waited = $true
                if ($scenario -eq 'wait-error') { throw 'FixtureWaitError' }
            }
            return $service
        }
        function Stop-Service {
            [CmdletBinding(SupportsShouldProcess=$true)]param($Name, [switch]$Force)
            $script:stopped = $true
            if ($scenario -eq 'stop-error') { Write-Error 'FixtureStopError' }
        }
        $caught = $null
        try { Stop-Supervisor } catch { $caught = $_ }
        if (($null -ne $caught) -ne ($scenario -in @('query-error', 'stop-error', 'wait-error'))) {
            throw "Unexpected stop outcome: $scenario"
        }
        if ($script:stopped -ne ($scenario -in @('running', 'stop-error', 'wait-error')) -or
            $script:waited -ne ($scenario -in @('running', 'wait-error'))) {
            throw "Unexpected stop sequence: $scenario"
        }
        Write-Output "PASS supervisor stop: $scenario"
    } $definition.Extent.Text $scenario
}
