param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'EPB_TEST_ARTIFACT_ROOT required'}
$root=Join-Path $EvidenceRoot ('bundle-files-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors|Out-String)}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-IndependentBundlePlan'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$files=@()
foreach($name in @('Base/MTTFTest.exe','Base/MTTFTest.SafetyAgent.exe','Base/MTTFTest.Watchdog.Protocol.dll',
    'FallbackGuard/MTTFTest.FallbackGuard.exe','FallbackGuard/MTTFTest.Watchdog.Protocol.dll',
    'FallbackGuard/System.Data.SQLite.dll','FallbackGuard/x86/SQLite.Interop.dll','Tools/Manage-IndependentRecovery.ps1')){
    $p=Join-Path $root $name;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))|Out-Null
    [IO.File]::WriteAllText($p,'NOT_EXECUTABLE_TEST_FIXTURE')
    $files+=,[ordered]@{path=$name;sha256=(Get-FileHash $p).Hash}
}
$original=[ordered]@{schemaVersion=2;version='4.1.0.0';recoveryArchitecture='V4-Independent-SystemExecutor';files=$files}|ConvertTo-Json -Depth 4
$passed=0
foreach($scenario in @('valid','traversal','ads','duplicate','digest','missing','architecture','trailing-dot')){
    $manifest=$original|ConvertFrom-Json
    switch($scenario){
        'traversal' {$manifest.files[0].path='Base/../outside.exe'}
        'ads' {$manifest.files[0].path='Base/MTTFTest.exe:extra'}
        'duplicate' {$manifest.files+=,$manifest.files[0]}
        'digest' {$manifest.files[0].sha256=('0'*64)}
        'missing' {$manifest.files=@($manifest.files|Select-Object -Skip 1)}
        'architecture' {$manifest.recoveryArchitecture='V2-Supervisor-SessionAgent-SafetyAgent'}
        'trailing-dot' {$manifest.files[0].path='Base./MTTFTest.exe'}
    }
    [IO.File]::WriteAllText((Join-Path $root 'automatic-bundle.json'),($manifest|ConvertTo-Json -Depth 4))
    $failure=$null;$plan=$null
    try{$plan=Get-IndependentBundlePlan $root (Join-Path $root 'target')}catch{$failure=$_.Exception}
    if($scenario -eq 'valid'){
        if($failure -or $plan.Files.Count -ne 8 -or $plan.Files[0].Relative -ne 'Current/MTTFTest.exe'){throw 'Valid file mapping rejected'}
    }elseif(-not $failure){throw ('Invalid bundle accepted: '+$scenario)}
    $passed++
}
Write-Output ('PASS independent bundle validation '+$passed+'/8; NO INSTALLATION PERFORMED; '+$root)
