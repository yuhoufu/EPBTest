[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$release = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\')
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -ReleaseDirectory $release | Out-Null
$identity = Get-Content -LiteralPath (Join-Path $release 'build-identity.json') -Raw | ConvertFrom-Json
$version = (Get-Item -LiteralPath (Join-Path $release 'MTTFTest.exe')).VersionInfo.FileVersion
$commit = [string]$identity.gitCommit
if ($commit -notmatch '^[a-fA-F0-9]{40}$') { throw '构建提交身份无效。' }
$output = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('V' + $version + '_' + $commit.Substring(0,12) + '_AUTO_RECOVERY_ONECLICK')
if (Test-Path -LiteralPath $output) { throw "拒绝覆盖已存在的候选包：$output" }
[void](New-Item -ItemType Directory -Path $output)
$base = Join-Path $output 'Base'
[void](New-Item -ItemType Directory -Path $base)
Get-ChildItem -LiteralPath $release -Force | Copy-Item -Destination $base -Recurse
$utf8 = New-Object Text.UTF8Encoding($true)
foreach ($script in @('Install-AutomaticRecoveryBundle.ps1','RecoveryGuard-Acceptance.ps1')) {
    [IO.File]::WriteAllText((Join-Path $output $script), [IO.File]::ReadAllText((Join-Path $PSScriptRoot $script)), $utf8)
}
$commands = [ordered]@{
    '恢复后台服务.cmd'='Restore'; '检查运行状态.cmd'='Status'; '启动试验.cmd'='Launch'
    '一键安装正式版.cmd'='Install'; '一键故障采证.cmd'='Evidence'
    '一键停止全部相关进程.cmd'='Stop'; '一键卸载.cmd'='Uninstall'; '一键修复.cmd'='Repair'
}
foreach ($entry in $commands.GetEnumerator()) {
    $command = "@echo off`r`nsetlocal`r`nset `"EPB_BUNDLE_SCRIPT=%~dp0Install-AutomaticRecoveryBundle.ps1`"`r`n"
    $command += '"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%EPB_BUNDLE_SCRIPT%" -Mode ' + $entry.Value + ' %*' + "`r`n"
    $command += "set EPB_EXIT=%ERRORLEVEL%`r`necho ExitCode=%EPB_EXIT%`r`nif not defined EPB_BUNDLE_NONINTERACTIVE pause`r`nexit /b %EPB_EXIT%`r`n"
    [IO.File]::WriteAllText((Join-Path $output $entry.Key), $command, [Text.Encoding]::ASCII)
}
$instructions = @'
# 自动恢复候选安装说明

本包基于 2.14.2.11 修复，采用 Supervisor、SessionAgent、SafetyAgent；没有独立 V3 RecoveryGuard。
这是候选版本，真实台架恢复及耐久验收未完成。“一键安装正式版”仅保留既有入口名称。

1. 完整解压到本地目录；不要从压缩包内部直接执行。
2. 先安全停止现有试验并正常退出。安装、修复和卸载禁止强杀主程序及安全代理。
3. 执行安装或修复入口；需要时 Windows 会请求管理员权限。保留 ProgramData 配置和全部项目数据、计数。
4. “启动试验”打开程序，动作启动仍经过程序安全准入；重复调用不会故意建立第二个进程。
5. 检查运行状态中的进程与服务仅是组件信息；不能作为试验恢复成功证据。
6. 停止入口请求正常关闭，不绕过安全联锁；安全证明不足时返回失败并保留保护组件。
7. 卸载后可以重复卸载；项目数据、运行配置及事故证据保留。回退旧版须使用单独核验的旧版安装包。
8. 故障采证包含轻量日志、状态及 index.db 在线一致性快照，不产生运行中 dump；全部波形的完整增量备份须另行核验。

支持 Windows PowerShell 5.1、.NET Framework 4.8。所有入口从自身目录解析；中文和空格路径必须保持完整。
RecoveryGuard-Acceptance.ps1 是只读包/环境检查；NOT_VERIFIED 不是通过。
验收要求每个应恢复通道真实动作、计数推进、正式圈持续落盘，至少连续三圈，并补充长期观察。
严禁据包完整性检查结果直接宣布现场稳定，不自动部署到 wj-epb。
'@
[IO.File]::WriteAllText((Join-Path $output '自动恢复安装说明.md'), $instructions, $utf8)
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($output.Length).TrimStart('\').Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest = [ordered]@{
    schemaVersion=1; version=$version; gitCommit=$commit; releaseStatus='CANDIDATE_NOT_FIELD_VALIDATED'
    fieldDeploymentApproved=$false; recoveryArchitecture='V2-Supervisor-SessionAgent-SafetyAgent'
    createdUtc=[DateTime]::UtcNow.ToString('O'); files=$files
}
[IO.File]::WriteAllText((Join-Path $output 'automatic-bundle.json'), ($manifest | ConvertTo-Json -Depth 6), $utf8)
Write-Output $output
