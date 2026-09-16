#requires -Version 5.1
param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'EPB_TEST_ARTIFACT_ROOT required'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root=Join-Path $EvidenceRoot ('host-compatibility-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$scripts=@('Install-AutomaticRecoveryBundle','RecoveryGuard-Acceptance','Get-EpbBusinessEvidence','Install-EPB-UnattendedAlarm','Install-MTTFTest-Unattended','Verify-Release','Complete-IndependentSetup','Export-IndependentRecoveryEvidence','Install-IndependentRecoveryBundle','Manage-IndependentRecovery','Manage-SessionHost','Repair-IndependentInstallerArchitecture')
$bridge=$null
foreach($name in $scripts){
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo ('Tools\'+$name+'.ps1')),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw ('Syntax error: '+$name)}
    $entry=$ast.EndBlock.Statements[0]
    if($entry -isnot [Management.Automation.Language.IfStatementAst] -or $entry.Extent.Text -notmatch 'PSEdition'){throw ('Host bridge missing: '+$name)}
    if($bridge -and $entry.Extent.Text -cne $bridge){throw ('Host bridge differs: '+$name)}
    $bridge=$entry.Extent.Text
}
$fixture=Join-Path $root '宿主 参数验证.ps1'
$body=@'
param([string]$OutputPath,[string]$Value,[switch]$Enabled,[int]$ResultCode=0)
__BRIDGE__
[IO.File]::WriteAllText($OutputPath,(@{edition=$PSVersionTable.PSEdition;value=$Value;enabled=[bool]$Enabled;host64=[Environment]::Is64BitProcess}|ConvertTo-Json))
exit $ResultCode
'@
[IO.File]::WriteAllText($fixture,$body.Replace('__BRIDGE__',$bridge),[Text.UTF8Encoding]::new($true))
$hosts=@("$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe","$env:SystemRoot\SysWOW64\WindowsPowerShell\v1.0\powershell.exe",(Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'))
$value='中文 空格 '' 单引号 " 双引号 $() ` & ;'
$passed=$scripts.Count
foreach($hostPath in $hosts){
    if(-not [IO.File]::Exists($hostPath)){throw ('Required test host missing: '+$hostPath)}
    foreach($enabled in @($true,$false)){
        $output=Join-Path $root ([Guid]::NewGuid().ToString('N')+'.json')
        $payload=@{script=$fixture;output=$output;value=$value;enabled=$enabled}|ConvertTo-Json -Compress
        $data=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
        $code='$ProgressPreference="SilentlyContinue";$d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("'+$data+'"))|ConvertFrom-Json;& $d.script -OutputPath $d.output -Value $d.value -Enabled:([bool]$d.enabled) -ResultCode 17;exit $LASTEXITCODE'
        & $hostPath -NoProfile -ExecutionPolicy Bypass -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code)))
        if($LASTEXITCODE -ne 17){throw 'Child exit code was lost'}
        $actual=[IO.File]::ReadAllText($output)|ConvertFrom-Json
        if($actual.edition -ne 'Desktop' -or $actual.value -cne $value -or $actual.enabled -ne $enabled){throw 'Typed arguments were corrupted'}
        $passed++
    }
}
[ordered]@{passed=$passed;hostCount=$hosts.Count;scriptCount=$scripts.Count;artifactRoot=$root}|ConvertTo-Json
