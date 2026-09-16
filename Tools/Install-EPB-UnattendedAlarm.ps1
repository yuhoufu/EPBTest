param(
    [Parameter(Mandatory = $true)] [string]$InstallDirectory,
    [string]$JournalDirectory = '',
    [string]$WebhookEndpoint = ''
)
# Public entry points accept PowerShell 7; .NET Framework deployment work is
# executed by the Windows PowerShell host with typed, data-only arguments.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $epbBridgeParameters = @{}
    foreach ($epbBridgeKey in $PSBoundParameters.Keys) {
        $epbBridgeValue = $PSBoundParameters[$epbBridgeKey]
        if ($epbBridgeValue -is [Management.Automation.SwitchParameter]) { $epbBridgeValue = [bool]$epbBridgeValue }
        $epbBridgeParameters[$epbBridgeKey] = $epbBridgeValue
    }
    $epbBridgeData = @{ Script = $PSCommandPath; Parameters = $epbBridgeParameters } | ConvertTo-Json -Depth 5 -Compress
    $epbBridgePayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($epbBridgeData))
    $epbBridgeCode = '$ErrorActionPreference="Stop";$ProgressPreference="SilentlyContinue";$d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("' + $epbBridgePayload + '"))|ConvertFrom-Json;$p=@{};foreach($v in $d.Parameters.PSObject.Properties){$p[$v.Name]=$v.Value};$global:LASTEXITCODE=0;& ([string]$d.Script) @p;exit $LASTEXITCODE'
    $epbBridgeEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($epbBridgeCode))
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -EncodedCommand $epbBridgeEncoded
    exit $LASTEXITCODE
}

$resolvedInstall = [IO.Path]::GetFullPath($InstallDirectory)
if (-not (Test-Path -LiteralPath $resolvedInstall -PathType Container)) {
    throw "安装目录不存在：$resolvedInstall"
}

if (-not [Diagnostics.EventLog]::SourceExists('MTTFTest.Watchdog')) {
    [Diagnostics.EventLog]::CreateEventSource('MTTFTest.Watchdog', 'Application')
}

$alarmConfig = Join-Path $resolvedInstall 'Config\UnattendedAlarmConfig.xml'
if (-not (Test-Path -LiteralPath $alarmConfig -PathType Leaf)) {
    throw "缺少无人值守告警配置：$alarmConfig"
}

function Test-DirectoryWriteAccess {
    param([Parameter(Mandatory = $true)] [string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    [void](New-Item -ItemType Directory -Path $resolved -Force)
    $probe = Join-Path $resolved ('.epb-permission-probe-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($probe, 'permission-probe', (New-Object Text.UTF8Encoding($false)))
        [IO.File]::Delete($probe)
    }
    catch {
        throw "目录不可写：$resolved。$($_.Exception.GetBaseException().Message)"
    }
}

Test-DirectoryWriteAccess -Path (Split-Path -Parent $alarmConfig)
if (-not [string]::IsNullOrWhiteSpace($JournalDirectory)) {
    Test-DirectoryWriteAccess -Path $JournalDirectory
}

[xml]$xml = Get-Content -LiteralPath $alarmConfig -Raw -Encoding UTF8
if (-not [string]::IsNullOrWhiteSpace($WebhookEndpoint)) {
    $uri = [Uri]$WebhookEndpoint
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') {
        throw 'WebhookEndpoint 必须是绝对 HTTPS 地址。'
    }
    $xml.UnattendedAlarm.Enabled = 'true'
    $xml.UnattendedAlarm.Endpoint = $uri.AbsoluteUri
    $xml.Save($alarmConfig)
    try {
        $preflight = Invoke-WebRequest -Uri $uri.AbsoluteUri -Method Head `
            -TimeoutSec 10 -UseBasicParsing
        Write-Host "Webhook 连通性预检成功：HTTP $([int]$preflight.StatusCode)"
    }
    catch {
        # DegradeAndContinue：部署不因通知通道不可用而阻断安全运行，但必须显式告警。
        Write-Warning "AlarmDeliveryDegraded：Webhook 连通性预检失败，将由 Sidecar 持续重连。$($_.Exception.GetBaseException().Message)"
    }
}
$secretVariable = [string]$xml.UnattendedAlarm.SecretEnvironmentVariable
if ([string]::IsNullOrWhiteSpace($secretVariable)) {
    $secretVariable = 'EPB_UNATTENDED_WEBHOOK_SECRET'
}
$secretPresent = -not [string]::IsNullOrWhiteSpace(
    [Environment]::GetEnvironmentVariable(
        $secretVariable,
        [EnvironmentVariableTarget]::Machine)) -or
    -not [string]::IsNullOrWhiteSpace(
        [Environment]::GetEnvironmentVariable($secretVariable))
if (-not $secretPresent) {
    Write-Warning "AlarmDeliveryDegraded：未检测到环境变量 $secretVariable。脚本不会通过参数或配置文件接收真实密钥。"
}

Write-Host "无人值守告警部署检查完成：$resolvedInstall"
