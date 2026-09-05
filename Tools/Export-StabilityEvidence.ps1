[CmdletBinding()]
param([string]$InstallRoot = '', [string]$ProjectDirectory = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'MTTFTest' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path ([Environment]::GetFolderPath('Desktop')) ('EPB-V216-Evidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw '采证目录已存在，拒绝覆盖。' }
[void](New-Item -ItemType Directory -Path $output)
$sources = @((Join-Path $InstallRoot 'Current\build-identity.json'),
    (Join-Path $env:ProgramData 'MTTFTest'), (Join-Path $env:LOCALAPPDATA 'MTTFTest\WatchdogControlV2'))
if ($ProjectDirectory) { $sources += (Join-Path $ProjectDirectory 'WatchdogSessions'); $sources += (Join-Path $ProjectDirectory 'Logs'); $sources += (Join-Path $ProjectDirectory 'Recovery') }
$index = @(); $count = 0; $budget = 256MB
foreach ($source in $sources) {
    if (-not (Test-Path -LiteralPath $source)) { continue }
    foreach ($file in @(Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object LastWriteTimeUtc -Descending)) {
        if ($file.Length -gt 20MB -or $file.Length -gt $budget) {
            $index += @{ Source=$file.FullName; Copied=$false; Reason='SizeBudget'; Bytes=$file.Length }; continue
        }
        $count++; $name = ('{0:D5}-' -f $count) + $file.Name
        try {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $output $name)
            $budget -= $file.Length
            $index += @{ Source=$file.FullName; Copied=$true; File=$name; SHA256=(Get-FileHash -LiteralPath (Join-Path $output $name)).Hash }
        } catch { $index += @{ Source=$file.FullName; Copied=$false; Reason=$_.Exception.Message } }
    }
}
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, OSArchitecture, LastBootUpTime | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'machine.json') -Encoding UTF8
Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'MTTFTest*' } | Select-Object Name, ProcessId, ExecutablePath, CommandLine | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'processes.json') -Encoding UTF8
try { Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=(Get-Date).AddDays(-1); Level=1,2 } -MaxEvents 200 | Select-Object TimeCreated, ProviderName, Id, Message | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'application-events.json') -Encoding UTF8 } catch { $_.Exception.Message | Set-Content -LiteralPath (Join-Path $output 'event-read-status.txt') }
$index | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'evidence-index.json') -Encoding UTF8
Write-Host "采证完成：$output。大文件和读取失败项见 evidence-index.json；原始生产数据未改动。"
