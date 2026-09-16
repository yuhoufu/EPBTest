[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProgramDirectory,
    [Parameter(Mandatory=$true)][string]$ProjectDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$SnapshotPath = ''
)
# Public entry points accept PowerShell 7; .NET Framework deployment work is
# executed by the Windows PowerShell host with typed, data-only arguments.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $epbBridgeParameters = @{}
    foreach ($epbBridgeKey in $PSBoundParameters.Keys) {
        $epbBridgeValue = $PSBoundParameters[$epbBridgeKey]
        if ($epbBridgeValue -is [Management.Automation.SwitchParameter]) { $epbBridgeValue = [bool]$epbBridgeValue }
        $epbBridgeParameters[$epbBridgeKey] = $epbBridgeValue
    }
    $epbBridgeData = @{ Script = $PSCommandPath; Parameters = $epbBridgeParameters } | ConvertTo-Json -Depth 5 -Compress
    $epbBridgePayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($epbBridgeData))
    $epbBridgeCode = '$ErrorActionPreference="Stop";$ProgressPreference="SilentlyContinue";$d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("' + $epbBridgePayload + '"))|ConvertFrom-Json;$p=@{};foreach($v in $d.Parameters.PSObject.Properties){$p[$v.Name]=$v.Value};$global:LASTEXITCODE=0;& ([string]$d.Script) @p;exit $LASTEXITCODE'
    $epbBridgeEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($epbBridgeCode))
    $epbBridgeModulePath = $env:PSModulePath
    try {
        $env:PSModulePath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules"
        & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand $epbBridgeEncoded
        $epbBridgeExitCode = $LASTEXITCODE
    } finally { $env:PSModulePath = $epbBridgeModulePath }
    exit $epbBridgeExitCode
}
$ErrorActionPreference = 'Stop'
try {
    $database = Join-Path ([IO.Path]::GetFullPath($ProjectDirectory)) 'index.db'
    if (-not (Test-Path -LiteralPath $database -PathType Leaf)) { throw '项目 index.db 不存在。' }
    [void][Reflection.Assembly]::LoadFrom((Join-Path $ProgramDirectory 'System.Data.SQLite.dll'))
    $builder = New-Object System.Data.SQLite.SQLiteConnectionStringBuilder
    $builder.DataSource=[string]$database; $builder.ReadOnly=$true; $builder.FailIfMissing=$true; $builder.Pooling=$false; $builder.DefaultTimeout=3
    $source = New-Object System.Data.SQLite.SQLiteConnection($builder.ConnectionString)
    $source.Open()
    try {
        if ($SnapshotPath) {
            if (Test-Path -LiteralPath $SnapshotPath) { throw '拒绝覆盖已有数据库快照。' }
            $destinationBuilder = New-Object System.Data.SQLite.SQLiteConnectionStringBuilder
            $destinationBuilder.DataSource=[IO.Path]::GetFullPath($SnapshotPath); $destinationBuilder.Pooling=$false
            $destination = New-Object System.Data.SQLite.SQLiteConnection($destinationBuilder.ConnectionString)
            try {
                $destination.Open()
                $source.BackupDatabase($destination,'main','main',128,$null,25)
                $check=$destination.CreateCommand()
                try { $check.CommandText='PRAGMA integrity_check'; if ($check.ExecuteScalar() -ne 'ok') { throw '数据库快照完整性校验失败。' } }
                finally { $check.Dispose() }
            } finally { $destination.Dispose() }
        }
        $command=$source.CreateCommand()
        try {
            $command.CommandText="select epb_id, count(*) as committed_count, max(cycle_number) as last_cycle, max(end_time) as last_commit from epb_cycles where status='completed' group by epb_id order by epb_id"
            $command.CommandTimeout=3
            $reader=$command.ExecuteReader()
            $rows=@()
            try { while ($reader.Read()) { $rows += [pscustomobject]@{channel=$reader['epb_id'];committedCount=$reader['committed_count'];lastCycle=$reader['last_cycle'];lastCommit=$reader['last_commit']} } }
            finally { $reader.Dispose() }
        } finally { $command.Dispose() }
        [ordered]@{utc=[DateTime]::UtcNow.ToString('O');database=$database;channels=$rows;actionEvidence='NOT_VERIFIED';continuingProgress='REQUIRES_COMPARISON';snapshot=$SnapshotPath} |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    } finally { $source.Dispose() }
    exit 0
} catch { Write-Error -ErrorAction Continue $_; exit 1 }
