$ErrorActionPreference='Stop'
$path=(Join-Path $PSScriptRoot '..\..\Tools\Manage-IndependentRecovery.ps1')
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors|Out-String)}
$function=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-IndependentProvision'},$true)
if(-not $function){throw 'Production orchestration function missing'}
. ([scriptblock]::Create($function.Extent.Text))
$passed=0
foreach($scenario in @('success','Prepare','Install','Enable','cleanup')){
    $calls=[Collections.Generic.List[string]]::new()
    $failure=$null
    try{
        Invoke-IndependentProvision {
            param($phase)
            $calls.Add($phase)
            if($phase -eq $scenario -or ($scenario -eq 'cleanup' -and $phase -in @('Enable','Uninstall'))){throw ('INJECTED:'+ $phase)}
        }
    }catch{$failure=$_.Exception}
    $expected=switch($scenario){
        'success' {'Prepare,Install,Enable'}
        'Prepare' {'Prepare'}
        'Install' {'Prepare,Install'}
        default {'Prepare,Install,Enable,Uninstall'}
    }
    if(($calls -join ',') -ne $expected){throw ('Wrong ownership cleanup: '+$scenario)}
    if(($scenario -eq 'success') -ne ($null -eq $failure)){throw ('Wrong outcome: '+$scenario)}
    if($scenario -eq 'cleanup' -and ($failure -isnot [AggregateException] -or $failure.InnerExceptions.Count -ne 2)){throw 'Both failures must remain inspectable'}
    $passed++
}
Write-Output ('PASS production provision orchestration '+$passed+'/5; NO SERVICES OR TASKS CREATED')
