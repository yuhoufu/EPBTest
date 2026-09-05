[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temp = Join-Path ([IO.Path]::GetTempPath()) ('EPB V216 中文 & () ! 测试-' + [Guid]::NewGuid().ToString('N'))
$passed = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++; Write-Output "PASS V216Deployment $Message"
}
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Check $failed $Message
}
function Import-Functions([string]$Path) {
    $tokens=$null; $errors=$null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if (@($errors).Count -gt 0) { throw "$Path : $($errors[0].Message)" }
    foreach ($node in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]}, $false)) {
        . ([scriptblock]::Create(($node.Extent.Text -replace '^function ', 'function script:')))
    }
}
function Refresh-FixtureIdentity([string]$Path) {
    $files = @(Get-ChildItem -LiteralPath $Path -File | Where-Object { $_.Name -ne 'build-identity.json' } | ForEach-Object {
        @{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    $components = @($files | Where-Object { $_.name -match '\.(exe|dll)$' } | ForEach-Object { @{name=$_.name;fileVersion='2.16.0.0'} })
    @{ gitCommit=('a'*40); packageContentSha256=('b'*64); files=$files; componentIdentities=$components;
        fileVersion='2.16.0.0'; recoveryArchitectureGeneration='EPB-V2.16'; watchdogSchema=6;
        releaseStatus='FORMAL_RELEASE'; deploymentApproved=$true } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $Path 'build-identity.json') -Encoding UTF8
}
try {
    [void](New-Item -ItemType Directory -Path $temp)
    Import-Functions (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1')
    Import-Functions (Join-Path $PSScriptRoot 'Stop-RelatedProcesses.ps1')
    $source = Join-Path $temp 'Package'
    $root = Join-Path $temp 'MTTFTest'
    [void](New-Item -ItemType Directory -Path $source)
    $names = @('MTTFTest.exe','Controller.dll','MTTFTest.Watchdog.exe','MTTFTest.SafetyAgent.exe',
        'MTTFTest.SessionAgent.exe','MTTFTest.SafetyHardware.dll','MTTFTest.Watchdog.Protocol.dll','MTTFTest.Watchdog.Client.dll')
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $repo ('MTTfTest\bin\Debug\' + $name)) -Destination (Join-Path $source $name) }
    'fixture-A' | Set-Content -LiteralPath (Join-Path $source 'fixture.txt')
    Refresh-FixtureIdentity $source
    Install-CurrentSlot $source $root
    Check (-not (Test-CurrentSlotReplacementRequired $source $root)) '首次安装完整文件及身份一致'
    Check (@(Get-ChildItem -LiteralPath $root -Directory -Filter '.retired-*').Count -eq 0) '重复安装不产生重复旧槽'
    'fixture-B' | Set-Content -LiteralPath (Join-Path $source 'fixture.txt')
    Refresh-FixtureIdentity $source
    Check (Test-CurrentSlotReplacementRequired $source $root) '同版本不同哈希必须换包'
    Install-CurrentSlot $source $root
    Check (-not (Test-CurrentSlotReplacementRequired $source $root)) '完整换包之后文件一致'
    'corrupt' | Set-Content -LiteralPath (Join-Path $source 'fixture.txt')
    Expect-Failure { Install-CurrentSlot $source $root } '损坏包在修改Current前拒绝'
    Check ((Get-Content -LiteralPath (Join-Path $root 'Current\fixture.txt')) -eq 'fixture-B') '损坏包不影响已有版本'
    Refresh-FixtureIdentity $source
    $locked = [IO.File]::Open((Join-Path $root 'Current\MTTFTest.exe'), 'Open', 'Read', 'None')
    try { Expect-Failure { Install-CurrentSlot $source $root } '文件独占占用时换包明确失败' }
    finally { $locked.Dispose() }
    Check (Test-Path -LiteralPath (Join-Path $root 'Current\MTTFTest.exe')) '文件占用失败保留当前程序'
    Install-CurrentSlot $source $root
    Check (-not (Test-CurrentSlotReplacementRequired $source $root)) '占用解除后重试成功'
    $interrupted = Join-Path $root '.retired-injected-interruption'
    Move-Item -LiteralPath (Join-Path $root 'Current') -Destination $interrupted
    @{ retired=$interrupted } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'install-transaction.json') -Encoding UTF8
    Install-CurrentSlot $source $root
    Check (-not (Test-CurrentSlotReplacementRequired $source $root)) '中断于Current移走后可恢复并重新安装'
    $identityPath = Join-Path $source 'build-identity.json'
    $valid = [IO.File]::ReadAllText($identityPath)
    $identity = $valid | ConvertFrom-Json
    $identity.recoveryArchitectureGeneration = 'EPB-V3'
    $identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $identityPath -Encoding UTF8
    Expect-Failure { Get-VerifiedDeploymentIdentity $source } 'V3组件身份拒绝混装'
    [IO.File]::WriteAllText($identityPath, $valid, (New-Object Text.UTF8Encoding($true)))
    $identity = $valid | ConvertFrom-Json
    $identity.files[0].name = '..\outside.exe'
    $identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $identityPath -Encoding UTF8
    Expect-Failure { Get-VerifiedDeploymentIdentity $source } '清单路径穿越拒绝'
    Expect-Failure { Resolve-SafeDirectory 'D:\' 'UnsafeRoot' } '磁盘根目录拒绝'
    $main = @{ Id=123; StartTicks=100 }
    $receipt = [pscustomobject]@{ SchemaVersion=2; MainProcessId=123; MainProcessStartUtcTicks=100;
        UpdatedUtcTicks=300; RequestedUtcTicks=201; SessionId='session'; State=3; RelaunchDisposition=1;
        MotorsOff=$true; PowerOff=$true; PressureSafe=$true; PersistenceDrained=$true; LogicalQuiescent=$true }
    Check (Test-StopReceipt $receipt $main 200) '清场接受本次准确身份的完整安全退出回执'
    $receipt.MainProcessStartUtcTicks=101
    Check (-not (Test-StopReceipt $receipt $main 200)) '清场拒绝PID复用回执'
    $receipt.MainProcessStartUtcTicks=100; $receipt.PowerOff=$false
    Check (-not (Test-StopReceipt $receipt $main 200)) '通信失败不能伪报断能'
    $receipt.PowerOff=$true; $receipt.RequestedUtcTicks=199
    Check (-not (Test-StopReceipt $receipt $main 200)) '历史停止回执不能授权本次清场'
    $receipt.RequestedUtcTicks=201; $receipt.RelaunchDisposition=2
    Check (-not (Test-StopReceipt $receipt $main 200)) '自动续跑许可不能授权人工清场'
    $jsonPath = Join-Path $temp 'maintenance.json'
    Write-MaintenanceJson $jsonPath @{TransactionId='same';CleanupCompleted=$false}
    Write-MaintenanceJson $jsonPath @{TransactionId='same';CleanupCompleted=$true}
    Check ((Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json).CleanupCompleted) '维护事务原子更新'
    foreach ($name in @('Stop-RelatedProcesses.ps1','Export-StabilityEvidence.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $temp }
    $saved = $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY
    try {
        $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY='1'
        foreach ($name in @('一键停止全部相关进程.cmd','恢复后台服务.cmd','一键故障采证.cmd')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('QuickDeploy\' + $name)) -Destination $temp
            $commandPath = Join-Path $temp $name
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = $env:ComSpec
            $info.Arguments = '/d /s /v:off /c ""' + $commandPath + '""'
            $info.UseShellExecute=$false; $info.CreateNoWindow=$true
            $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
            $process = [Diagnostics.Process]::Start($info)
            try {
                $out = $process.StandardOutput.ReadToEnd()
                $errorText = $process.StandardError.ReadToEnd()
                $process.WaitForExit()
                Check ($process.ExitCode -eq 0 -and $out.Contains('QUICKDEPLOY_MAINTENANCE_PARSE_PASS')) "中文及特殊字符路径入口：$name $errorText"
            } finally { $process.Dispose() }
        }
    } finally { $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY=$saved }
    Write-Output "PASS V216Deployment $passed/$passed (isolated filesystem; no SCM or hardware mutation)"
}
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '测试清理路径越界。' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
