#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$identity=Get-Content (Join-Path $root 'bundle-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$recoveryMode=if($identity.schemaVersion -eq 1){'ObserveOnly'}else{[string]$identity.recoveryMode}
if($identity.schemaVersion -notin @(1,2) -or $recoveryMode -notin @('ObserveOnly','RecoverExited') -or
    $identity.automaticRecoveryEnabled -isnot [bool] -or
    $identity.automaticRecoveryEnabled -ne ($recoveryMode -eq 'RecoverExited')){throw '完整包恢复模式声明无效。'}
$names=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $identity.files){
    $name=[string]$entry.name
    $path=[IO.Path]::GetFullPath((Join-Path $root $name))
    if([IO.Path]::IsPathRooted($name) -or $name -match '(^|[\\/])\.\.([\\/]|$)' -or -not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $names.Add($name.Replace('\','/'))){throw '包清单路径无效或重复。'}
    if(-not(Test-Path $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path).Hash -ine $entry.sha256){throw "文件缺失或损坏：$name"}
}
$actual=@(Get-ChildItem $root -Recurse -File | Where-Object FullName -ne (Join-Path $root 'bundle-identity.json'))
if($actual.Count -ne $names.Count){throw '包中存在清单外文件。'}
foreach($file in $actual){if(-not $names.Contains($file.FullName.Substring($root.Length+1).Replace('\','/'))){throw '包文件集合不一致。'}}
& (Join-Path $root 'Package\Deployment\Verify-Release.ps1') -ReleaseDirectory (Join-Path $root 'Package') | Out-Null
& (Join-Path $root 'Guard\Install-MTTFTest-RecoveryGuard.ps1') -Mode Validate -SourceDirectory (Join-Path $root 'Guard') -RecoveryMode $recoveryMode | Out-Null
$guardIdentity=Get-Content (Join-Path $root 'Guard\guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($recoveryMode -eq 'RecoverExited' -and
    (Get-FileHash (Join-Path $root 'Package\build-identity.json')).Hash -ine $guardIdentity.mainIdentitySha256){throw '自动恢复验收不匹配完整包Main。'}
if((Get-FileHash (Join-Path $root 'Package\MTTFTest.RecoveryControl.dll')).Hash -ne (Get-FileHash (Join-Path $root 'Guard\MTTFTest.RecoveryControl.dll')).Hash){throw 'Main与Guard共享组件不一致。'}
Write-Host "完整包校验通过：$($identity.productVersion)，Guard模式：$recoveryMode。"
