#requires -Version 5.1
param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'EPB_TEST_ARTIFACT_ROOT required'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repo 'Tools\Independent-InstallSetup.ps1')
$root=Join-Path $EvidenceRoot ('oneclick-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$script:passed=0
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message};$script:passed++}
function Reject([scriptblock]$Action,[string]$Message){$failed=$false;try{& $Action}catch{$failed=$true};Check $failed $Message}
$project=Join-Path $root '现有项目 空格';[IO.Directory]::CreateDirectory((Join-Path $project 'Config'))|Out-Null
[IO.File]::WriteAllText((Join-Path $project 'Config\TestConfig.xml'),'<TestConfig/>')
[IO.File]::WriteAllText((Join-Path $project 'index.db'),'EXISTING DATA MUST REMAIN')
$digest=(Get-FileHash (Join-Path $project 'index.db')).Hash
$user=Join-Path $root 'user-state.json';$runtime=Join-Path $root 'TestConfig.xml'
Check ((Find-IndependentSetupProject $user $runtime) -eq '') 'Clean machine must await project'
[IO.File]::WriteAllText($runtime,('<TestConfig><Basic><StoreDir>'+[Security.SecurityElement]::Escape($root)+'</StoreDir><TestName>现有项目 空格</TestName></Basic></TestConfig>'))
Check ((Find-IndependentSetupProject $user $runtime) -eq $project) 'Existing runtime project is discovered'
[IO.File]::WriteAllText($user,(@{schemaVersion=1;storeDir=$root;testName='缺失项目'}|ConvertTo-Json))
Check ((Find-IndependentSetupProject $user $runtime) -eq '') 'Broken last selection cannot silently select another project'
[IO.File]::WriteAllText($user,(@{schemaVersion=1;storeDir=$root;testName='现有项目 空格'}|ConvertTo-Json))
Check ((Find-IndependentSetupProject $user $runtime) -eq $project) 'Last selected project restored'
[IO.File]::WriteAllText($user,(@{schemaVersion=1;storeDir=$root;testName='..'}|ConvertTo-Json))
Check ((Find-IndependentSetupProject $user $runtime) -eq '') 'Traversal rejected'
[IO.File]::WriteAllText($user,'broken')
Reject {Find-IndependentSetupProject $user $runtime} 'Malformed selection rejected'
[IO.File]::Delete($user)
[IO.File]::WriteAllText($runtime,'<!DOCTYPE x [<!ENTITY e SYSTEM "file:///C:/Windows/win.ini">]><TestConfig>&e;</TestConfig>')
Reject {Find-IndependentSetupProject $user $runtime} 'External XML entities rejected'
[IO.File]::WriteAllText($user,('x'*(1MB+1)))
Reject {Read-IndependentSetupJson $user} 'Oversize selection bounded'
$sid='S-1-5-21-1-2-3-1001'
$resolved=Resolve-IndependentInstallContext $project $sid
Check ($resolved.InteractiveUserSid -eq $sid -and $resolved.ProjectDirectory -eq $project) 'Captured pre-UAC user retained'
Reject {Resolve-IndependentInstallContext (Join-Path $root 'missing') $sid} 'Explicit missing project does not fabricate data'
Reject {Resolve-IndependentInstallContext $project 'S-1-5-18'} 'SYSTEM cannot become interactive user'
$state=[pscustomobject]@{schemaVersion=1;version='4.1.0.3';stage='AwaitingProject';interactiveUserSid=$sid;projectDirectory=''}
Write-IndependentSetupState $root $state
Check ((Read-IndependentSetupJson (Join-Path $root 'install-setup.json')).stage -eq 'AwaitingProject') 'Cancellation keeps durable pending state'
Reject {Invoke-IndependentSetupStages $root $state {throw 'Should not execute'}} 'Missing project never invokes registration'
$state.projectDirectory=$project
foreach($failure in @('Recovery','SessionHost','Shortcut','Verify','RemoveSetupShortcut')){
    $events=[Collections.Generic.List[string]]::new()
    Reject {Invoke-IndependentSetupStages $root $state {param($step);$events.Add($step);if($step -eq $failure){throw 'Injected failure'}}} ('Failure propagates: '+$failure)
    Check ((Read-IndependentSetupJson (Join-Path $root 'install-setup.json')).stage -eq 'Binding') ('Never reports Ready after '+$failure)
    Check ($events[$events.Count-1] -eq $failure) 'No later side effects after failed step'
}
$events=[Collections.Generic.List[string]]::new()
Invoke-IndependentSetupStages $root $state {param($step);$events.Add($step)}
Check (($events -join ',') -eq 'Recovery,SessionHost,Shortcut,Verify,RemoveSetupShortcut') 'Binding sequence is explicit'
Check ((Read-IndependentSetupJson (Join-Path $root 'install-setup.json')).stage -eq 'Ready') 'Retry can complete after verified steps'
Check ((Get-FileHash (Join-Path $project 'index.db')).Hash -eq $digest) 'Existing database preserved through all setup scenarios'
$savedProgramData=$env:ProgramData
try{
    $env:ProgramData=Join-Path $root 'program-data'
    $installed=Join-Path $root 'installed';$templates=Join-Path $installed 'Current\Config'
    [IO.Directory]::CreateDirectory($templates)|Out-Null
    $runtime=Join-Path $env:ProgramData 'MTTFTest\Config';[IO.Directory]::CreateDirectory($runtime)|Out-Null
    [IO.File]::WriteAllText((Join-Path $runtime 'AIConfig.xml'),'PRESERVED FIELD CONFIG')
    foreach($name in @('AIConfig.xml','AlarmConfig.xml','AOConfig.xml','DOConfig.xml','PowerSupplyConfig.xml','TestConfig.xml','UnattendedAlarmConfig.xml','UIConfig.xml')){
        [IO.File]::WriteAllText((Join-Path $templates $name),'TEMPLATE')
    }
    Initialize-IndependentSetupConfig $installed
    Check ([IO.File]::ReadAllText((Join-Path $runtime 'AIConfig.xml')) -eq 'PRESERVED FIELD CONFIG') 'Existing machine configuration preserved'
    Check ([IO.File]::ReadAllText((Join-Path $runtime 'AOConfig.xml')) -eq 'TEMPLATE') 'Missing configuration initialized'
    Check ([IO.File]::Exists((Join-Path $installed 'Current\MTTFTest.FirstRun.configured'))) 'Legacy bootstrap is disabled for independent setup'
}finally{$env:ProgramData=$savedProgramData}
foreach($file in @('Independent-InstallSetup.ps1','Complete-IndependentSetup.ps1','Install-AutomaticRecoveryBundle.ps1','Install-IndependentRecoveryBundle.ps1','New-AutomaticRecoveryBundle.ps1')){
    $tokens=$null;$errors=$null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo ('Tools\'+$file)),[ref]$tokens,[ref]$errors)|Out-Null
    Check ($errors.Count -eq 0) ('Windows PowerShell parses '+$file)
}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-IndependentBundlePlan'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$bundle=Join-Path $root 'package';$entries=@()
foreach($relative in @('Base/MTTFTest.exe','Base/MTTFTest.SafetyAgent.exe','Base/MTTFTest.Watchdog.Protocol.dll',
    'FallbackGuard/MTTFTest.FallbackGuard.exe','FallbackGuard/MTTFTest.Watchdog.Protocol.dll',
    'FallbackGuard/System.Data.SQLite.dll','FallbackGuard/x86/SQLite.Interop.dll','Tools/Manage-IndependentRecovery.ps1',
    'Tools/Independent-InstallSetup.ps1','Tools/Complete-IndependentSetup.ps1')){
    $path=Join-Path $bundle $relative;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null
    [IO.File]::WriteAllText($path,'NOT EXECUTABLE - PACKAGE VALIDATION FIXTURE')
    $entries+=,[ordered]@{path=$relative;sha256=(Get-FileHash $path).Hash}
}
$manifest=[ordered]@{schemaVersion=2;version='4.1.0.3';recoveryArchitecture='V4-Independent-SystemExecutor';files=$entries}
[IO.File]::WriteAllText((Join-Path $bundle 'automatic-bundle.json'),($manifest|ConvertTo-Json -Depth 4))
$plan=Get-IndependentBundlePlan $bundle (Join-Path $root 'destination')
Check (@($plan.Files|Where-Object Relative -Like '*InstallSetup.ps1').Count -eq 1 -and @($plan.Files|Where-Object Relative -Like '*IndependentSetup.ps1').Count -eq 1) 'First-project scripts are installed payloads'
$manifest.files=@($entries|Where-Object path -ne 'Tools/Complete-IndependentSetup.ps1')
[IO.File]::WriteAllText((Join-Path $bundle 'automatic-bundle.json'),($manifest|ConvertTo-Json -Depth 4))
Reject {Get-IndependentBundlePlan $bundle (Join-Path $root 'destination')} 'New package without setup launcher rejected before install'
Write-Output ('PASS one-click setup '+$script:passed+' checks; NO INSTALLATION OR HARDWARE; '+$root)
