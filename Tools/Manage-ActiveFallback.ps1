[CmdletBinding()]
param(
 [ValidateSet('Start','Enable','Disable','Status','Install','Uninstall','Run')][string]$Mode='Status',
 [string]$ProjectDirectory='',
 [string]$SessionId='',
 [string]$InstallRoot='C:\Program Files (x86)\MTTFTest'
)
$ErrorActionPreference='Stop'
if((-not $ProjectDirectory) -ne (-not $SessionId)){throw '项目目录和会话必须同时提供，或同时留空以持续自动绑定。'}
if(-not $ProjectDirectory){
 & (Join-Path $PSScriptRoot 'Manage-PersistentFallback.ps1') -Mode $Mode -InstallRoot $InstallRoot
 exit $LASTEXITCODE
}
# Explicit identities remain available only for isolated acceptance fixtures.
$manager=Join-Path $PSScriptRoot 'Manage-FallbackGuard.ps1'
$arguments=@{ProjectDirectory=$ProjectDirectory;SessionId=$SessionId;BenchActive=$true}
if($Mode -eq 'Start'){
 & $manager -Mode Enable @arguments
 & $manager -Mode Run @arguments
}else{& $manager -Mode $Mode @arguments}