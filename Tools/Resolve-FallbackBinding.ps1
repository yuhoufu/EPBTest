function Read-FallbackBindingJson([string]$Path){
 if((Get-Item -LiteralPath $Path).Length -gt 65536){throw '绑定/快照超过64KiB。'}
 $value=[IO.File]::ReadAllText($Path,[Text.Encoding]::UTF8) | ConvertFrom-Json
 if($null -eq $value){throw '绑定/快照为空。'}
 return $value
}
function Get-FallbackBindingPath([string]$InstallRoot){
 $sha=[Security.Cryptography.SHA256]::Create()
 try{$token=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($InstallRoot).ToUpperInvariant())))).Replace('-','').Substring(0,20)}finally{$sha.Dispose()}
 return Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('MTTFTest\FallbackBindings\'+$token+'.json')
}
function Save-FallbackBinding([string]$Path,$Value){
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
 $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
 try{
  [IO.File]::WriteAllText($temporary,($Value | ConvertTo-Json -Depth 3),(New-Object Text.UTF8Encoding($false)))
  if([IO.File]::Exists($Path)){[IO.File]::Replace($temporary,$Path,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temporary,$Path)}
 }finally{if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}}
}
function Read-FallbackArgument([string]$Line,[string]$Name){
 $m=[regex]::Match($Line,'(?:^|\s)'+[regex]::Escape($Name)+'\s+(?:"([^"]+)"|(\S+))')
 if(-not $m.Success){return $null}
 if($m.Groups[1].Success){return $m.Groups[1].Value}
 return $m.Groups[2].Value
}
function Resolve-FallbackBinding([string]$InstallRoot){
 $expected=[IO.Path]::GetFullPath((Join-Path $InstallRoot 'Current\MTTFTest.Watchdog.exe'))
 $sidecars=@(Get-CimInstance Win32_Process -Filter "Name='MTTFTest.Watchdog.exe'" -OperationTimeoutSec 3 | Where-Object {$_.ExecutablePath -eq $expected -and $_.CommandLine -match '(?:^|\s)--session-host(?:\s|$)' -and (Read-FallbackArgument ([string]$_.CommandLine) '--journal-directory') -and (Read-FallbackArgument ([string]$_.CommandLine) '--session')})
 if($sidecars.Count -ne 1){throw ('找到 '+$sidecars.Count+' 个当前侧车，请先启动已安装主程序或核对多开。')}
 $sidecar=$sidecars[0]
 $directory=Read-FallbackArgument ([string]$sidecar.CommandLine) '--journal-directory'
 $session=Read-FallbackArgument ([string]$sidecar.CommandLine) '--session'
 $id=[Guid]::Empty
 if(-not $directory -or -not [Guid]::TryParseExact($session,'N',[ref]$id)){throw '侧车命令行身份无效。'}
 $directory=[IO.Path]::GetFullPath($directory)
 $snapshot=Read-FallbackBindingJson (Join-Path $directory ('fallback-observation-'+$session+'.json'))
 $ledger=Read-FallbackBindingJson (Join-Path $directory ('fallback-'+$session+'.json'))
 $age=([DateTime]::UtcNow-[DateTime]::Parse($snapshot.CapturedUtc).ToUniversalTime()).TotalSeconds
 # CIM CreationDate is microsecond precision; the journal uses GetProcessTimes (100ns).
 # Accept only truncation inside that same microsecond, never another PID/start identity.
 $startDelta=[long]$snapshot.ProcessStartUtcTicks-$sidecar.CreationDate.ToUniversalTime().Ticks
 if($snapshot.SchemaVersion -ne 1 -or $ledger.SchemaVersion -ne 1 -or $snapshot.SessionId -ne $session -or $ledger.SessionId -ne $session -or $snapshot.ProcessId -ne $sidecar.ProcessId -or $startDelta -lt 0 -or $startDelta -ge 10 -or $age -lt -5 -or $age -gt 10 -or -not $snapshot.Heartbeat.RunId -or $ledger.RunId -ne $snapshot.Heartbeat.RunId){throw '当前快照未就绪、过期或进程/Run身份不匹配，拒绝猜测。'}
 return [pscustomobject]@{ProjectDirectory=$directory;SessionId=$session}
}
