[CmdletBinding()]
param([string]$InstallRoot = '', [string]$ProjectDirectory = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
function Get-EvidenceSourceScan([string]$Source) {
    $scanErrors = @()
    $files = @()
    try {
        if (Test-Path -LiteralPath $Source -ErrorAction Stop) {
            $files = @(Get-ChildItem -LiteralPath $Source -File -Recurse -ErrorAction SilentlyContinue `
                -ErrorVariable +scanErrors | Sort-Object LastWriteTimeUtc -Descending)
        }
        else {
            return [pscustomobject]@{ Files=@(); Errors=@(@{ Source=$Source; Copied=$false;
                Kind='MissingSource'; Reason='SourceMissing' }) }
        }
    } catch { $scanErrors += $_ }
    [pscustomobject]@{ Files=$files; Errors=@($scanErrors | ForEach-Object {
        @{ Source=[string]$_.TargetObject; Copied=$false; Kind='Enumeration'; Reason=$_.Exception.Message }
    }) }
}
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'MTTFTest' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path ([Environment]::GetFolderPath('Desktop')) ('EPB-V217-Evidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw '采证目录已存在，拒绝覆盖。' }
[void](New-Item -ItemType Directory -Path $output)
$sources = @((Join-Path $InstallRoot 'Current\build-identity.json'),
    (Join-Path $env:ProgramData 'MTTFTest'), (Join-Path $env:LOCALAPPDATA 'MTTFTest\WatchdogControlV2'))
if ($ProjectDirectory) { $sources += (Join-Path $ProjectDirectory 'WatchdogSessions'); $sources += (Join-Path $ProjectDirectory 'Logs'); $sources += (Join-Path $ProjectDirectory 'Recovery') }
$index = @(); $count = 0; $budget = 256MB
foreach ($source in $sources) {
    $scan = Get-EvidenceSourceScan $source
    $index += @($scan.Errors)
    foreach ($file in @($scan.Files)) {
        if ($file.FullName.StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
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
try { Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, OSArchitecture, LastBootUpTime | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'machine.json') -Encoding UTF8 }
catch { $index += @{ Source='Win32_OperatingSystem'; Copied=$false; Reason=$_.Exception.Message } }
try { Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'MTTFTest*' } | Select-Object Name, ProcessId, ExecutablePath, CommandLine | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'processes.json') -Encoding UTF8 }
catch { $index += @{ Source='Win32_Process'; Copied=$false; Reason=$_.Exception.Message } }
try { Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=(Get-Date).AddDays(-1); Level=1,2 } -MaxEvents 200 | Select-Object TimeCreated, ProviderName, Id, Message | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'application-events.json') -Encoding UTF8 }
catch {
    $_.Exception.Message | Set-Content -LiteralPath (Join-Path $output 'event-read-status.txt') -Encoding UTF8
    if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') {
        $index += @{ Source='ApplicationEventLog'; Copied=$false; Reason=$_.Exception.Message }
    }
}
$index | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'evidence-index.json') -Encoding UTF8
$problems = @($index | Where-Object { $_.Copied -eq $false }).Count
[ordered]@{ Complete=($problems -eq 0); Copied=@($index | Where-Object { $_.Copied -eq $true }).Count;
    Problems=$problems; Output=$output } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'evidence-summary.json') -Encoding UTF8
Write-Host "采证已导出：$output；未取得/跳过项=$problems。具体原因见 evidence-index.json；权限不足时请用管理员入口补采，原始生产数据未改动。"
