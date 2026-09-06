# Install-MTTFTest-Unattended.ps1 换代迁移函数沙盒回归：
# 从源脚本提取真实函数文本执行，验证空未授权检查点归档放行、
# 带负载旧schema仍拒绝、以及迁移记录幂等重跑。
$ErrorActionPreference = 'Stop'
$scriptPath = 'D:\Github\wanxiang\EPBTest\Tools\Install-MTTFTest-Unattended.ps1'

$tokens = $null; $errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -gt 0) {
    throw ('PARSE ERRORS: ' + (($errors | ForEach-Object { $_.Message }) -join '; '))
}
Write-Host 'PASS 语法解析无错误'

$src = Get-Content -LiteralPath $scriptPath -Raw
$m = [regex]::Match($src, '(?s)function Invoke-LegacyCheckpointSafeRollover\b.*?\r?\n\}')
if (-not $m.Success) { throw '未找到 Invoke-LegacyCheckpointSafeRollover 函数' }
Invoke-Expression $m.Value

$sandbox = Join-Path $env:TEMP ('EPB-Rollover-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path (Join-Path $sandbox 'Local\MTTFTest') -Force)
[void](New-Item -ItemType Directory -Path (Join-Path $sandbox 'ProgramData\MTTFTest') -Force)
$env:LOCALAPPDATA = Join-Path $sandbox 'Local'
$env:ProgramData = Join-Path $sandbox 'ProgramData'
$PhysicalIsolationConfirmed = $false

function Read-Utf8JsonFile([string]$Path, [string]$Label) {
    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Resolve-SafeDirectory([string]$Path, [string]$Label) {
    [IO.Path]::GetFullPath($Path)
}

$checkpointPath = Join-Path $env:LOCALAPPDATA 'MTTFTest\unattended-run-checkpoint.json'
$migrationPath = Join-Path $env:ProgramData 'MTTFTest\Migration\schema5-remaining-cycles-migration.json'

# 用例1：空未授权 schema-1 空壳（现场实际形态）→ 归档放行
Set-Content -LiteralPath $checkpointPath -Encoding UTF8 -Value (
    '{"SchemaVersion":1,"Revision":2,"Armed":false,"GracefulPaused":false,' +
    '"RunId":null,"StoreDir":null,"TestName":null,"RemainingFormalCycles":{},' +
    '"LastReason":"MonitorClosing"}')
Invoke-LegacyCheckpointSafeRollover 'C:\FakeInstallRoot'
if (Test-Path -LiteralPath $checkpointPath) { throw '用例1失败：空壳未被归档移走' }
$migration = Get-Content -LiteralPath $migrationPath -Raw | ConvertFrom-Json
if ($migration.legacyDisposition -ne 'EmptyDisarmedDiscarded') {
    throw ('用例1失败：迁移记录缺少EmptyDisarmedDiscarded，实际=' + $migration.legacyDisposition)
}
$archived = Get-ChildItem -LiteralPath $migration.archiveRoot -Filter 'unattended-run-checkpoint.json' -ErrorAction SilentlyContinue
if ($null -eq $archived) { throw '用例1失败：归档目录缺少封存文件' }
Write-Host 'PASS 用例1 空未授权schema-1空壳归档放行'

# 用例2：带负载的 schema-1（有授权、有圈数）→ 仍拒绝
Remove-Item -LiteralPath $migrationPath -Force
Set-Content -LiteralPath $checkpointPath -Encoding UTF8 -Value (
    '{"SchemaVersion":1,"Revision":5,"Armed":true,"RunId":"abc123",' +
    '"StoreDir":"D:\\Store","TestName":"P1","RemainingFormalCycles":{"4":10}}')
$rejected = $false
try { Invoke-LegacyCheckpointSafeRollover 'C:\FakeInstallRoot' } catch { $rejected = $true }
if (-not $rejected) { throw '用例2失败：带负载schema-1未被拒绝' }
Write-Host 'PASS 用例2 带负载schema-1仍拒绝安装'

# 用例3：无检查点且迁移记录已存在 → 幂等直接返回
Remove-Item -LiteralPath $checkpointPath -Force
Set-Content -LiteralPath $migrationPath -Encoding UTF8 -Value '{}'
Invoke-LegacyCheckpointSafeRollover 'C:\FakeInstallRoot'
Write-Host 'PASS 用例3 已换代后幂等重跑直接返回'

# 用例4：schema-6 正常授权会话仍走原有封存迁移
Remove-Item -LiteralPath $migrationPath -Force
Set-Content -LiteralPath $checkpointPath -Encoding UTF8 -Value (
    '{"SchemaVersion":6,"Revision":9,"Armed":true,"RunId":"abc456",' +
    '"RootRunId":"root1","StoreDir":"D:\\Store","TestName":"P1",' +
    '"MotorOffConfirmed":true,"PressureSafeConfirmed":true,"PersistenceDrained":true,' +
    '"RemainingFormalCycles":{"4":7}}')
Invoke-LegacyCheckpointSafeRollover 'C:\FakeInstallRoot'
$migration = Get-Content -LiteralPath $migrationPath -Raw | ConvertFrom-Json
if ($migration.sourceSchemaVersion -ne 6 -or $migration.remainingFormalCycles.'4' -ne 7) {
    throw '用例4失败：schema-6封存迁移记录不正确'
}
Write-Host 'PASS 用例4 schema-6正常封存迁移保持不变'

Remove-Item -LiteralPath $sandbox -Recurse -Force
Write-Host 'PASS 换代迁移沙盒回归 4/4'
