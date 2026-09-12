[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProgramDirectory,
    [Parameter(Mandatory=$true)][string]$ProjectDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$SnapshotPath = ''
)
$ErrorActionPreference = 'Stop'
try {
    $database = Join-Path ([IO.Path]::GetFullPath($ProjectDirectory)) 'index.db'
    if (-not (Test-Path -LiteralPath $database -PathType Leaf)) { throw '项目 index.db 不存在。' }
    [void][Reflection.Assembly]::LoadFrom((Join-Path $ProgramDirectory 'System.Data.SQLite.dll'))
    $builder = New-Object System.Data.SQLite.SQLiteConnectionStringBuilder
    $builder.DataSource=$database; $builder.ReadOnly=$true; $builder.FailIfMissing=$true; $builder.Pooling=$false; $builder.DefaultTimeout=3
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
