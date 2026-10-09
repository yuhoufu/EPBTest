$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$path=Join-Path $PSScriptRoot '..\..\Tools\New-AutomaticRecoveryBundle.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors|Out-String)}
$statement=$ast.Find({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$manifest'},$true)
if(-not $statement){throw 'Manifest production expression missing'}
$version='4.1.0.0';$commit='a'*40;$files=@()
$fallbackOutput=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\FallbackGuard\bin\Debug'))
if(-not [IO.File]::Exists((Join-Path $fallbackOutput 'MTTFTest.FallbackGuard.exe'))){throw 'Build Debug first'}
foreach($LegacyRecovery in @($false,$true)){
    $manifest=& ([scriptblock]::Create('$PSScriptRoot=$args[0]' + "`r`n" + $statement.Right.Extent.Text)) ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)))
    $expectedArchitecture=if($LegacyRecovery){'V2-Supervisor-SessionAgent-SafetyAgent'}else{'V4-Independent-SystemExecutor'}
    $expectedSchema=if($LegacyRecovery){1}else{2}
    $expectedDefault=if($LegacyRecovery){'ObservationOnly'}else{'RequiresDurableRunIntent'}
    if($manifest.schemaVersion -ne $expectedSchema -or $manifest.recoveryArchitecture -ne $expectedArchitecture -or $manifest.fallbackDefault -ne $expectedDefault){throw 'Wrong package authority mode'}
    if($manifest.fieldDeploymentApproved){throw 'Packaging authorized field deployment'}
}
Write-Output 'PASS production bundle architecture 2/2; NO ARCHIVE GENERATED'
