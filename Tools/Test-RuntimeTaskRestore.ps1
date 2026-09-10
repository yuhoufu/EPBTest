param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Restore-InstalledRuntimeTasks'
}, $true)
if ($null -eq $definition) { throw 'Task restore function missing' }
. ([scriptblock]::Create($definition.Extent.Text))
$xmlDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-NormalizedScheduledTaskXml'
}, $true)
if ($null -eq $xmlDefinition) { throw 'Task XML normalizer missing' }
. ([scriptblock]::Create($xmlDefinition.Extent.Text))
$startDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Start-RestoredRuntimeTasks'
}, $true)
if ($null -eq $startDefinition) { throw 'Deferred task start function missing' }
. ([scriptblock]::Create($startDefinition.Extent.Text))

$autoStartTaskName = 'FixtureAuto'
$taskName = 'FixtureAgent'
$healthTaskName = 'FixtureHealth'
$root = 'C:\FixtureOnly\MTTFTest'
$reference = [pscustomobject]@{
    Path = 'fixture.json'; Sha256 = ('A' * 64); TransactionId = ('b' * 32)
    InstallRoot = $root; MachineName = $env:COMPUTERNAME
}
$script:operations = @()
$script:present = @{
    FixtureAuto = $true
    FixtureAgent = $true
    FixtureHealth = $true
}
$script:sddl = @{}
$saved = [pscustomobject]@{
    transactionId = $reference.TransactionId
    installRoot = $root
    machineName = $env:COMPUTERNAME
    tasks = @(
        [pscustomobject]@{ name='FixtureAuto'; path='\'; existed=$true; wasRunning=$true;
            xml='<Task><Auto /></Task>'; securityDescriptor='O:SYG:SYD:(A;;FA;;;SY)' }
        [pscustomobject]@{ name='FixtureAgent'; path='\'; existed=$false; wasRunning=$false;
            xml=$null; securityDescriptor=$null }
        [pscustomobject]@{ name='FixtureHealth'; path='\'; existed=$true; wasRunning=$false;
            xml='<Task><Health /></Task>'; securityDescriptor='O:SYG:SYD:(A;;FA;;;SY)' }
    )
}
function Read-DeploymentTaskBackup {
    param($Path, $Root, $ExpectedSha256)
    if ($Path -cne $reference.Path -or $Root -cne $root -or $ExpectedSha256 -cne $reference.Sha256) {
        throw 'Wrong restore reader identity'
    }
    $script:operations += 'read'
    return $saved
}
function Get-ScheduledTask {
    [CmdletBinding()]param($TaskName, $TaskPath)
    if ($TaskPath -cne '\') { throw 'Wrong task path' }
    $script:operations += "query:$TaskName"
    if ($script:present[$TaskName]) { [pscustomobject]@{ TaskName=$TaskName; TaskPath='\' } }
}
function Stop-ScheduledTask {
    [CmdletBinding()]param($TaskName, $TaskPath)
    $script:operations += "stop:$TaskName"
}
function Unregister-ScheduledTask {
    [CmdletBinding()]param($TaskName, $TaskPath, [switch]$Confirm)
    $script:operations += "unregister:$TaskName"
    $script:present[$TaskName] = $false
}
function Register-ScheduledTask {
    [CmdletBinding()]param($TaskName, $TaskPath, $Xml, [switch]$Force)
    $script:operations += "register:${TaskName}:$Xml"
    $script:present[$TaskName] = $true
    [pscustomobject]@{ TaskName=$TaskName }
}
function Export-ScheduledTask {
    [CmdletBinding()]param($TaskName, $TaskPath)
    $script:operations += "export:$TaskName"
    return [string](@($saved.tasks | Where-Object { $_.name -ceq $TaskName })[0].xml)
}
function Set-InstalledTaskSecurityDescriptor {
    param($Name, $SecurityDescriptor)
    $script:operations += "security:$Name"
    $script:sddl[$Name] = $SecurityDescriptor
}
function Get-InstalledTaskSecurityDescriptor {
    param($Name)
    $script:operations += "verify-security:$Name"
    if ($script:sddl.ContainsKey($Name)) { return $script:sddl[$Name] }
    return 'O:SYG:SYD:(A;;FR;;;SY)'
}
function Start-ScheduledTask {
    [CmdletBinding()]param($TaskName, $TaskPath)
    $script:operations += "start:$TaskName"
}

$deferredRunning = @(Restore-InstalledRuntimeTasks $reference $root -DeferRunningStart)
Start-RestoredRuntimeTasks $deferredRunning
if (-not $script:present.FixtureAuto -or $script:present.FixtureAgent -or
    -not $script:present.FixtureHealth) { throw 'Original existence state was not restored' }
$expected = @(
    'read',
    'query:FixtureAuto','stop:FixtureAuto','unregister:FixtureAuto','register:FixtureAuto:<Task><Auto /></Task>',
    'export:FixtureAuto',
    'verify-security:FixtureAuto','security:FixtureAuto','verify-security:FixtureAuto',
    'query:FixtureAgent','stop:FixtureAgent','unregister:FixtureAgent',
    'query:FixtureHealth','stop:FixtureHealth','unregister:FixtureHealth','register:FixtureHealth:<Task><Health /></Task>',
    'export:FixtureHealth',
    'verify-security:FixtureHealth','security:FixtureHealth','verify-security:FixtureHealth','start:FixtureAuto'
)
if (($script:operations -join '|') -cne ($expected -join '|')) {
    throw "Unexpected restore sequence: $($script:operations -join '|')"
}
Write-Output 'PASS task restore: exact existence, XML, OWNER/GROUP/DACL and prior running state'

$badReference = [pscustomobject]@{
    Path = $reference.Path; Sha256 = $reference.Sha256; TransactionId = ('c' * 32)
    InstallRoot = $root; MachineName = $env:COMPUTERNAME
}
$caught = $null
try { Restore-InstalledRuntimeTasks $badReference $root } catch { $caught = $_ }
if ($null -eq $caught -or $caught.Exception.Message -ne 'TaskRestoreReferenceMismatch') {
    throw "Mismatched publisher reference was accepted: $caught"
}
Write-Output 'PASS task restore: mismatched protected publisher reference rejected before mutation'
