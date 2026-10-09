param(
    [string]$SQLiteAssemblyPath = "",
    [string]$SevenZip = "C:\Program Files\7-Zip\7z.exe"
)

$ErrorActionPreference = "Stop"

$RepositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\..")
)
$BackupScript = Join-Path $RepositoryRoot "Tools\EPB-IncrementalBackup_new.ps1"
$ApplicationExecutable = Join-Path $RepositoryRoot "MTTfTest\bin\Release\MTTFTest.exe"

if ([string]::IsNullOrWhiteSpace($SQLiteAssemblyPath)) {
    $SQLiteAssemblyPath = Join-Path $RepositoryRoot `
        "MTTfTest\bin\Release\System.Data.SQLite.dll"
}

if (-not (Test-Path -LiteralPath $BackupScript -PathType Leaf)) {
    throw "未找到待测备份脚本：$BackupScript"
}
if (-not (Test-Path -LiteralPath $ApplicationExecutable -PathType Leaf)) {
    throw "未找到用于自动识别版本的 MTTFTest.exe：$ApplicationExecutable"
}
if (-not (Test-Path -LiteralPath $SQLiteAssemblyPath -PathType Leaf)) {
    throw "未找到 System.Data.SQLite.dll：$SQLiteAssemblyPath"
}
if (-not (Test-Path -LiteralPath $SevenZip -PathType Leaf)) {
    throw "未找到 7-Zip：$SevenZip"
}

$TestRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ("epb-incremental-backup-test-" + [Guid]::NewGuid().ToString("N"))
$ResolvedTestRoot = [IO.Path]::GetFullPath($TestRoot)
$ResolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'

if (-not $ResolvedTestRoot.StartsWith(
    $ResolvedTempRoot,
    [StringComparison]::OrdinalIgnoreCase
)) {
    throw "临时测试目录超出系统临时目录：$ResolvedTestRoot"
}

$Writer = $null
$WriterCommand = $null
$Reader = $null
$ReaderCommand = $null

try {
    $SourceDir = Join-Path $ResolvedTestRoot "source"
    $OutputDir = Join-Path $ResolvedTestRoot "output"
    $WorkRoot = Join-Path $ResolvedTestRoot "work"
    $ExtractDir = Join-Path $ResolvedTestRoot "extract"
    New-Item -ItemType Directory -Force `
        -Path $SourceDir, $OutputDir, $WorkRoot, $ExtractDir |
        Out-Null

    $ProjectConfigDir = Join-Path $SourceDir "Config"
    New-Item -ItemType Directory -Force -Path $ProjectConfigDir | Out-Null
    $ExeVersionInfo = (Get-Item -LiteralPath $ApplicationExecutable).VersionInfo
    $ProjectVersion = "V" + [string]$ExeVersionInfo.ProductVersion
    $ProjectIdentityPath = Join-Path $ProjectConfigDir "runtime-build-identity.json"
    [ordered]@{
        schemaVersion = 1
        projectName = "source"
        projectRoot = $SourceDir
        productVersion = $ProjectVersion
        assemblyVersion = [string]$ExeVersionInfo.FileVersion
        executablePath = $ApplicationExecutable
        executableSha256 = (Get-FileHash -LiteralPath $ApplicationExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
        capturedUtc = [DateTime]::UtcNow.ToString("o")
    } | ConvertTo-Json | Set-Content -LiteralPath $ProjectIdentityPath -Encoding UTF8

    # 合法 CSV 可以没有末尾换行，不能因此中止整轮备份。
    [IO.File]::WriteAllText(
        (Join-Path $SourceDir "sealed-without-newline.csv"),
        "cycle,value`r`n1,ok",
        [Text.Encoding]::UTF8
    )

    $SQLiteAssemblyPath = [IO.Path]::GetFullPath($SQLiteAssemblyPath)
    $SQLiteDirectory = Split-Path $SQLiteAssemblyPath -Parent
    $Architecture = if ([Environment]::Is64BitProcess) { "x64" } else { "x86" }
    $env:Path = (Join-Path $SQLiteDirectory $Architecture) + ";" +
        $SQLiteDirectory + ";" + $env:Path
    Add-Type -Path $SQLiteAssemblyPath

    $DatabasePath = Join-Path $SourceDir "index.db"
    $Builder = New-Object System.Data.SQLite.SQLiteConnectionStringBuilder
    $Builder.DataSource = [string]$DatabasePath
    $Builder.Pooling = $false
    $Writer = New-Object System.Data.SQLite.SQLiteConnection
    $Writer.ConnectionString = $Builder.ConnectionString
    $Writer.Open()

    $WriterCommand = $Writer.CreateCommand()
    $WriterCommand.CommandText = "PRAGMA journal_mode=WAL;"
    [void]$WriterCommand.ExecuteScalar()
    $WriterCommand.CommandText =
        "CREATE TABLE cycles(id INTEGER PRIMARY KEY, value TEXT NOT NULL);"
    [void]$WriterCommand.ExecuteNonQuery()
    $WriterCommand.CommandText =
        "INSERT INTO cycles(value) VALUES ('first');"
    [void]$WriterCommand.ExecuteNonQuery()

    # 模拟旧脚本已经留下 CSV 状态、但尚不存在备份链状态的现场升级场景。
    $LegacyStateDir = Join-Path $WorkRoot "State"
    New-Item -ItemType Directory -Force -Path $LegacyStateDir | Out-Null
    [PSCustomObject]@{
        RelativePath = "legacy-removed.txt"
        Length = 1
        LastWriteTimeUtcTicks = 1
    } |
        Export-Csv `
            -LiteralPath (Join-Path $LegacyStateDir "source.csv") `
            -NoTypeInformation `
            -Encoding UTF8

    $WalPath = "$DatabasePath-wal"
    $MainBefore = Get-Item -LiteralPath $DatabasePath
    $MainLengthBefore = $MainBefore.Length
    $MainTimeBefore = $MainBefore.LastWriteTimeUtc.Ticks
    $WalBefore = Get-Item -LiteralPath $WalPath
    $WalLengthBefore = $WalBefore.Length
    $WalTimeBefore = $WalBefore.LastWriteTimeUtc.Ticks

    $WindowsPowerShell = Join-Path $env:SystemRoot `
        "System32\WindowsPowerShell\v1.0\powershell.exe"
    $CommonArguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $BackupScript,
        "-ApplicationExecutable", $ApplicationExecutable,
        "-SourceDir", $SourceDir,
        "-OutputDir", $OutputDir,
        "-WorkRoot", $WorkRoot,
        "-SevenZip", $SevenZip,
        "-SQLiteAssemblyPath", $SQLiteAssemblyPath
    )

    $FirstOutput = & $WindowsPowerShell @CommonArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "第一轮备份失败：`n$($FirstOutput -join "`n")"
    }

    Start-Sleep -Milliseconds 1100
    $WriterCommand.CommandText =
        "INSERT INTO cycles(value) VALUES ('second-only-in-wal');"
    [void]$WriterCommand.ExecuteNonQuery()

    $MainAfter = Get-Item -LiteralPath $DatabasePath
    $WalAfter = Get-Item -LiteralPath $WalPath
    $MainMetadataChanged =
        $MainAfter.Length -ne $MainLengthBefore -or
        $MainAfter.LastWriteTimeUtc.Ticks -ne $MainTimeBefore
    $WalMetadataChanged =
        $WalAfter.Length -ne $WalLengthBefore -or
        $WalAfter.LastWriteTimeUtc.Ticks -ne $WalTimeBefore

    if ($MainMetadataChanged) {
        throw "测试前提不成立：第二次提交改变了主库元数据。"
    }
    if (-not $WalMetadataChanged) {
        throw "测试前提不成立：第二次提交没有改变 WAL 元数据。"
    }

    $SecondOutput = & $WindowsPowerShell @CommonArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "第二轮备份失败：`n$($SecondOutput -join "`n")"
    }

    # 仅 SHM 被读取或更新时间变化时，不应持续制造无意义增量包。
    $ThirdOutput = & $WindowsPowerShell @CommonArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "无变化复查失败：`n$($ThirdOutput -join "`n")"
    }

    $Archives = @(
        Get-ChildItem -LiteralPath $OutputDir -Filter "*.7z" |
            Sort-Object LastWriteTimeUtc, Name
    )
    if ($Archives.Count -ne 2) {
        throw "WAL 变化应触发第二轮备份，实际压缩包数量：$($Archives.Count)"
    }
    $BaseArchive = $Archives[0]
    $SecondArchive = $Archives[-1]

    $BaseExtractDir = Join-Path $ResolvedTestRoot "extract-base"
    New-Item -ItemType Directory -Force -Path $BaseExtractDir | Out-Null
    & $SevenZip x $BaseArchive.FullName "-o$BaseExtractDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "无法解压基包：$($BaseArchive.FullName)"
    }

    & $SevenZip x $SecondArchive.FullName "-o$ExtractDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "无法解压第二轮备份：$($SecondArchive.FullName)"
    }

    if (
        (Test-Path -LiteralPath (Join-Path $ExtractDir "index.db-wal")) -or
        (Test-Path -LiteralPath (Join-Path $ExtractDir "index.db-shm"))
    ) {
        throw "归档中不应包含 SQLite WAL/SHM 活动副文件。"
    }

    $VerifyBuilder = New-Object System.Data.SQLite.SQLiteConnectionStringBuilder
    $VerifyBuilder.DataSource = [string](Join-Path $ExtractDir "index.db")
    $VerifyBuilder.ReadOnly = $true
    $Reader = New-Object System.Data.SQLite.SQLiteConnection
    $Reader.ConnectionString = $VerifyBuilder.ConnectionString
    $Reader.Open()
    $ReaderCommand = $Reader.CreateCommand()
    $ReaderCommand.CommandText = "SELECT COUNT(*) FROM cycles;"
    $RowCount = [int]$ReaderCommand.ExecuteScalar()
    $ReaderCommand.CommandText = "PRAGMA integrity_check;"
    $Integrity = [string]$ReaderCommand.ExecuteScalar()

    if ($RowCount -ne 2) {
        throw "第二轮 SQLite 快照缺少 WAL 中的新记录，实际行数：$RowCount"
    }
    if ($Integrity -ine "ok") {
        throw "第二轮 SQLite 快照完整性检查失败：$Integrity"
    }

    $Manifest = Get-Content `
        -LiteralPath (Join-Path $ExtractDir "backup-manifest.json") `
        -Raw |
        ConvertFrom-Json
    $BaseManifest = Get-Content `
        -LiteralPath (Join-Path $BaseExtractDir "backup-manifest.json") `
        -Raw |
        ConvertFrom-Json
    if ($Manifest.python_required -ne $false) {
        throw "归档清单必须明确标记不需要 Python。"
    }
    if ($BaseManifest.backup_kind -ne "base" -or $BaseManifest.backup_sequence -ne 1) {
        throw "首个归档没有正确标记为基包。"
    }
    if ($BaseManifest.project_runtime_identity_file -ne "Config/runtime-build-identity.json" -or
        [string]::IsNullOrWhiteSpace([string]$BaseManifest.project_runtime_identity_captured_utc)) {
        throw "基包没有记录项目运行身份文件及其捕获时间。"
    }
    if ($BaseManifest.base_reason -ne "旧状态迁移，建立可验证链") {
        throw "旧 CSV 状态升级时没有创建新的完整基包。"
    }
    if ($Manifest.backup_kind -ne "incremental" -or $Manifest.backup_sequence -ne 2) {
        throw "第二个归档没有正确标记为第 2 个增量包。"
    }
    if ($Manifest.base_backup_id -ne $BaseManifest.backup_id) {
        throw "增量包没有指向正确的基包。"
    }
    if ($Manifest.previous_backup_id -ne $BaseManifest.backup_id) {
        throw "增量包没有指向正确的前一节点。"
    }
    if ([string]::IsNullOrWhiteSpace([string]$Manifest.application_version)) {
        throw "归档清单缺少自动识别的程序版本。"
    }

    # 仅删除文件时也必须生成可追溯增量包，否则无法将基包还原为最终状态。
    Remove-Item -LiteralPath (Join-Path $SourceDir "sealed-without-newline.csv") -Force
    $DeletionOutput = & $WindowsPowerShell @CommonArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "删除增量备份失败：`n$($DeletionOutput -join "`n")"
    }
    $Archives = @(
        Get-ChildItem -LiteralPath $OutputDir -Filter "*.7z" |
            Sort-Object LastWriteTimeUtc, Name
    )
    if ($Archives.Count -ne 3) {
        throw "仅删除文件时应生成第 3 个增量包，实际压缩包数量：$($Archives.Count)"
    }

    $DeletionExtractDir = Join-Path $ResolvedTestRoot "extract-deletion"
    New-Item -ItemType Directory -Force -Path $DeletionExtractDir | Out-Null
    & $SevenZip x $Archives[-1].FullName "-o$DeletionExtractDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "无法解压删除增量包：$($Archives[-1].FullName)"
    }
    $DeletionManifest = Get-Content `
        -LiteralPath (Join-Path $DeletionExtractDir "backup-manifest.json") `
        -Raw |
        ConvertFrom-Json
    if ($DeletionManifest.backup_kind -ne "incremental" -or
        $DeletionManifest.backup_sequence -ne 3) {
        throw "删除增量包没有正确标记为第 3 个链节点。"
    }
    if ($DeletionManifest.previous_backup_id -ne $Manifest.backup_id) {
        throw "删除增量包没有指向第 2 个链节点。"
    }
    if (-not $DeletionManifest.contains_deletions -or
        @($DeletionManifest.deleted_paths) -notcontains "sealed-without-newline.csv") {
        throw "删除增量包缺少删除路径清单。"
    }
    if (@($DeletionManifest.files).Count -ne 0) {
        throw "删除增量包不应包含未变化文件。"
    }

    Write-Host "PASS：Windows PowerShell 5.1 无 Python 基包/增量/WAL/删除链测试通过。"
    Write-Host "压缩包数量：$($Archives.Count)"
    Write-Host "第二轮 SQLite 行数：$RowCount"
    Write-Host "SQLite integrity_check：$Integrity"
}
finally {
    if ($null -ne $ReaderCommand) { $ReaderCommand.Dispose() }
    if ($null -ne $Reader) { $Reader.Dispose() }
    if ($null -ne $WriterCommand) { $WriterCommand.Dispose() }
    if ($null -ne $Writer) { $Writer.Dispose() }

    if (
        (Test-Path -LiteralPath $ResolvedTestRoot) -and
        $ResolvedTestRoot.StartsWith(
            $ResolvedTempRoot,
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        Remove-Item -LiteralPath $ResolvedTestRoot -Recurse -Force
    }
}
