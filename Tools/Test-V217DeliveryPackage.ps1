param([Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$verified = & (Join-Path $workspace 'Tools\Verify-Release.ps1') -ReleaseDirectory $PackageDirectory | ConvertFrom-Json
if (-not $verified.verified) { throw 'Package verification failed' }
$prefix = [IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\') + '\'
if ((Get-FileHash -LiteralPath (Join-Path $BundleDirectory 'bundle-identity.json')).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $BundleDirectory '快捷部署包身份.json')).Hash) { throw 'Identity alias mismatch' }
if ((Get-FileHash -LiteralPath (Join-Path $BundleDirectory 'SHA256SUMS.txt')).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $BundleDirectory '快捷部署包-SHA256.txt')).Hash) { throw 'Manifest alias mismatch' }
$count=0
foreach ($line in Get-Content -LiteralPath (Join-Path $BundleDirectory 'SHA256SUMS.txt') -Encoding UTF8) {
    $pair=$line -split '  ',2
    if ($pair.Count -ne 2) { throw 'Malformed outer checksum' }
    $path=[IO.Path]::GetFullPath((Join-Path $BundleDirectory $pair[1]))
    if (-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $path).Hash -ne $pair[0]) { throw "Outer hash mismatch: $path" }
    $count++
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('EPB-Delivery-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $temp 'MTTFTest'
$current = Join-Path $install 'Current'
try {
    [void](New-Item -ItemType Directory -Path $current -Force)
    [void](New-Item -ItemType Directory -Path $EvidenceDirectory -Force)
    Get-ChildItem -LiteralPath $PackageDirectory -Force | Copy-Item -Destination $current -Recurse -Force
    'Isolated marker verification' | Set-Content -LiteralPath (Join-Path $current 'MTTFTest.FirstRun.configured') -Encoding UTF8
    $installed = & (Join-Path $current 'Deployment\Verify-Release.ps1') -ReleaseDirectory $current -AllowInstalledRuntimeState | ConvertFrom-Json
    if (-not $installed.verified) { throw 'Installed marker verification failed' }
    & (Join-Path $BundleDirectory 'Stop-RelatedProcesses.ps1') -Mode Stop -InstallRoot $install -WhatIf
    $status = & (Join-Path $BundleDirectory 'Stop-RelatedProcesses.ps1') -Mode Status -InstallRoot $install | ConvertFrom-Json
    if (@($status.Processes).Count -ne 0) { throw 'Unexpected process in isolated install' }
    $export = Join-Path $EvidenceDirectory ('evidence-tool-smoke-' + [Guid]::NewGuid().ToString('N'))
    & (Join-Path $BundleDirectory 'Export-StabilityEvidence.ps1') -InstallRoot $install -OutputDirectory $export
    $index = Get-Content -LiteralPath (Join-Path $export 'evidence-index.json') -Raw | ConvertFrom-Json
    $copiedIdentity = @($index | Where-Object { $_.Copied -eq $true -and $_.Source -eq (Join-Path $current 'build-identity.json') })
    if ($copiedIdentity.Count -ne 1) { throw 'Evidence export missed installed identity' }
    $evidenceSummary = Get-Content -LiteralPath (Join-Path $export 'evidence-summary.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($evidenceSummary.Complete -and @($index | Where-Object { $_.Copied -eq $false }).Count -gt 0) { throw 'Evidence summary hides gaps' }
    $result = [ordered]@{ packageVerified=$true; outerFilesVerified=$count;
        installedMarkerVerified=$true; maintenanceWhatIf=$true; maintenanceStatus=$true;
        evidenceExport=$true; evidenceComplete=$evidenceSummary.Complete; evidenceProblems=$evidenceSummary.Problems; scmMutationPerformed=$false; hardwareTestPerformed=$false;
        sourceIdentitySha256=(Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'build-identity.json')).Hash }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'delivery-smoke-result.json') -Encoding UTF8
    $result | ConvertTo-Json
}
finally {
    $resolved=[IO.Path]::GetFullPath($temp)
    $allowed=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if (-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup escaped temporary root' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
