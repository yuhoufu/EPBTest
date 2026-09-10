param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Archive-MaintenanceInhibitForInstall'
}, $true)
if ($null -eq $definition) { throw 'Archive function missing' }
$move = '[IO.File]::Move($maintenance, (Join-Path $archiveRoot $archiveName))'
if ([regex]::Matches($definition.Extent.Text, [regex]::Escape($move)).Count -ne 1) {
    throw 'Archive injection point changed'
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('epb-archive-' + [Guid]::NewGuid().ToString('N'))
foreach ($scenario in @('none', 'file', 'directory')) {
    & {
        param($definition, $move, $fixture, $scenario)
        $state = Join-Path $fixture $scenario
        [void][IO.Directory]::CreateDirectory($state)
        $marker = Join-Path $state 'maintenance-inhibit.json'
        [IO.File]::WriteAllText($marker, '{"Mode":"Repair"}')
        $before = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
        $injection = @'
$collisionTarget = Join-Path $archiveRoot $archiveName
if ($scenario -eq 'file') { [IO.File]::WriteAllText($collisionTarget, 'external') }
if ($scenario -eq 'directory') { [void][IO.Directory]::CreateDirectory($collisionTarget) }
'@
        . ([scriptblock]::Create($definition.Replace($move, $injection + [Environment]::NewLine + $move)))
        $caught = $null
        try { Archive-MaintenanceInhibitForInstall $state } catch { $caught = $_ }
        if (($null -ne $caught) -ne ($scenario -ne 'none')) { throw "Wrong archive result: $scenario" }
        $entries = @(Get-ChildItem -LiteralPath (Join-Path $state 'MaintenanceArchive') -Force)
        if ($entries.Count -ne 1) { throw 'Unexpected archive entries' }
        if ($scenario -eq 'none') {
            if ([IO.File]::Exists($marker) -or (Get-FileHash $entries[0].FullName).Hash -ne $before) {
                throw 'Archive did not preserve original bytes'
            }
        } else {
            if ((Get-FileHash -LiteralPath $marker).Hash -ne $before) { throw 'Conflict changed original marker' }
            if ($scenario -eq 'file' -and [IO.File]::ReadAllText($entries[0].FullName) -ne 'external') {
                throw 'Conflict overwrote external file'
            }
            if ($scenario -eq 'directory' -and @([IO.Directory]::GetFileSystemEntries($entries[0].FullName)).Count -ne 0) {
                throw 'Conflict nested original marker into external directory'
            }
        }
        Write-Output "PASS maintenance archive: $scenario"
    } $definition.Extent.Text $move $fixture $scenario
}
Write-Output "Preserved evidence: $fixture"
