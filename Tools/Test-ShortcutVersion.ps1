param([Parameter(Mandatory=$true)][string]$OutputDirectory,
      [Parameter(Mandatory=$true)][string]$ExecutablePath)
$ErrorActionPreference='Stop'
# Load only shortcut functions: never execute the installer or touch real desktop links.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
foreach($name in @('Get-ShortcutPaths','Install-Shortcuts','Remove-Shortcuts')){
 $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
 if(-not $node){throw ('Missing production function '+$name)}
 Invoke-Expression $node.Extent.Text
}
$root=[IO.Path]::GetFullPath($OutputDirectory)
$script:shortcutTestFolders=@((Join-Path $root 'desktop'),(Join-Path $root 'programs'))
function Get-ShortcutFolders { $script:shortcutTestFolders }
$current=Join-Path $root 'Current'
[IO.Directory]::CreateDirectory($current) | Out-Null
Copy-Item -LiteralPath $ExecutablePath -Destination (Join-Path $current 'MTTFTest.exe')
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath).FileVersion
$shell=New-Object -ComObject WScript.Shell
foreach($folder in $script:shortcutTestFolders){
 [IO.Directory]::CreateDirectory($folder) | Out-Null
 foreach($name in @('MT EPB 试验系统 V2.14.lnk','MT EPB 试验系统 V0.0.0.0.lnk')){
  $link=$shell.CreateShortcut((Join-Path $folder $name))
  $link.TargetPath=if($name -like '*V2.14.lnk'){Join-Path $current 'MTTFTest.exe'}else{Join-Path $root 'other\MTTFTest.exe'}
  $link.Save()
 }
}
Install-Shortcuts $root
Install-Shortcuts $root
foreach($folder in $script:shortcutTestFolders){
 $path=Join-Path $folder ('MT EPB 试验系统 V'+$version+'.lnk')
 if(-not [IO.File]::Exists($path)){throw 'Actual EXE version missing from link name'}
 if($shell.CreateShortcut($path).TargetPath -ne (Join-Path $current 'MTTFTest.exe')){throw 'Wrong target'}
 if(([IO.File]::ReadAllBytes($path)[21] -band 0x20) -eq 0){throw 'Run-as flag missing'}
 if(Test-Path (Join-Path $folder 'MT EPB 试验系统 V2.14.lnk')){throw 'Legacy link remains'}
 if(-not(Test-Path (Join-Path $folder 'MT EPB 试验系统 V0.0.0.0.lnk'))){throw 'Other installation link removed'}
}
Remove-Shortcuts $root
foreach($folder in $script:shortcutTestFolders){
 if(@(Get-ChildItem $folder -Filter '*.lnk').Count -ne 1){throw 'Uninstall removed wrong links'}
}
'PASS shortcut install/repair/version/legacy cleanup/ownership/uninstall'
