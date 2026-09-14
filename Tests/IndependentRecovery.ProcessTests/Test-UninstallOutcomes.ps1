param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'Evidence root required'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
$definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-IndependentUninstallSteps'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$root=Join-Path $EvidenceRoot ('uninstall-outcomes-'+[Guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($root)|Out-Null
$id=[Guid]::NewGuid().ToString('N');$journal=Join-Path $root 'uninstall-result.json'
Invoke-IndependentUninstallSteps $root '4.1.0.0' $id {} {}
$success=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
if($success.stage -ne 'ComponentsRemoved'){throw 'Initial completion absent'}
$removeCalled=[Collections.Generic.List[bool]]::new()
$failed=$false
try{Invoke-IndependentUninstallSteps $root '4.1.0.0' $id {throw 'INJECTED_UNREGISTER_FAILURE'} {$removeCalled.Add($true)}}catch{$failed=$true}
$current=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
if(-not $failed -or $current.stage -ne 'Failed' -or $current.requestId -eq $success.requestId -or $removeCalled.Count -ne 0 -or $current.failure -notlike '*INJECTED_UNREGISTER_FAILURE*'){throw 'Previous success hid new unregister failure'}
$failed=$false
try{Invoke-IndependentUninstallSteps $root '4.1.0.0' $id {} {
 $phase=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
 if($phase.stage -ne 'RemovingComponents'){throw 'Removal phase was not persisted before work'}
 throw 'INJECTED_FILE_REMOVAL_FAILURE'
}}catch{$failed=$true}
$current=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
if(-not $failed -or $current.stage -ne 'Failed' -or $current.failure -notlike '*INJECTED_FILE_REMOVAL_FAILURE*'){throw 'File failure misreported'}
Invoke-IndependentUninstallSteps $root '4.1.0.0' $id {} {}
$current=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
if($current.stage -ne 'ComponentsRemoved' -or $current.failure){throw 'Explicit retry did not finish'}
Write-Output ('PASS uninstall outcome failure injection and explicit retry; '+$root)
