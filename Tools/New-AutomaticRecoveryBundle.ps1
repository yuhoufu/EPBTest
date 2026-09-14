[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [switch]$LegacyRecovery
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
$fallbackSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\FallbackGuard\bin\Release'))
$fallbackOutput = Join-Path $output 'FallbackGuard'
[IO.Directory]::CreateDirectory($fallbackOutput) | Out-Null
foreach ($name in @('MTTFTest.FallbackGuard.exe','MTTFTest.Watchdog.Protocol.dll')) {
    $source = Join-Path $fallbackSource $name
    if (-not [IO.File]::Exists($source) -or [Diagnostics.FileVersionInfo]::GetVersionInfo($source).FileVersion -ne $version) { throw "Fallback 组件版本不符：$source" }
    Copy-Item -LiteralPath $source -Destination $fallbackOutput
}
if ((Get-FileHash (Join-Path $fallbackOutput 'MTTFTest.Watchdog.Protocol.dll')).Hash -ne (Get-FileHash (Join-Path $base 'MTTFTest.Watchdog.Protocol.dll')).Hash) { throw 'Fallback 与 Base 协议程序集不同源' }
foreach ($relative in @('System.Data.SQLite.dll', 'x86\SQLite.Interop.dll')) {
    $source = Join-Path $fallbackSource $relative
    if (-not [IO.File]::Exists($source)) { throw "Fallback 数据库依赖缺失：$source" }
    $destination = Join-Path $fallbackOutput $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
$toolsOutput = Join-Path $output 'Tools'
[IO.Directory]::CreateDirectory($toolsOutput) | Out-Null
$toolNames = @('Install-IndependentRecoveryBundle.ps1','Manage-IndependentRecovery.ps1')
if ($LegacyRecovery) { $toolNames += @('Manage-FallbackGuard.ps1','Test-FallbackGuard.ps1',
    'Resolve-FallbackBinding.ps1','Persistent-Fallback.ps1','Manage-PersistentFallback.ps1',
    'Watch-ActiveFallback.ps1','Test-FallbackBinding.ps1','Test-PersistentFallback.ps1','Test-PersistentTask.ps1') }
foreach ($name in $toolNames) {
    [IO.File]::WriteAllText((Join-Path $toolsOutput $name), [IO.File]::ReadAllText((Join-Path $PSScriptRoot $name), [Text.Encoding]::UTF8), $utf8)
}
if ($LegacyRecovery) {
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\docs\02_Issues\2026-09-13_V4数据库监督实施与候选验收.md') -Destination (Join-Path $output '独立兜底说明.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\docs\02_Issues\2026-09-13_V4项目数据库独立监督与恢复修复实施方案_待确认.md') -Destination $output
foreach ($entry in @(
    @{Name='启用独立数据库监督.cmd';Mode='Install'},
    @{Name='禁用独立数据库监督.cmd';Mode='Disable'},
    @{Name='查询独立数据库监督.cmd';Mode='Status'},
    @{Name='卸载独立数据库监督.cmd';Mode='Uninstall'})) {
    $line = '@echo off' + "`r`n" + 'setlocal' + "`r`n" +
        '"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Manage-PersistentFallback.ps1" -Mode ' + $entry.Mode + ' %*' + "`r`n" +
        'set EPB_EXIT=%ERRORLEVEL%' + "`r`n" + 'echo ExitCode=%EPB_EXIT%' + "`r`n" +
        'if not defined EPB_BUNDLE_NONINTERACTIVE pause' + "`r`n" + 'exit /b %EPB_EXIT%' + "`r`n"
    [IO.File]::WriteAllText((Join-Path $output $entry.Name), $line, [Text.Encoding]::ASCII)
}
} else {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\docs\2026-09-14_V4.1_独立执行器组合安装入口验证.md') -Destination (Join-Path $output '独立兜底说明.md')
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

本包由 2.14.2.12 稳定恢复基线实施，采用原 Supervisor、SessionAgent、SafetyAgent，并新增可独立启停的 FallbackGuard。独立程序默认只观察，详见独立兜底说明。
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
if (-not $LegacyRecovery) {
    $instructions = @'
# 独立执行器候选安装说明

本包使用独立 SYSTEM 执行服务及专属交互启动任务。主程序明确开始试验后建立持久运行意图；没有运行意图时服务等待，不自动启动卡钳。不是仅观察模式，仍须通过真实动作、计数推进及至少三周期正式落盘验证恢复。

1. 完整解压到本机，先安全停止并退出原控制程序。不得覆盖运行中的安装。
2. 首次安装须明确项目路径和实际交互账户 SID。管理员 PowerShell 中执行：

```powershell
.\Install-AutomaticRecoveryBundle.ps1 -Mode Install -InstallRoot 'C:\Program Files (x86)\MTTFTest' -ProjectDirectory 'D:\实际项目' -InteractiveUserSid '实际账户SID'
```

3. InstallRoot 必须不存在，父目录必须受保护。已有安装的版本升级/回滚不能使用首次安装覆盖。
4. 安装成功表示文件校验、服务和任务注册完成，不代表试验已运行。启动入口只打开主程序，试验仍经过原安全预检。
5. 检查入口显示组件状态；修复入口只修复缺失服务/任务，保留原绑定并进入维护。修复后用“恢复后台服务”显式启用监督。
6. 卸载删除本安装服务和任务，保留受保护程序文件、注册及项目数据。现阶段不是完整二进制卸载或旧版回滚。
7. Stop、Evidence、二进制修复和版本升级尚未完成时不得将本包作为最终现场交付。未完成入口返回错误，不转用旧恢复架构。

真实台架和耐久验收未完成。RecoveryGuard-Acceptance 仅检查包/宿主，NOT_VERIFIED 不能视为通过。本轮不自动部署 WJ-EPB。
'@
}
[IO.File]::WriteAllText((Join-Path $output '自动恢复安装说明.md'), $instructions, $utf8)
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($output.Length).TrimStart('\').Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest = [ordered]@{
    schemaVersion=$(if ($LegacyRecovery) {1} else {2}); version=$version; gitCommit=$commit; releaseStatus='CANDIDATE_NOT_FIELD_VALIDATED'
    packagingGitCommit=([string](& git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD)).Trim()
    fieldDeploymentApproved=$false; recoveryArchitecture=$(if ($LegacyRecovery) {'V2-Supervisor-SessionAgent-SafetyAgent'} else {'V4-Independent-SystemExecutor'})
    fallbackGuard=$(if ($LegacyRecovery) {'Independent-Database-v2'} else {'Independent-SystemExecutor'})
    fallbackDefault=$(if ($LegacyRecovery) {'ObservationOnly'} else {'RequiresDurableRunIntent'}); candidateTag=('v'+[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $fallbackOutput 'MTTFTest.FallbackGuard.exe')).ProductVersion)
    createdUtc=[DateTime]::UtcNow.ToString('O'); files=$files
}
[IO.File]::WriteAllText((Join-Path $output 'automatic-bundle.json'), ($manifest | ConvertTo-Json -Depth 6), $utf8)
$sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
if (-not (Test-Path -LiteralPath $sevenZip)) { throw '缺少 7-Zip，不能生成最大压缩率候选包。' }
$archive = $output + '.7z'
Push-Location (Split-Path -Parent $output)
try {
    & $sevenZip a -t7z -m0=LZMA2 -mx=9 -mmt=2 $archive (Split-Path -Leaf $output) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '7-Zip 最大压缩率打包失败。' }
    & $sevenZip t $archive | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '7-Zip 压缩包完整性验证失败。' }
} finally { Pop-Location }
$digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
[IO.File]::WriteAllText($archive + '.sha256.txt', $digest + '  ' + (Split-Path -Leaf $archive) + "`r`n", [Text.Encoding]::ASCII)
Write-Output $output
