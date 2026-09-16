#requires -Version 5.1
param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'EPB_TEST_ARTIFACT_ROOT required'}
$output=Join-Path $EvidenceRoot ('command-resolution-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($output)|Out-Null
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\New-AutomaticRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Packager syntax invalid'}
$assignment=$ast.Find({param($n)$n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$commands'},$true)
$extra=$ast.Find({param($n)$n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.Contains("`$commands['检查升级条件.cmd']")},$true)
$writer=$ast.Find({param($n)$n -is [Management.Automation.Language.ForEachStatementAst] -and $n.Condition.Extent.Text -eq '$commands.GetEnumerator()'},$true)
if(-not $assignment -or -not $extra -or -not $writer){throw 'Actual package command generator not found'}
$LegacyRecovery=$false;$utf8=[Text.UTF8Encoding]::new($true)
. ([scriptblock]::Create($assignment.Extent.Text))
. ([scriptblock]::Create($extra.Extent.Text))
. ([scriptblock]::Create($writer.Extent.Text))
$stub=@'
param([string]$Mode)
Get-FileHash -LiteralPath $PSCommandPath -ErrorAction Stop | Out-Null
[IO.File]::WriteAllText($env:EPB_COMMAND_TEST_RESULT,$Mode)
exit 17
'@
[IO.File]::WriteAllText((Join-Path $output 'Install-AutomaticRecoveryBundle.ps1'),$stub,$utf8)
$savedPause=$env:EPB_BUNDLE_NONINTERACTIVE;$savedResult=$env:EPB_COMMAND_TEST_RESULT
$savedModulePath=$env:PSModulePath
$passed=0
try{
 $env:EPB_BUNDLE_NONINTERACTIVE='1';$env:EPB_COMMAND_TEST_RESULT=Join-Path $output 'mode.txt'
 # Reproduce the actual PS7 -> cmd -> Windows PowerShell module-path failure.
 # Core syntax can invoke the cmd even with this inherited module environment;
 # the delivered cmd must select the 5.1 built-in modules for its child.
 $env:PSModulePath=Join-Path $env:ProgramFiles 'PowerShell\7\Modules'
 foreach($hostPath in @("$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe",(Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'))){
  if(-not [IO.File]::Exists($hostPath)){throw 'Required PowerShell host missing'}
  foreach($entry in $commands.GetEnumerator()){
   $stem=Join-Path $output ([IO.Path]::GetFileNameWithoutExtension($entry.Key))
   $command='& '''+$stem.Replace("'","''")+''';exit $LASTEXITCODE'
   & $hostPath -NoProfile -ExecutionPolicy Restricted -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))) *> (Join-Path $output 'last-command.log')
   if($LASTEXITCODE -ne 17 -or [IO.File]::ReadAllText($env:EPB_COMMAND_TEST_RESULT) -ne $entry.Value){throw ('Extensionless command or exit code failed: '+$entry.Key+' in '+$hostPath)}
   $passed++
  }
 }
}finally{$env:EPB_BUNDLE_NONINTERACTIVE=$savedPause;$env:EPB_COMMAND_TEST_RESULT=$savedResult;$env:PSModulePath=$savedModulePath}
if($passed -ne 26){throw 'Expected all 13 public commands in both hosts'}
Write-Output ('PASS one-click command resolution '+$passed+' checks; '+$output)
