$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Web.Extensions
$testAssembly=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'bin\Debug\IndependentRecovery.ProcessTests.exe'))
$fixtureType=@($testAssembly.GetTypes()|Where-Object Name -eq 'IndependentProjectStateTests')[0]
$factory=$fixtureType.GetMethod('CreateRegistrationFixture',[Reflection.BindingFlags]'NonPublic,Static')
$registration=$factory.Invoke($null,@('D:\IsolatedUpgradeProject'))
$registration.ExecutablePath='D:\IsolatedUpgrade\Current\app.exe'
$registration.SafetyExecutablePath='D:\IsolatedUpgrade\Current\safety.exe'
$state=[MTTFTest.Watchdog.Protocol.IndependentProjectState]::new();$state.Revision=1
$next=[pscustomobject]@{Destination='D:\IsolatedUpgrade';Files=@(
    [pscustomobject]@{Relative='Current/app.exe';Sha256=('a'*64)},
    [pscustomobject]@{Relative='Current/safety.exe';Sha256=('b'*64)})}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
$node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-IndependentUpgradeRegistration'},$true)
. ([scriptblock]::Create($node.Extent.Text))
$json=[System.Web.Script.Serialization.JavaScriptSerializer]::new()
$before=$json.Serialize($registration);$stateBefore=$json.Serialize($state)
$copy=Get-IndependentUpgradeRegistration $registration $state $next
if($copy.ExecutableSha256 -ne ('a'*64) -or $copy.SafetyExecutableSha256 -ne ('b'*64)){throw 'Executable hashes not migrated'}
$copy.ExecutableSha256=$registration.ExecutableSha256;$copy.SafetyExecutableSha256=$registration.SafetyExecutableSha256
if($json.Serialize($copy) -ne $before -or $json.Serialize($state) -ne $stateBefore){throw 'Non-executable metadata or state changed'}
$copy.Files[0].Sha256='c'*64
if($json.Serialize($registration) -ne $before){throw 'Preparation shared mutable configuration with original'}
$state.Maintenance=$false;$failed=$false
try{Get-IndependentUpgradeRegistration $registration $state $next|Out-Null}catch{$failed=$true}
if(-not $failed){throw 'Unmaintained registration accepted'}
$state.Maintenance=$true;$next.Files[0].Relative='Current/other.exe';$failed=$false
try{Get-IndependentUpgradeRegistration $registration $state $next|Out-Null}catch{$failed=$true}
if(-not $failed){throw 'Executable path migration accepted'}
Write-Output 'PASS upgrade registration metadata clone, maintenance gate and path binding 3/3; no files modified'
