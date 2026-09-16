param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'Evidence root required'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root=Join-Path $EvidenceRoot ('top-upgrade-'+[Guid]::NewGuid().ToString('N'))
$bundle=Join-Path $root ('bundle space '+[char]0x5347+[char]0x7EA7)
[IO.Directory]::CreateDirectory($bundle)|Out-Null
$stub=@'
param([string]$Mode,[string]$BundleDirectory,[string]$InstallRoot,[string]$PreviousBundleDirectory,[string]$TransactionId)
if($Mode -eq 'Validate'){return}
if($Mode -ne 'ValidateUpgrade'){throw 'Unexpected mutating mode in routing fixture'}
[IO.File]::WriteAllText($env:EPB_ROUTE_LOG,([ordered]@{mode=$Mode;bundle=$BundleDirectory;install=$InstallRoot;previous=$PreviousBundleDirectory;transaction=$TransactionId}|ConvertTo-Json -Depth 2))
'@
$entries=@()
foreach($relative in @('Base/MTTFTest.exe','Base/Deployment/Install-MTTFTest-Unattended.ps1','RecoveryGuard-Acceptance.ps1','Tools/Export-IndependentRecoveryEvidence.ps1','Tools/Install-IndependentRecoveryBundle.ps1','Tools/Independent-MaintenanceContext.ps1','Install-AutomaticRecoveryBundle.ps1')){
    $path=Join-Path $bundle $relative;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null
    if($relative -eq 'Install-AutomaticRecoveryBundle.ps1'){[IO.File]::Copy((Join-Path $repo 'Tools\Install-AutomaticRecoveryBundle.ps1'),$path,$false)}
    elseif($relative -eq 'Tools/Independent-MaintenanceContext.ps1'){[IO.File]::Copy((Join-Path $repo $relative),$path,$false)}
    elseif($relative -eq 'Tools/Install-IndependentRecoveryBundle.ps1'){[IO.File]::WriteAllText($path,$stub)}
    else{[IO.File]::WriteAllText($path,'NONEXECUTABLE_ROUTING_FIXTURE')}
    $entries+=,[ordered]@{path=$relative;sha256=(Get-FileHash $path).Hash}
}
[IO.File]::WriteAllText((Join-Path $bundle 'automatic-bundle.json'),([ordered]@{schemaVersion=2;version='4.1.0.0';recoveryArchitecture='V4-Independent-SystemExecutor';files=$entries}|ConvertTo-Json -Depth 4))
$env:EPB_ROUTE_LOG=Join-Path $root 'route.json'
$previous=Join-Path $root 'previous bundle';$install=Join-Path $root 'installation space'
$ps=Join-Path $env:SystemRoot 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
try{
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bundle 'Install-AutomaticRecoveryBundle.ps1') -Mode ValidateUpgrade -InstallRoot $install -PreviousBundleDirectory $previous
    if($LASTEXITCODE -ne 0){throw 'Read-only upgrade routing failed'}
    $record=[IO.File]::ReadAllText($env:EPB_ROUTE_LOG)|ConvertFrom-Json
    if($record.mode -ne 'ValidateUpgrade' -or $record.bundle -ne $bundle -or $record.install -ne $install -or $record.previous -ne $previous){throw 'Argument forwarding changed paths'}
    $before=(Get-FileHash $env:EPB_ROUTE_LOG).Hash
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bundle 'Install-AutomaticRecoveryBundle.ps1') -Mode ValidateUpgrade -InstallRoot $install
    if($LASTEXITCODE -eq 0 -or (Get-FileHash $env:EPB_ROUTE_LOG).Hash -ne $before){throw 'Missing old bundle reached inner installer'}
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bundle 'Install-AutomaticRecoveryBundle.ps1') -Mode RecoverFiles -InstallRoot $install -TransactionId invalid
    if($LASTEXITCODE -eq 0 -or (Get-FileHash $env:EPB_ROUTE_LOG).Hash -ne $before){throw 'Invalid recovery ID reached elevation or mutation'}
}finally{Remove-Item Env:EPB_ROUTE_LOG -ErrorAction SilentlyContinue}
Write-Output ('PASS top upgrade routing and pre-elevation argument rejection 3/3; no installation; '+$root)
