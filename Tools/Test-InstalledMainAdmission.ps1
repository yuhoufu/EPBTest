param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Assert-InstalledMainStopped'
}, $true)
if ($null -eq $definition) { throw 'Main admission function missing' }
foreach ($scenario in @('query-failure', 'missing-path', 'installed', 'other-root', 'none')) {
    & {
        param($definition, $scenario)
        . ([scriptblock]::Create($definition))
        function Get-CimInstance {
            [CmdletBinding()]param($ClassName, $Filter)
            if ($scenario -eq 'query-failure') { Write-Error 'FixtureQueryFailed'; return }
            if ($scenario -eq 'missing-path') { return [pscustomobject]@{ ExecutablePath = $null } }
            if ($scenario -eq 'installed') {
                return [pscustomobject]@{ ExecutablePath = 'C:\FixtureOnly\MTTFTest\Current\MTTFTest.exe' }
            }
            if ($scenario -eq 'other-root') {
                return [pscustomobject]@{ ExecutablePath = 'C:\FixtureOnly\MTTFTest-other\MTTFTest.exe' }
            }
        }
        $caught = $null
        try { Assert-InstalledMainStopped 'C:\FixtureOnly\MTTFTest' } catch { $caught = $_ }
        $mustReject = $scenario -in @('query-failure', 'missing-path', 'installed')
        if (($null -ne $caught) -ne $mustReject) { throw "Admission mismatch: $scenario" }
        if ($scenario -eq 'query-failure' -and $caught.Exception.Message -notlike '*FixtureQueryFailed*') {
            throw 'Query failure was not propagated'
        }
        Write-Output "PASS installed main admission: $scenario"
    } $definition.Extent.Text $scenario
}
