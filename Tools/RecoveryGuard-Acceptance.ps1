[CmdletBinding()]
param([string]$InstallRoot = '', [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$hostPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $hostPath -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Install-AutomaticRecoveryBundle.ps1') -Mode ValidatePackage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$manifestPath = Join-Path $PSScriptRoot 'automatic-bundle.json'
if ((Get-Item -LiteralPath $manifestPath).Length -gt 4MB) { throw '包清单超出读取上限。' }
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$report = [ordered]@{
    computer = $env:COMPUTERNAME
    utc = [DateTime]::UtcNow.ToString('O')
    powershell = $PSVersionTable.PSVersion.ToString()
    frameworkRelease = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop).Release
    packageIntegrity = 'PASS'
    recoveryArchitecture = [string]$manifest.recoveryArchitecture
    separateRecoveryGuard = $(if ($manifest.schemaVersion -eq 2) {'Independent-SystemExecutor'} else {'FallbackGuard-v1-ObservationDefault'})
    fallbackActiveHardwareAcceptance = 'NOT_VERIFIED'
    hardwareActions = 'NOT_VERIFIED'
    countersAndPersistence = 'NOT_VERIFIED'
    fieldEndurance = 'NOT_VERIFIED'
}
if ($report.frameworkRelease -lt 528040) { throw '.NET Framework 4.8 不满足。' }
if ($InstallRoot) {
    & $hostPath -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Install-AutomaticRecoveryBundle.ps1') -Mode Status -InstallRoot $InstallRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$json = $report | ConvertTo-Json -Depth 5
if ($OutputPath) { $json | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
$json
Write-Host '包与宿主兼容性检查完成；真实试验恢复未验收。'
exit 0
