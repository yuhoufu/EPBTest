param(
    [string]$ReleaseDirectory = 'D:\Github\wanxiang\EPBTest\artifacts\releases\V2.16.0.0-343ea80f8331-20260905_073154'
)
$ErrorActionPreference = 'Stop'
# Run in Windows PowerShell/.NET Framework. No service/host constructors or
# application entry points are called. Only the production comparison is invoked.
$protocolPath = Join-Path $ReleaseDirectory 'MTTFTest.Watchdog.Protocol.dll'
$watchdogPath = Join-Path $ReleaseDirectory 'MTTFTest.Watchdog.exe'
$mainPath = Join-Path $ReleaseDirectory 'MTTFTest.exe'
[void][Reflection.Assembly]::LoadFrom($protocolPath)
$assembly = [Reflection.Assembly]::LoadFrom($watchdogPath)
$ownedType = $assembly.GetTypes() | Where-Object { $_.Name -eq 'SupervisorOwnedSession' }
if ($null -eq $ownedType) { throw 'Production SupervisorOwnedSession type not found.' }
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$instance = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($ownedType)
$ownedType.GetField('_gate', $flags).SetValue($instance, (New-Object object))
$ownedType.GetField('_mainExecutablePath', $flags).SetValue($instance, $mainPath)
$upper = [MTTFTest.Watchdog.Protocol.SupervisorProtocol]::ComputeSha256($mainPath)
$lower = $upper.ToLowerInvariant()
$ownedType.GetField('_mainExecutableSha256', $flags).SetValue($instance, $upper)
$method = $ownedType.GetMethod('MatchesMainExecutable', $flags)
$sameCase = [bool]$method.Invoke($instance, [string[]]@($mainPath, $upper))
$lowerCase = [bool]$method.Invoke($instance, [string[]]@($mainPath, $lower))
$changed = $(if ($upper[0] -eq '0') { '1' } else { '0' }) + $upper.Substring(1)
$differentHash = [bool]$method.Invoke($instance, [string[]]@($mainPath, $changed))
$result = [ordered]@{
    ReleaseDirectory = $ReleaseDirectory
    WatchdogSha256 = (Get-FileHash -LiteralPath $watchdogPath -Algorithm SHA256).Hash
    MainSha256 = $upper
    SameUpperCaseMatches = $sameCase
    SameBytesLowerCaseMatches = $lowerCase
    DifferentHashMatches = $differentHash
    Meaning = 'Defect reproduction, not a fix validation; no hardware or services started.'
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'hash-case-repro.json') -Encoding UTF8
$result | ConvertTo-Json
if (-not $sameCase -or $lowerCase -or $differentHash) { throw 'Observed behavior differs from the hypothesized defect.' }
