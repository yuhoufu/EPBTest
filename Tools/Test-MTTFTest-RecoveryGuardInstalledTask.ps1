#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [string]$OutputDirectory = '',
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($PackageDirectory)
$installer = Join-Path $source 'Install-MTTFTest-RecoveryGuard.ps1'
# Validate the copied package using its own manifest before loading any task
# definition. This does not register a task or modify installation authority.
$null = & $installer -Mode Validate -SourceDirectory $source
$identity = Get-Content -LiteralPath (Join-Path $source 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($identity.deliveryStage -ne 'ObserveOnlyCommissioning' -or $identity.automaticExecutionReady -ne $false) {
    throw '隔离任务验收只接受 ObserveOnly 校验包。'
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$administrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $PlanOnly -and -not $administrator) {
    throw 'SYSTEM 任务验收需要管理员 PowerShell。可先用 -PlanOnly 生成隔离验收计划；计划不代表安装态通过。'
}
$token = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot ('..\artifacts\recoveryguard-installed-task-' + $token)
}
$root = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (Test-Path -LiteralPath $root) { throw '验收输出目录已存在，不覆盖历史证据。' }
$control = Join-Path $root 'isolated-control'
$formalControl = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'MTTFTest\RecoveryControl'
if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\') -or
    $root.Equals($formalControl, [StringComparison]::OrdinalIgnoreCase) -or
    $root.StartsWith($formalControl + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '验收路径不能使用正式授权目录。' }
[void](New-Item -ItemType Directory -Path $root)
$binaryDirectory = Join-Path $root 'independent-package'
[void](New-Item -ItemType Directory -Path $binaryDirectory)
foreach ($file in @($identity.files | ForEach-Object { $_.name }) + @('guard-identity.json')) {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $binaryDirectory
}
$null = & (Join-Path $binaryDirectory 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Validate -SourceDirectory $binaryDirectory
$exe = Join-Path $binaryDirectory 'MTTFTest.RecoveryGuard.exe'
$settings = Join-Path $binaryDirectory 'guard-settings.json'
$settingsValue = Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
if ($settingsValue.Mode -ne 0) { throw '验收禁止恢复或操作硬件。' }

$parseTokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-GuardTaskXml'
}, $true)
if ($null -eq $definition) { throw '安装器任务定义函数缺失。' }
Invoke-Expression $definition.Extent.Text
$plans = @()
foreach ($execution in @($false, $true)) {
    $suffix = if ($execution) { 'Execution' } else { 'Scan' }
    $name = 'MTTFTestRecoveryGuard-Isolated-' + $token + '-' + $suffix
    $journal = Join-Path $root ($suffix + '-journal')
    [xml]$xml = New-GuardTaskXml $exe $binaryDirectory $settings $journal $execution $true
    $ns = New-Object Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    # Keep production identity, singleton and lifetime settings. Use manual
    # triggers only and an explicit isolated root so this test cannot recur or
    # read/write the real installation's control authority.
    $xml.SelectSingleNode('/t:Task/t:Triggers', $ns).RemoveAll()
    $argsNode = $xml.SelectSingleNode('/t:Task/t:Actions/t:Exec/t:Arguments', $ns)
    if ($control.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0) { throw '验收路径含非法任务参数字符。' }
    $argsNode.InnerText += ' --root "' + $control + '"'
    $xmlPath = Join-Path $root ($suffix + '-task.xml')
    $xml.Save($xmlPath)
    $plans += [pscustomobject]@{ Name=$name; Xml=$xml.OuterXml; XmlPath=$xmlPath;
        Arguments=$argsNode.InnerText; Journal=$journal; Execution=$execution }
}
$evidence = [ordered]@{
    schemaVersion=2; scope='GuardObserveOnlyTaskLifecycle'; packageVersion=$identity.version; packageConfiguration=$identity.configuration
    packageIdentitySha256=(Get-FileHash -LiteralPath (Join-Path $source 'guard-identity.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    guardExecutableSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    controlAssemblySha256=(Get-FileHash -LiteralPath (Join-Path $binaryDirectory 'MTTFTest.RecoveryControl.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    testScriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    createdUtc=[DateTime]::UtcNow.ToString('O'); automaticRecoveryVerified=$false; installationUpgradeRollbackVerified=$false
    mode='ObserveOnly'; planOnly=[bool]$PlanOnly; administrator=$administrator
    isolatedRoot=$control; businessExecutableAbsent=$true; taskRegistrationPerformed=$false
    actualTaskLifecycleVerified=$false; tasksRemoved=$false; cases=@(); failure=$null
    tasks=@($plans | ForEach-Object { @{ name=$_.Name; xml=$_.XmlPath;
        xmlSha256=(Get-FileHash -LiteralPath $_.XmlPath -Algorithm SHA256).Hash.ToLowerInvariant() } })
}
$reportPath = Join-Path $root 'results.json'
function Save-Report {
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
if ($PlanOnly) {
    Save-Report
    [ordered]@{ planOnly=$true; actualTaskLifecycleVerified=$false; evidence=$reportPath } | ConvertTo-Json
    return
}

function Assert-TestTask($Task, $Plan) {
    $actions = @($Task.Actions)
    if ($Task.TaskName -ne $Plan.Name -or $actions.Count -ne 1 -or
        $actions[0].Execute -ne $exe -or $actions[0].Arguments -ne $Plan.Arguments) {
        throw '任务身份已变化，禁止启动或清理。'
    }
}
function Invoke-TestTask($Plan, [int]$ExpectedEvents) {
    $task = Get-ScheduledTask -TaskName $Plan.Name -TaskPath '\' -ErrorAction Stop
    Assert-TestTask $task $Plan
    Start-ScheduledTask -TaskName $Plan.Name -TaskPath '\'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $eventPath = Join-Path $Plan.Journal 'guard-events.jsonl'
    while ($watch.Elapsed.TotalSeconds -lt 30) {
        Start-Sleep -Milliseconds 250
        $task = Get-ScheduledTask -TaskName $Plan.Name -TaskPath '\' -ErrorAction Stop
        $events = @()
        if (Test-Path -LiteralPath $eventPath) { $events = @(Get-Content -LiteralPath $eventPath -Encoding UTF8) }
        if ($events.Count -ge $ExpectedEvents -and $task.State -ne 'Running') {
            $info = Get-ScheduledTaskInfo -TaskName $Plan.Name -TaskPath '\'
            if ($info.LastTaskResult -ne 0) { throw ('隔离任务退出失败：' + $info.LastTaskResult) }
            $last = $events[-1] | ConvertFrom-Json
            if ($Plan.Execution) {
                if (-not $last.StopWorker -or $last.ActionDispatched -ne $false) { throw '观察执行任务出现动作或没有正常结束。' }
            } elseif ($last.Decision.Code -ne 'Unarmed' -or $last.Worker.TaskRequested -ne $false) {
                throw '隔离扫描错误地请求恢复。'
            }
            $evidence.cases += @{ name=$Plan.Name; invocation=$ExpectedEvents; passed=$true; exitCode=$info.LastTaskResult }
            return
        }
    }
    throw '隔离任务 30 秒内未完成，保留日志并清理本测试任务。'
}

$ownedTasks = New-Object 'System.Collections.Generic.HashSet[string]'
try {
    $missingMain = Join-Path $root 'missing-business\MTTFTest.exe'
    $null = & $exe --register --root $control --bench ('isolated-' + $token) --main $missingMain
    if ($LASTEXITCODE -ne 0) { throw '隔离授权注册失败。' }
    foreach ($plan in $plans) {
        if (Get-ScheduledTask -TaskName $plan.Name -TaskPath '\' -ErrorAction SilentlyContinue) { throw '随机验收任务名已存在。' }
        [void]$ownedTasks.Add($plan.Name)
        Register-ScheduledTask -TaskName $plan.Name -TaskPath '\' -Xml $plan.Xml | Out-Null
        $evidence.taskRegistrationPerformed = $true
        Invoke-TestTask $plan 1
        Invoke-TestTask $plan 2
    }
    $evidence.actualTaskLifecycleVerified = $true
}
catch { $evidence.failure = $_.Exception.Message; throw }
finally {
    try {
        foreach ($plan in $plans) {
            if (-not $ownedTasks.Contains($plan.Name)) { continue }
            $task = Get-ScheduledTask -TaskName $plan.Name -TaskPath '\' -ErrorAction SilentlyContinue
            if ($task) {
                Assert-TestTask $task $plan
                # Only this exact isolated ObserveOnly task can be stopped.
                Disable-ScheduledTask -TaskName $plan.Name -TaskPath '\' | Out-Null
                if ($task.State -eq 'Running') {
                    Stop-ScheduledTask -TaskName $plan.Name -TaskPath '\'
                    $stopWatch = [Diagnostics.Stopwatch]::StartNew()
                    while ((Get-ScheduledTask -TaskName $plan.Name -TaskPath '\').State -eq 'Running') {
                        if ($stopWatch.Elapsed.TotalSeconds -ge 3) { throw '隔离观察任务尚未退出，保留任务记录便于核查。' }
                        Start-Sleep -Milliseconds 100
                    }
                }
                Unregister-ScheduledTask -TaskName $plan.Name -TaskPath '\' -Confirm:$false
            }
            if (Get-ScheduledTask -TaskName $plan.Name -TaskPath '\' -ErrorAction SilentlyContinue) { throw '隔离任务未清理。' }
        }
        $evidence.tasksRemoved = $true
    }
    catch { $evidence.actualTaskLifecycleVerified=$false; $evidence.failure='Cleanup: ' + $_.Exception.Message; throw }
    finally { Save-Report }
}
[ordered]@{ passed=$evidence.cases.Count; actualTaskLifecycleVerified=$evidence.actualTaskLifecycleVerified;
    tasksRemoved=$evidence.tasksRemoved; evidence=$reportPath } | ConvertTo-Json
