function Get-PersistentFallbackPaths([string]$InstallRoot){
 $legacy=Get-FallbackBindingPath $InstallRoot
 $token=[IO.Path]::GetFileNameWithoutExtension($legacy)
 return [pscustomobject]@{Token=$token;Settings=$legacy+'.monitor.json';Binding=$legacy+'.running.json';Status=$legacy+'.status.json';Legacy=$legacy;Task=('MTTFTest-FallbackMonitor-'+$token)}
}
function Get-ConfiguredFallbackProject([string]$InstallRoot,[string]$SelectionPath=(Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Wanxiang\EPBTest\user-state.json')){
 if([IO.File]::Exists($SelectionPath)){
  $selection=Read-FallbackBindingJson $SelectionPath
  if($selection.schemaVersion -ne 1 -or -not $selection.storeDir -or -not $selection.testName){throw '最后项目配置无效'}
  if([IO.Path]::IsPathRooted($selection.testName) -or $selection.testName -match '[\\/]' -or $selection.testName -in @('.','..')){throw '项目名称无效'}
  $root=[IO.Path]::GetFullPath((Join-Path $selection.storeDir $selection.testName))
 }else{
  $config=Join-Path $InstallRoot 'Current\MTTFTest.exe.config'
  if((Get-Item -LiteralPath $config).Length -gt 65536){throw '程序配置超过64KiB'}
  $xml=New-Object Xml.XmlDocument;$xml.XmlResolver=$null
  $xml.LoadXml([IO.File]::ReadAllText($config))
  $node=$xml.SelectSingleNode('/configuration/appSettings/add[@key="InitialProjectPath"]')
  if(-not $node -or -not $node.value){throw '程序尚未保存项目，请在主程序中选择并保存项目'}
  $root=[IO.Path]::GetFullPath([string]$node.value)
 }
 if(-not [IO.File]::Exists((Join-Path $root 'Config\TestConfig.xml'))){throw '配置指向的项目不可用，等待主程序保存有效项目'}
 return Join-Path $root 'WatchdogSessions'
}
function Resolve-ConfiguredFallbackBinding([string]$InstallRoot){
 $directory=Get-ConfiguredFallbackProject $InstallRoot
 $binding=Resolve-FallbackBinding $InstallRoot
 if($binding.ProjectDirectory.TrimEnd('\') -ne $directory.TrimEnd('\')){throw '项目配置与运行会话不一致，等待配置保存及会话就绪'}
 return $binding
}
function Get-PersistentGuardExecutable {return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\FallbackGuard\MTTFTest.FallbackGuard.exe'))}
function Invoke-PersistentGuard([string]$Action,$Binding){
 $exe=Get-PersistentGuardExecutable
 $arguments=@($Action,'--project-directory',$Binding.ProjectDirectory,'--session-id',$Binding.SessionId)
 if($Action -eq '--enable'){$arguments+='--active'}
 & $exe @arguments | Out-Null
 if($LASTEXITCODE -ne 0){throw ('独立协议命令失败：'+$Action)}
}
function Get-BoundGuardProcesses($Binding){
 $exe=Get-PersistentGuardExecutable
 return @(Get-CimInstance Win32_Process -Filter "Name='MTTFTest.FallbackGuard.exe'" -OperationTimeoutSec 3 | Where-Object {
  (Read-FallbackArgument $_.CommandLine '--session-id') -eq $Binding.SessionId -and (Read-FallbackArgument $_.CommandLine '--project-directory') -eq $Binding.ProjectDirectory -and $_.CommandLine -match '(?:^|\s)--run(?:\s|$)'
 })
}
function Test-FallbackDrained($Binding){
 if(@(Get-BoundGuardProcesses $Binding).Count -ne 0){return $false}
 $ledger=Read-FallbackBindingJson (Join-Path $Binding.ProjectDirectory ('fallback-'+$Binding.SessionId+'.json'))
 return ($ledger.SchemaVersion -eq 1 -and $ledger.SessionId -eq $Binding.SessionId -and $ledger.Owner -eq 'Original' -and $ledger.Phase -eq 'Idle' -and -not $ledger.OutstandingLaunch)
}
