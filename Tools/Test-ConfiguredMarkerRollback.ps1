param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Read-Utf8JsonFile', 'Write-ConfiguredMarker', 'Restore-ConfiguredMarkerTransaction')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "Missing function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$configuredMarkerName = 'configured.test'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('epb-marker-rollback-' + [Guid]::NewGuid().ToString('N'))
$current = Join-Path $fixture 'Current'
[void](New-Item -ItemType Directory -Path $current)
$marker = Join-Path $current $configuredMarkerName
try {
    [IO.File]::WriteAllText($marker, 'ORIGINAL', [Text.Encoding]::ASCII)
    $reference = Write-ConfiguredMarker $fixture
    Restore-ConfiguredMarkerTransaction $fixture $reference
    if ([IO.File]::ReadAllText($marker, [Text.Encoding]::ASCII) -cne 'ORIGINAL') {
        throw 'Original marker bytes were not restored'
    }
    Restore-ConfiguredMarkerTransaction $fixture $reference
    Write-Output 'PASS configured marker rollback: original bytes restored idempotently'

    Remove-Item -LiteralPath $marker -Force
    $absentReference = Write-ConfiguredMarker $fixture
    Restore-ConfiguredMarkerTransaction $fixture $absentReference
    if (Test-Path -LiteralPath $marker) { throw 'New marker was not retracted' }
    Write-Output 'PASS configured marker rollback: prior absence restored with publication retained'

    $conflictReference = Write-ConfiguredMarker $fixture
    [IO.File]::WriteAllText($marker, 'FOREIGN-CHANGE', [Text.Encoding]::ASCII)
    $conflict = $null
    try { Restore-ConfiguredMarkerTransaction $fixture $conflictReference } catch { $conflict = $_ }
    if ($null -eq $conflict -or $conflict.Exception.Message -cne 'ConfiguredMarkerRollbackConflict' -or
        [IO.File]::ReadAllText($marker, [Text.Encoding]::ASCII) -cne 'FOREIGN-CHANGE') {
        throw 'Marker rollback conflict was not preserved'
    }
    Write-Output 'PASS configured marker rollback: changed publication rejected without mutation'
    Write-Output 'PASS configured marker rollback 3/3'
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
