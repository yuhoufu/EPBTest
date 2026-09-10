param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Get-InstalledServiceRegistrySnapshot', 'Restore-InstalledServiceRegistrySnapshot')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "Missing function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$serviceName = 'EpbTxnFixture' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$baseKey = [Microsoft.Win32.Registry]::CurrentUser
$keyPath = 'SYSTEM\CurrentControlSet\Services\' + $serviceName
$created = $false
try {
    $key = $baseKey.CreateSubKey($keyPath, $true)
    if ($null -eq $key) { throw 'Fixture registry key create failed' }
    $created = $true
    try {
        $key.SetValue('ImagePath', 'original.exe', [Microsoft.Win32.RegistryValueKind]::ExpandString)
        $key.SetValue('Start', 3, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('FailureActions', [byte[]](1,2,3,4), [Microsoft.Win32.RegistryValueKind]::Binary)
        $key.SetValue('FailureActionsOnNonCrashFailures', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
    } finally { $key.Dispose() }
    $before = Get-InstalledServiceRegistrySnapshot $baseKey $keyPath

    $key = $baseKey.OpenSubKey($keyPath, $true)
    try {
        $key.SetValue('ImagePath', 'changed.exe', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('Start', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('DelayedAutoStart', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('FailureActions', [byte[]](9,8), [Microsoft.Win32.RegistryValueKind]::Binary)
        $key.DeleteValue('FailureActionsOnNonCrashFailures', $false)
    } finally { $key.Dispose() }
    $changed = Get-InstalledServiceRegistrySnapshot $baseKey $keyPath
    if (($changed | ConvertTo-Json -Depth 6 -Compress) -ceq ($before | ConvertTo-Json -Depth 6 -Compress)) {
        throw 'Fixture service did not change'
    }

    Restore-InstalledServiceRegistrySnapshot $before $baseKey
    $restored = Get-InstalledServiceRegistrySnapshot $baseKey $keyPath
    if (($restored | ConvertTo-Json -Depth 6 -Compress) -cne ($before | ConvertTo-Json -Depth 6 -Compress)) {
        throw 'Fixture service registry state was not restored exactly'
    }
    Write-Output 'PASS service registry rollback integration 1/1'
}
finally {
    if ($created) {
        $baseKey.DeleteSubKeyTree($keyPath, $false)
    }
}
