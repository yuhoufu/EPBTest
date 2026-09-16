#requires -Version 5.1
param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'EPB_TEST_ARTIFACT_ROOT required'}
$root=Join-Path $EvidenceRoot ('reinstall-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$script:passed=0
function Check([bool]$Value,[string]$Name){if(-not $Value){throw $Name};$script:passed++}
function Reject([scriptblock]$Action,[string]$Name){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Check $rejected $Name}
$source=Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'Installer parses under this host'
foreach($name in @('Get-IndependentRepairFiles','Get-IndependentInstallResumePlan','Restore-IndependentMissingPayloads','Invoke-IndependentReinstallChild')){
 $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
 if(-not $node){throw ('Missing production function '+$name)}
 . ([scriptblock]::Create($node.Extent.Text))
}
$destination=Join-Path $root 'installed';$bundle=Join-Path $root 'bundle'
$files=@()
foreach($relative in @('Current/AsyncTcpClient.dll','Current/MTTFTest.exe','Current/Config/TestConfig.xml','Tools/Complete-IndependentSetup.ps1')){
 $path=Join-Path $bundle $relative;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null
 [IO.File]::WriteAllText($path,('FIXTURE ONLY '+$relative))
 $files+=,[pscustomobject]@{Relative=$relative;Source=$path;Sha256=(Get-FileHash $path).Hash}
}
$plan=[pscustomobject]@{Version='4.1.0.5';Destination=$destination;Files=$files}
$receipt=[pscustomobject]@{schemaVersion=1;version=$plan.Version;installRoot=$destination;files=@($files|ForEach-Object{[pscustomobject]@{path=$_.Relative;sha256=$_.Sha256}})}
$setup=[pscustomobject]@{version=$plan.Version;stage='Ready'}
Restore-IndependentMissingPayloads $plan
Check (-not (Get-IndependentInstallResumePlan $plan $receipt $setup).NeedsRepair) 'Healthy install only needs shortcut reconciliation'
$dll=Join-Path $destination 'Current\AsyncTcpClient.dll';$config=Join-Path $destination 'Current\Config\TestConfig.xml'
$before=(Get-FileHash $config).Hash
[IO.File]::Delete($dll)
$resume=Get-IndependentInstallResumePlan $plan $receipt $setup
Check ($resume.NeedsRepair -and $resume.Missing -contains 'Current/AsyncTcpClient.dll') 'Reported missing DLL selects repair without hashing nonexistent file'
Restore-IndependentMissingPayloads $plan
Check ((Get-FileHash $dll).Hash -eq $files[0].Sha256) 'Missing DLL restored from verified payload'
Check ((Get-FileHash $config).Hash -eq $before) 'Existing configuration preserved'
[IO.File]::WriteAllText($config,'USER CONFIGURATION')
Reject {Get-IndependentInstallResumePlan $plan $receipt $setup} 'Changed configuration is not reset'
Restore-IndependentMissingPayloads $plan
Check ([IO.File]::ReadAllText($config) -eq 'USER CONFIGURATION') 'Missing-only copy does not overwrite existing configuration'
[IO.File]::Copy($files[2].Source,$config,$true)
[IO.File]::WriteAllText($dll,'CORRUPTED PROGRAM')
Check ((Get-IndependentInstallResumePlan $plan $receipt $setup).Changed -contains 'Current/AsyncTcpClient.dll') 'Owned damaged binary selects guarded repair'
[IO.File]::Copy($files[0].Source,$dll,$true)
$setup.stage='AwaitingProject'
Check (-not (Get-IndependentInstallResumePlan $plan $receipt $setup).NeedsRepair) 'Healthy pending setup keeps pending shortcut'
$setup.stage='Installing'
Check ((Get-IndependentInstallResumePlan $plan $receipt $setup).NeedsRepair) 'Interrupted setup retries finalization even after complete copy'
$setup.stage='Binding'
Check ((Get-IndependentInstallResumePlan $plan $receipt $setup).NeedsRepair) 'Interrupted binding is not reported as installed'
$setup.stage='Uninstalled'
foreach($file in $files){[IO.File]::Delete((Join-Path $destination $file.Relative))}
$resume=Get-IndependentInstallResumePlan $plan $receipt $setup
Check ($resume.NeedsRepair -and $resume.Missing.Count -eq 4) 'Uninstall then install keeps directory and receipt while selecting reinstallation'
Restore-IndependentMissingPayloads $plan
Check ((Get-FileHash $config).Hash -eq $before) 'Missing packaged template restored without deleting installation directory'
$setup.stage='Ready';$receipt.files[0].sha256='a'*64
Reject {Get-IndependentInstallResumePlan $plan $receipt $setup} 'Different build cannot use repair ownership'
$receipt.files[0].sha256=$files[0].Sha256;$setup.stage='Unknown'
Reject {Get-IndependentInstallResumePlan $plan $receipt $setup} 'Unknown setup state is not guessed'
$probe=Join-Path $root '子进程.ps1'
[IO.File]::WriteAllText($probe,@'
param([string]$Mode,[string]$BundleDirectory,[string]$InstallRoot)
[IO.File]::WriteAllText((Join-Path $InstallRoot 'child.json'),(@{pid=$PID;mode=$Mode;bundle=$BundleDirectory;root=$InstallRoot}|ConvertTo-Json))
exit ([int]$env:EPB_REINSTALL_TEST_EXIT)
'@,[Text.UTF8Encoding]::new($true))
$saved=$env:EPB_REINSTALL_TEST_EXIT;$modules=$env:PSModulePath
try{
 $env:EPB_REINSTALL_TEST_EXIT='0';Invoke-IndependentReinstallChild $probe $bundle $root
 $child=[IO.File]::ReadAllText((Join-Path $root 'child.json'))|ConvertFrom-Json
 Check ($child.pid -ne $PID -and $child.mode -eq 'Reinstall') 'Reinstallation runs in isolated process'
 Check ($child.bundle -eq $bundle -and $child.root -eq $root) 'Reinstallation paths survive native argument passing'
 $env:EPB_REINSTALL_TEST_EXIT='17'
 Reject {Invoke-IndependentReinstallChild $probe $bundle $root} 'Failed reinstallation cannot report install success'
 Check ($env:PSModulePath -eq $modules) 'Child failure restores module environment'
}finally{$env:EPB_REINSTALL_TEST_EXIT=$saved;$env:PSModulePath=$modules}
Write-Output ('PASS reinstall resume '+$script:passed+' checks; '+$root)
