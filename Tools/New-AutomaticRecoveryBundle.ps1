[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [switch]$LegacyRecovery,
    [string]$CandidateTag = ''
)
$ErrorActionPreference = 'Stop'
$release = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\')
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -ReleaseDirectory $release | Out-Null
$identity = Get-Content -LiteralPath (Join-Path $release 'build-identity.json') -Raw | ConvertFrom-Json
$version = (Get-Item -LiteralPath (Join-Path $release 'MTTFTest.exe')).VersionInfo.FileVersion
$commit = [string]$identity.gitCommit
if ($commit -notmatch '^[a-fA-F0-9]{40}$') { throw '构建提交身份无效。' }
if ([string]::IsNullOrWhiteSpace($CandidateTag)) {
    $CandidateTag = 'v' + $version + $(if ($LegacyRecovery) { '' } else { '-rc.1' })
}
if ($CandidateTag -notmatch ('^v' + [regex]::Escape($version) + '(-rc\.[1-9][0-9]*)?$')) {
    throw '候选标签必须与程序版本一致。'
}
$tagCommit = [string](& git -C (Join-Path $PSScriptRoot '..') rev-parse --verify --quiet ('refs/tags/' + $CandidateTag + '^{commit}'))
$tagExists = $LASTEXITCODE -eq 0
if ($tagExists -and $tagCommit.Trim() -ne $commit) { throw '已有候选标签指向其他源码提交，拒绝误标安装包。' }
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
$toolNames = @('Install-IndependentRecoveryBundle.ps1','Manage-IndependentRecovery.ps1','Manage-SessionHost.ps1','Service-Lifecycle.ps1','Export-IndependentRecoveryEvidence.ps1','Independent-InstallSetup.ps1','Complete-IndependentSetup.ps1')
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
if (-not $LegacyRecovery) {
    $commands['检查升级条件.cmd']='ValidateUpgrade'
    $commands['升级程序.cmd']='Upgrade'
    $commands['继续升级收尾.cmd']='FinalizeUpgrade'
    $commands['恢复中断文件事务.cmd']='RecoverFiles'
    $commands['回退版本.cmd']='RollbackUpgrade'
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
2. 首次安装直接双击“一键安装正式版.cmd”，无需输入参数；Windows 仍可能请求管理员授权。安装器在提权前识别当前桌面账户，优先沿用该账户上次选择的完整项目；没有项目时先安装程序并创建“项目设置”桌面入口。

```powershell
.\一键安装正式版.cmd
```

3. 新机器首次打开桌面入口时，创建新项目或打开已有项目，然后点击“完成设置并打开程序”，自动绑定恢复组件。该页面不连接硬件，不启动试验；关闭或取消后可以再次打开。新项目卡钳均未启用、计数为零；不会重建已有项目缺失的历史数据库。等待项目绑定不等于恢复已经就绪。必须使用安装时的试验账户，不能用另一个管理员账户替代。
   同版本重复安装只校验并保留现有安装；其他版本仍走下方升级流程，不覆盖正在运行的安装。安装参数仍保留给维护脚本使用。
4. 安装成功表示文件校验、服务和任务注册完成，不代表试验已运行。启动入口只打开主程序，试验仍经过原安全预检。
5. 检查入口显示组件与恢复事务状态。修复入口要求原安装文件记录与同构建安装包，恢复程序组件、缺失服务/任务及快捷方式，保留项目配置和数据库；成功后仍保持维护停止状态。修复后用“恢复后台服务”显式启用监督，组件运行不代表试验已续测。
6. 卸载注销本安装服务、任务和快捷方式，并按原安装记录及摘要删除程序组件。配置、项目数据、诊断、注册和重复卸载所需的受保护维护协议保留；不会递归清空目录。重复卸载须使用同构建包，组件内容变化时保留并报告错误。此入口不等于旧版回滚。
7. Evidence 导出有界独立状态、当前事务安全回执及只读正式记录摘要，缺失项写入清单；不包含完整数据库快照或全部波形，也不证明动作恢复。
8. 停止入口先持久撤销续测许可，再由独立服务完成安全收尾；“已受理”不等于进程全部退出。检查 SafetyCleanupPending、事务阶段与错误详情。监督服务和启动任务保留；缺少可信批次身份时拒绝按名称强杀。
9. 升级必须保留原构建完整包，在新包目录执行以下命令；先检查，再升级，成功后仍保持维护停止，不自动续测：

```powershell
.\Install-AutomaticRecoveryBundle.ps1 -Mode ValidateUpgrade -InstallRoot 'C:\Program Files (x86)\MTTFTest' -PreviousBundleDirectory 'D:\旧版完整包'
.\Install-AutomaticRecoveryBundle.ps1 -Mode Upgrade -InstallRoot 'C:\Program Files (x86)\MTTFTest' -PreviousBundleDirectory 'D:\旧版完整包'
```

10. 若结果为 FinalizationFailed，使用新包的 FinalizeUpgrade，TransactionId 为安装目录 Upgrade 下的32位目录名。若文件事务中断且阶段为 Prepared/RollbackFailed，RecoverFiles 的 TransactionId 为 Repair 下的32位目录名。两种事务ID含义不同，不得猜测；先查看对应 result.json 或 transaction.json。文件恢复不等于版本降级，不启动试验。
11. 版本回退从本次新版包运行 RollbackUpgrade，必须同时提供原构建完整包与 Upgrade 目录事务ID。入口核验原包和备份，恢复程序及注册后重新核验原组件摘要、服务/任务和旧版快捷方式。保持维护停止，不自动启动试验。示例：

```powershell
.\Install-AutomaticRecoveryBundle.ps1 -Mode RollbackUpgrade -InstallRoot 'C:\Program Files (x86)\MTTFTest' -PreviousBundleDirectory 'D:\旧版完整包' -TransactionId '升级记录中的32位目录ID'
```

12. 上述维护模式提供对应 cmd，参数通过命令行传入；缺少旧包或事务ID会拒绝执行。不得通过普通 RecoverFiles 撤销已成功提交的升级；回退后需要继续处理的错误保留在升级结果中。降级回滚的完整现场验收及真实硬件验收仍未完成，本包不得作为最终现场交付。未完成入口不转用旧恢复架构。

真实台架和耐久验收未完成。RecoveryGuard-Acceptance 仅检查包/宿主，NOT_VERIFIED 不能视为通过。本轮不自动部署 WJ-EPB。
'@
}
[IO.File]::WriteAllText((Join-Path $output '自动恢复安装说明.md'), $instructions, $utf8)
if (-not $LegacyRecovery) {
    foreach ($document in @(
        '2026-09-15_V4.1候选包安装升级回退与验收说明.md',
        '2026-09-15_V4恢复初始化边界修复与停止意图升级验证.md',
        '2026-09-15_V4单通道人工暂停与独立恢复目标同步修复.md',
        '2026-09-15_V4主程序AO安全归零与压力标定分离修复.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('..\docs\' + $document)) -Destination $output
    }
}
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($output.Length).TrimStart('\').Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest = [ordered]@{
    schemaVersion=$(if ($LegacyRecovery) {1} else {2}); version=$version; gitCommit=$commit; releaseStatus='CANDIDATE_NOT_FIELD_VALIDATED'
    packagingGitCommit=([string](& git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD)).Trim()
    fieldDeploymentApproved=$false; recoveryArchitecture=$(if ($LegacyRecovery) {'V2-Supervisor-SessionAgent-SafetyAgent'} else {'V4-Independent-SystemExecutor'})
    fallbackGuard=$(if ($LegacyRecovery) {'Independent-Database-v2'} else {'Independent-SystemExecutor'})
    fallbackDefault=$(if ($LegacyRecovery) {'ObservationOnly'} else {'RequiresDurableRunIntent'}); candidateTag=$CandidateTag
    candidateTagExistsAtPackaging=$tagExists
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
