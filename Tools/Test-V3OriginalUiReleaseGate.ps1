[CmdletBinding()]
param(
    [string]$RepositoryRoot = '',
    [switch]$AllowInstalledUiValidationPending
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
$status = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Build\OriginalUiCompletion.json') -Raw | ConvertFrom-Json
$softwareRequired = @('monitorVerified', 'configurationTransactionsVerified',
    'maintenanceCalibrationVerified', 'alarmAndChannelCommandsVerified',
    'playbackVerified')
$missingSoftware = @($softwareRequired | Where-Object {
    $status.$_ -isnot [bool] -or -not $status.$_
})
if ($status.contractVersion -ne 1 -or $missingSoftware.Count -gt 0) {
    throw "V3 原界面软件功能尚未完整验收，禁止生成现场验证包。未通过：$($missingSoftware -join ', ')"
}
if ($status.installedUiRecoveryVerified -isnot [bool]) {
    throw 'V3 原界面安装态验收字段无效。'
}
if (-not $status.installedUiRecoveryVerified) {
    if (-not $AllowInstalledUiValidationPending) {
        throw 'V3 原界面安装态 LocalSystem、快捷方式和 UI 恢复尚未验收，禁止生成正式包。'
    }
    Write-Output 'PASS V3OriginalUiCandidateReleaseGate InstalledUiRecovery=PENDING_FIELD_VALIDATION'
    return
}
Write-Output 'PASS V3OriginalUiReleaseGate InstalledUiRecovery=PASS'
