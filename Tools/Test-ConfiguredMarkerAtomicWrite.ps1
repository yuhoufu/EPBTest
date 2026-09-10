# Extract only the marker writer: never execute the installer entry point.
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw $parseErrors[0] }
$function = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Write-ConfiguredMarker'
}, $true)
if ($null -eq $function) { throw 'Marker writer missing.' }
. ([scriptblock]::Create($function.Extent.Text))
$configuredMarkerName = 'configured.test'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('epb-marker-' + [guid]::NewGuid().ToString('N'))
$current = Join-Path $testRoot 'Current'
[void](New-Item -ItemType Directory -Path $current)
$marker = Join-Path $current $configuredMarkerName
Write-ConfiguredMarker $testRoot
$originalBytes = [IO.File]::ReadAllBytes($marker)
if ([Text.Encoding]::UTF8.GetString($originalBytes) -notmatch '^ConfiguredUtc=') { throw 'Invalid marker.' }
$originalHash = (Get-FileHash -LiteralPath $marker).Hash
Write-ConfiguredMarker $testRoot
$backups = @(Get-ChildItem -LiteralPath $current -Filter '*.previous-*')
if ($backups.Count -ne 1 -or (Get-FileHash -LiteralPath $backups[0].FullName).Hash -ne $originalHash) {
    throw 'Replacement did not preserve original marker.'
}
$beforeFailure = (Get-FileHash -LiteralPath $marker).Hash
$held = [IO.File]::Open($marker, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
$rejected = $false
try {
    try { Write-ConfiguredMarker $testRoot } catch { $rejected = $true }
} finally { $held.Dispose() }
if (-not $rejected -or (Get-FileHash -LiteralPath $marker).Hash -ne $beforeFailure) {
    throw 'Locked replacement damaged original marker or incorrectly succeeded.'
}
if (@(Get-ChildItem -LiteralPath $current -Filter '*.pending-*').Count -ne 1) {
    throw 'Failed publication evidence missing.'
}
$failedStage = @(Get-ChildItem -LiteralPath $current -Filter '*.pending-*')[0].FullName
$failedHash = (Get-FileHash -LiteralPath $failedStage).Hash
Write-ConfiguredMarker $testRoot
if ([IO.File]::ReadAllText($marker) -notmatch '^ConfiguredUtc=' -or
    (Get-FileHash -LiteralPath $failedStage).Hash -ne $failedHash) {
    throw 'Retry failed or overwrote prior failure evidence.'
}
$retryBackups = @(Get-ChildItem -LiteralPath $current -Filter '*.previous-*')
if ($retryBackups.Count -ne 2 -or
    -not @($retryBackups | Where-Object {
        (Get-FileHash -LiteralPath $_.FullName).Hash -eq $beforeFailure
    }).Count) { throw 'Retry did not preserve pre-retry marker.' }
# Retain all isolated files for audit; no installation/service/task mutations.
"PASS marker create, replace, backup identity, locked preservation, retry and evidence retention; Evidence=$testRoot"
