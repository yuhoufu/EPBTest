param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
$statusPath = Join-Path $EvidenceDirectory 'soak-status.json'
# Atomic publication replaces the directory entry; readers must allow delete sharing.
$statusStream = [System.IO.File]::Open($statusPath, [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read,
    ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
try {
    $statusReader = [System.IO.StreamReader]::new($statusStream)
    try { $statusReader.ReadToEnd() | ConvertFrom-Json }
    finally { $statusReader.Dispose() }
}
finally { $statusStream.Dispose() }
