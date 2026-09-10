param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8),
    [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$functionAst = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Install-ServiceAndAgent'
}, $true)
if ($null -eq $functionAst) { throw '缺少安装生产函数' }

# 在独立作用域替换全部原生命令；不加载或调用安装脚本入口。
foreach ($queryCode in @(0, 1060, 5, 1722)) {
$failureStages = if ($queryCode -eq 0) { @('config', 'failure', 'failureflag') }
    elseif ($queryCode -eq 1060) { @('create', 'failure', 'failureflag') }
    else { @('query') }
foreach ($failureStage in $failureStages) {
& {
    param($definition, $queryCode, $failureStage)
    . ([scriptblock]::Create($definition))
    $serviceName = 'FixtureSupervisor'
    $script:reachedTask = $false
    $script:operations = @()
    $script:accountArgumentsValid = $true
    function sc.exe {
        $script:operations += [string]$args[0]
        if ($args[0] -eq 'config' -and
            (($args -contains 'obj=') -or ($args -contains 'password='))) {
            $script:accountArgumentsValid = $false
        }
        if ($args[0] -eq 'create' -and
            (-not ($args -contains 'obj=') -or -not ($args -contains 'LocalSystem'))) {
            $script:accountArgumentsValid = $false
        }
        $global:LASTEXITCODE = if ($args[0] -eq 'query') { $queryCode }
            elseif ($args[0] -eq $failureStage) { 5 } else { 0 }
    }
    function New-ScheduledTaskAction { $script:reachedTask = $true; throw '不应进入任务安装' }
    function Start-Service { throw '不允许启动真实服务' }
    function Start-ScheduledTask { throw '不允许启动真实任务' }
    $caught = $null
    try { Install-ServiceAndAgent 'C:\FixtureOnly\MTTFTest' }
    catch { $caught = $_.Exception.Message }
    $expectedError = if ($failureStage -in @('config', 'create')) {
        '监督服务安装失败：5*'
    } elseif ($failureStage -eq 'failure') {
        'SCM 恢复策略设置失败：5*'
    } elseif ($failureStage -eq 'failureflag') {
        'SCM 非崩溃失败恢复策略设置失败：5*'
    } else { "无法确认监督服务是否存在，拒绝修改服务：$queryCode*" }
    if ($caught -notlike $expectedError) {
        throw "未传播原生命令失败：$caught"
    }
    if ($script:reachedTask) { throw '失败后仍注册任务' }
    if (-not $script:accountArgumentsValid) { throw '更新修改了原账户或新建服务缺少明确账户' }
    $expectedOperations = if ($queryCode -eq 0) { 'query,config,failure,failureflag' }
        elseif ($queryCode -eq 1060) { 'query,create,failure,failureflag' }
        else { 'query' }
    $expectedSequence = @($expectedOperations -split ',')
    $lastExpected = [Array]::IndexOf($expectedSequence, $failureStage)
    $expectedOperations = $expectedSequence[0..$lastExpected] -join ','
    if (($script:operations -join ',') -ne $expectedOperations) {
        throw "服务调用路径不匹配：$($script:operations -join ',')"
    }
    Write-Output "PASS 服务查询 $queryCode、失败阶段 $failureStage 的变更准入及失败传播"
} $functionAst.Extent.Text $queryCode $failureStage
}
}
