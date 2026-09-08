#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'Install-MTTFTest-RecoveryGuard.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-GuardTaskXml'
}, $true)
if ($null -eq $definition) { throw '任务定义函数缺失。' }
# Load only the pure production XML function. Never execute installer main,
# register a task, or write to the actual installation authority in this test.
Invoke-Expression $definition.Extent.Text
$ownershipDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-GuardTaskOwnership'
}, $true)
if ($null -eq $ownershipDefinition) { throw '任务归属函数缺失。' }
Invoke-Expression $ownershipDefinition.Extent.Text
$base = 'C:\Guard & Test'
$exe = Join-Path $base 'MTTFTest.RecoveryGuard.exe'
$settings = Join-Path $base 'guard-settings.json'
$passed = 0
foreach ($execution in @($false, $true)) {
    $journal = Join-Path $base $(if ($execution) { 'execution-journal' } else { 'journal' })
    [xml]$xml = New-GuardTaskXml $exe $base $settings $journal $execution (-not $execution)
    $ns = New-Object Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    function Read-Value([string]$Path) { $xml.SelectSingleNode('/t:Task/' + $Path, $ns).InnerText }
    if ((Read-Value 't:Settings/t:MultipleInstancesPolicy') -ne 'IgnoreNew') { throw '必须禁止同任务重叠实例。' }
    if ((Read-Value 't:Principals/t:Principal/t:UserId') -ne 'S-1-5-18') { throw '必须使用 SYSTEM。' }
    if ((Read-Value 't:Settings/t:StartWhenAvailable') -ne 'true' -or
        (Read-Value 't:Triggers/t:TimeTrigger/t:Repetition/t:Interval') -ne 'PT1M' -or
        (Read-Value 't:Triggers/t:BootTrigger/t:Enabled') -ne 'true') { throw '启动或补跑触发缺失。' }
    if ((Read-Value 't:Actions/t:Exec/t:Command') -ne $exe -or
        (Read-Value 't:Actions/t:Exec/t:WorkingDirectory') -ne $base) { throw 'XML 转义破坏路径。' }
    $argsText = Read-Value 't:Actions/t:Exec/t:Arguments'
    $expectedVerb = if ($execution) { '--execute' } else { '--check' }
    if ($argsText -ne ($expectedVerb + ' --settings "' + $settings + '" --journal "' + $journal + '"')) { throw '任务参数丢失或错误。' }
    if ($execution) {
        if ((Read-Value 't:Settings/t:ExecutionTimeLimit') -ne 'PT0S' -or
            (Read-Value 't:Settings/t:AllowHardTerminate') -ne 'false' -or
            (Read-Value 't:Settings/t:Enabled') -ne 'false') { throw '执行任务不得被检查期限中断或提前启用。' }
    } elseif ((Read-Value 't:Settings/t:ExecutionTimeLimit') -ne 'PT45S' -or
        (Read-Value 't:Settings/t:AllowHardTerminate') -ne 'true' -or
        (Read-Value 't:Settings/t:Enabled') -ne 'true') { throw '检查任务必须有界启用。' }
    $passed++
    $null = Assert-GuardTaskOwnership $xml.OuterXml $base $base $execution
    if ($xml.Task.Principals.Principal.LogonType) { throw 'SYSTEM task XML must use the Windows-export-compatible omitted LogonType form' }
    $passed++
}
foreach ($change in @('Command', 'Arguments', 'WorkingDirectory', 'UserId', 'LogonType', 'AdditionalAction', 'Context')) {
    [xml]$foreign = New-GuardTaskXml $exe $base $settings (Join-Path $base 'journal') $false $true
    switch ($change) {
        'Command' { $foreign.Task.Actions.Exec.Command = 'C:\Unrelated\tool.exe' }
        'Arguments' { $foreign.Task.Actions.Exec.Arguments += ' --root "C:\Other"' }
        'WorkingDirectory' { $foreign.Task.Actions.Exec.WorkingDirectory = 'C:\Other' }
        'UserId' { $foreign.Task.Principals.Principal.UserId = 'S-1-5-19' }
        'LogonType' {
            $node = $foreign.CreateElement('LogonType', $foreign.Task.NamespaceURI)
            $node.InnerText = 'InteractiveToken'
            [void]$foreign.Task.Principals.Principal.AppendChild($node)
        }
        'AdditionalAction' { $null = $foreign.Task.Actions.AppendChild($foreign.Task.Actions.Exec.CloneNode($true)) }
        'Context' { $foreign.Task.Actions.Context = 'OtherPrincipal' }
    }
    $rejected = $false
    try { Assert-GuardTaskOwnership $foreign.OuterXml $base $base $false }
    catch { if ($_.Exception.Message -notlike '*同名任务不属于*') { throw }; $rejected = $true }
    if (-not $rejected) { throw "任务归属未拒绝 $change" }
    $passed++
}
foreach ($bad in @('relative.exe', 'C:\bad"argument.exe', "C:\bad`nargument.exe", 'C:\ends\')) {
    $rejected = $false
    try { $null = New-GuardTaskXml $bad $base $settings (Join-Path $base 'journal') $true $false }
    catch { if ($_.Exception.Message -notlike '*任务路径无效*') { throw }; $rejected = $true }
    if (-not $rejected) { throw '危险任务路径未拒绝。' }
    $passed++
}
$guardBinary = Join-Path $PSScriptRoot "..\MTTFTest.RecoveryGuard\bin\$Configuration\MTTFTest.RecoveryGuard.exe"
$guardAssembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($guardBinary))
$validateTask = $guardAssembly.GetType('MTTFTest.RecoveryGuard.RecoveryWorkerDispatch').GetMethod('ValidateTask', [Reflection.BindingFlags]'Static, NonPublic')
$executionJournal = Join-Path $base 'execution-journal'
$enabledXml = New-GuardTaskXml $exe $base $settings $executionJournal $true $true
$null = $validateTask.Invoke($null, [string[]]@($enabledXml, $exe, $settings, $executionJournal))
$passed++
[xml]$exportedEnabledXml = $enabledXml
$enabledNode = $exportedEnabledXml.SelectSingleNode('/t:Task/t:Settings/t:Enabled', $ns)
[void]$enabledNode.ParentNode.RemoveChild($enabledNode)
$null = $validateTask.Invoke($null, [string[]]@($exportedEnabledXml.OuterXml, $exe, $settings, $executionJournal))
$passed++
$disabledXml = New-GuardTaskXml $exe $base $settings $executionJournal $true $false
$disabledRejected = $false
try { $null = $validateTask.Invoke($null, [string[]]@($disabledXml, $exe, $settings, $executionJournal)) }
catch { if ($_.Exception.GetBaseException().Message -ne 'RecoveryWorkerTaskDefinitionMismatch') { throw }; $disabledRejected = $true }
if (-not $disabledRejected) { throw '执行入口未拒绝安装器生成的禁用任务。' }
$passed++
[void]($modeDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-GuardRecoveryMode'
}, $true))
if ($null -eq $modeDefinition) { throw 'Mode policy missing' }
Invoke-Expression $modeDefinition.Extent.Text
foreach ($ready in @($false, $true)) {
    foreach ($mode in @('ObserveOnly', 'RecoverExited', 'RecoverStalled')) {
        $identity = [pscustomobject]@{ schemaVersion=$(if ($ready) {3} else {2}); deliveryStage=$(if ($ready) { 'AutomaticRecovery' } else { 'ObserveOnlyCommissioning' });
            automaticExecutionReady=$ready; configuration='Release'; builtFromVerifiedInputs=$true; gitDirty=$false;
            approvedModes=@('RecoverExited','RecoverStalled') }
        $config = [pscustomobject]@{ Mode=0; SupervisionExpirySeconds=3600 }
        $rejected = $false
        try { $selected = Resolve-GuardRecoveryMode $identity $config $mode }
        catch { if ($_.Exception.Message -notlike '*未开放自动恢复*') { throw }; $rejected = $true }
        if ($rejected -ne (-not $ready -and $mode -ne 'ObserveOnly')) { throw 'Mode readiness gate mismatch' }
        if (-not $rejected -and $selected -ne [Array]::IndexOf(@('ObserveOnly','RecoverExited','RecoverStalled'),$mode)) { throw 'Wrong selected mode' }
        $passed++
    }
}
$config.Mode = 2
if ((Resolve-GuardRecoveryMode $identity $config '') -ne 2) { throw 'Upgrade discarded existing recovery mode' }
$passed++
$identity.builtFromVerifiedInputs = $false
$rejected = $false
try { $null = Resolve-GuardRecoveryMode $identity $config '' } catch { $rejected = $true }
if (-not $rejected) { throw 'Unbound binaries allowed activation' }
$passed++
[void]($identity.builtFromVerifiedInputs = $true)
foreach ($case in @('LegacyAutomaticSchema','UnapprovedMode','DirtyBuild')) {
    $identity.schemaVersion=3; $identity.approvedModes=@('RecoverExited','RecoverStalled'); $identity.gitDirty=$false
    if ($case -eq 'LegacyAutomaticSchema') { $identity.schemaVersion=2 }
    if ($case -eq 'UnapprovedMode') { $identity.approvedModes=@('RecoverExited') }
    if ($case -eq 'DirtyBuild') { $identity.gitDirty=$true }
    $rejected=$false
    try { $null=Resolve-GuardRecoveryMode $identity $config 'RecoverStalled' } catch { $rejected=$true }
    if (-not $rejected) { throw "Automatic mode admitted invalid scope: $case" }
    $passed++
}
[ordered]@{ passed = $passed; taskRegistrationPerformed = $false; scope = 'ProductionTaskXmlContract' } | ConvertTo-Json
