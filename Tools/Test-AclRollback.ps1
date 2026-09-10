param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Get-DeploymentAclSnapshot', 'Restore-DeploymentAclSnapshot')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "Missing function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$oldProgramData = $env:ProgramData
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('epb-acl-rollback-' + [Guid]::NewGuid().ToString('N'))
$env:ProgramData = Join-Path $fixture 'ProgramData'
$root = Join-Path $fixture 'Install'
[void](New-Item -ItemType Directory -Path $root, (Join-Path $env:ProgramData 'MTTFTest') -Force)
try {
    $snapshot = Get-DeploymentAclSnapshot $root
    foreach ($entry in @($snapshot.Entries)) {
        # Disable inheritance while copying inherited ACEs, so the fixture stays accessible.
        & icacls.exe ([string]$entry.Path) '/inheritance:d' *> $null
        if ($LASTEXITCODE -ne 0) { throw "ACL fixture mutation failed: $($entry.Name)" }
    }
    $changed = Get-DeploymentAclSnapshot $root
    if (($changed | ConvertTo-Json -Depth 5 -Compress) -ceq ($snapshot | ConvertTo-Json -Depth 5 -Compress)) {
        throw 'ACL fixture did not change'
    }
    Restore-DeploymentAclSnapshot $root $snapshot
    $restored = Get-DeploymentAclSnapshot $root
    foreach ($entry in @($snapshot.Entries)) {
        $actual = @($restored.Entries | Where-Object { $_.Name -ceq $entry.Name })[0]
        $expectedDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new([string]$entry.Sddl)
        $actualDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new([string]$actual.Sddl)
        if ($actualDescriptor.GetSddlForm(14) -cne $expectedDescriptor.GetSddlForm(14)) {
            throw "ACL fixture restore mismatch: $($entry.Name)"
        }
    }
    Write-Output 'PASS ACL rollback 1/1'
}
finally {
    $env:ProgramData = $oldProgramData
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
