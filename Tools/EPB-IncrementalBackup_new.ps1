param(
    # --------------------------------------------------------
    # 程序版本号
    #
    # 支持：
    #   -Version 2.13.0.28
    #   -Version V2.13.0.28
    #
    # 最终统一输出：
    #   V2.13.0.28
    # --------------------------------------------------------
    # 留空时自动读取当前 MTTFTest.exe 的 ProductVersion。
    # 手动传入时会与项目身份、指定/自动找到的 EXE 版本交叉核对；
    # 若不一致，脚本会要求选择本次备份使用的版本。
    [string]$Version = "",

    # 当宿主无法提供交互输入时，可显式指定冲突时采用的版本。
    # 该值必须是项目身份、EXE 或 -Version 提供的候选版本之一。
    [string]$VersionOnConflict = "",

    # 可选：指定当前运行的 MTTFTest.exe，用于自动识别版本。
    [string]$ApplicationExecutable = "",

    # 正在运行的数据目录
    [string]$SourceDir = "D:\EPB_Data\10358-029a",

    # 压缩包输出目录
    [string]$OutputDir = "D:\EPB_Data",

    # 状态及临时文件目录
    [string]$WorkRoot = "D:\EPB_BackupWork",

    # 7-Zip
    [string]$SevenZip = "C:\Program Files\7-Zip\7z.exe",

    # System.Data.SQLite.dll。留空时从脚本目录、当前目录和正在运行的
    # MTTFTest 程序目录中自动查找。现场机不需要安装 Python。
    [string]$SQLiteAssemblyPath = "",

    # 主动开启新的完整基包；完成后后续备份会归入这条新链。
    [switch]$StartNewBase
)


# ============================================================
# 基础设置
# ============================================================

$ErrorActionPreference = "Stop"


function Test-IsSqliteDatabaseFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $Stream = $null

    try {
        $ShareMode =
            [IO.FileShare]::ReadWrite -bor
            [IO.FileShare]::Delete

        $Stream = [IO.FileStream]::new(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            $ShareMode
        )

        if ($Stream.Length -lt 16) {
            return $false
        }

        $HeaderBytes = New-Object byte[] 16
        if ($Stream.Read($HeaderBytes, 0, 16) -ne 16) {
            return $false
        }

        $Header = [Text.Encoding]::ASCII.GetString($HeaderBytes)
        return $Header -eq ("SQLite format 3" + [char]0)
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $Stream) {
            $Stream.Dispose()
        }
    }
}


function ConvertTo-NormalizedVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$SourceDescription
    )

    $Normalized = $Value.Trim()
    if ($Normalized -notmatch '^[Vv]') {
        $Normalized = "V$Normalized"
    }
    else {
        $Normalized = "V" + $Normalized.Substring(1)
    }

    if ($Normalized -notmatch '^V\d+\.\d+\.\d+\.\d+$') {
        throw "$SourceDescription 的版本号格式不正确：$Value"
    }

    return $Normalized
}


function Read-ProjectRuntimeIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$SourceDirectory
    )

    $IdentityPath = Join-Path $SourceDirectory "Config\runtime-build-identity.json"
    if (-not (Test-Path -LiteralPath $IdentityPath -PathType Leaf)) {
        return $null
    }

    try {
        # RuntimeBuildIdentity 以 UTF-8（无 BOM）写入此文件。Windows PowerShell
        # 5.1 的 Get-Content 默认按系统 ANSI 代码页读取无 BOM 文件；中文内容会
        # 乱码，末尾的多字节字符还可能吞掉紧随其后的 JSON 引号。
        $Identity = Get-Content -LiteralPath $IdentityPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch {
        throw "读取项目运行身份失败：$IdentityPath`n$($_.Exception.Message)"
    }

    $ExpectedProject = Split-Path $SourceDirectory.TrimEnd('\') -Leaf
    if ([int]$Identity.schemaVersion -ne 1) {
        throw "项目运行身份 schemaVersion 不受支持：$IdentityPath"
    }
    if ([string]::IsNullOrWhiteSpace([string]$Identity.projectName) -or
        [string]$Identity.projectName -ine $ExpectedProject) {
        throw "项目运行身份中的 projectName 与源目录不一致：$IdentityPath"
    }

    $NormalizedVersion = ConvertTo-NormalizedVersion `
        -Value ([string]$Identity.productVersion) `
        -SourceDescription "项目运行身份"
    $CapturedUtc = [DateTime]::MinValue
    if (-not [DateTime]::TryParse(
            [string]$Identity.capturedUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$CapturedUtc)) {
        throw "项目运行身份 capturedUtc 格式无效：$IdentityPath"
    }

    return [PSCustomObject]@{
        Path = [IO.Path]::GetFullPath($IdentityPath)
        ProjectName = [string]$Identity.projectName
        ProductVersion = $NormalizedVersion
        AssemblyVersion = [string]$Identity.assemblyVersion
        ExecutablePath = [string]$Identity.executablePath
        ExecutableSha256 = [string]$Identity.executableSha256
        CapturedUtc = $CapturedUtc
    }
}


function Resolve-BackupVersion {
    param(
        [string]$RequestedVersion,

        [string]$ConflictVersion,

        [Parameter(Mandatory)]
        [PSCustomObject]$ApplicationIdentity
    )

    $Candidates = @()

    if (-not [string]::IsNullOrWhiteSpace($RequestedVersion)) {
        $Candidates += [PSCustomObject]@{
            Version = ConvertTo-NormalizedVersion `
                -Value $RequestedVersion `
                -SourceDescription "-Version 参数"
            Source = "-Version 参数"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ApplicationIdentity.ProductVersion)) {
        $Candidates += [PSCustomObject]@{
            Version = ConvertTo-NormalizedVersion `
                -Value $ApplicationIdentity.ProductVersion `
                -SourceDescription "MTTFTest.exe"
            Source = "EXE 版本"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ApplicationIdentity.ProjectVersion)) {
        $Candidates += [PSCustomObject]@{
            Version = ConvertTo-NormalizedVersion `
                -Value $ApplicationIdentity.ProjectVersion `
                -SourceDescription "项目运行身份"
            Source = "项目身份"
        }
    }

    if ($Candidates.Count -eq 0) {
        throw "未获得可用于备份的版本号。请传入 -Version Vx.x.x.x。"
    }

    $DistinctVersions = @($Candidates.Version | Select-Object -Unique)
    if ($DistinctVersions.Count -eq 1) {
        return [PSCustomObject]@{
            Version = $DistinctVersions[0]
            Source = ($Candidates.Source | Select-Object -Unique) -join "、"
        }
    }

    $VersionChoices = @(
        foreach ($CandidateVersion in $DistinctVersions) {
            [PSCustomObject]@{
                Version = $CandidateVersion
                Source = (@(
                    $Candidates |
                        Where-Object { $_.Version -ieq $CandidateVersion } |
                        Select-Object -ExpandProperty Source -Unique
                ) -join "、")
            }
        }
    )

    if (-not [string]::IsNullOrWhiteSpace($ConflictVersion)) {
        $NormalizedConflictVersion = ConvertTo-NormalizedVersion `
            -Value $ConflictVersion `
            -SourceDescription "-VersionOnConflict 参数"
        $Selected = @(
            $VersionChoices |
                Where-Object { $_.Version -ieq $NormalizedConflictVersion }
        ) | Select-Object -First 1

        if ($null -eq $Selected) {
            throw @"
-VersionOnConflict 指定的版本不在本次候选版本中：$NormalizedConflictVersion
候选版本：$($DistinctVersions -join '、')
"@
        }

        return [PSCustomObject]@{
            Version = $Selected.Version
            Source = "冲突处理参数：$($Selected.Source)"
        }
    }

    Write-Host ""
    Write-Host "检测到备份版本不一致，请选择本次备份使用的版本：" -ForegroundColor Yellow
    for ($CandidateIndex = 0; $CandidateIndex -lt $VersionChoices.Count; $CandidateIndex++) {
        $Candidate = $VersionChoices[$CandidateIndex]
        $ChoiceDescription = "{0}：{1}（{2}）" -f `
            ($CandidateIndex + 1), $Candidate.Version, $Candidate.Source
        Write-Host "  $ChoiceDescription"
    }
    if (-not [string]::IsNullOrWhiteSpace($ApplicationIdentity.Path)) {
        Write-Host "  EXE 路径：$($ApplicationIdentity.Path)"
    }
    if (-not [string]::IsNullOrWhiteSpace($ApplicationIdentity.ProjectIdentityPath)) {
        Write-Host "  身份文件：$($ApplicationIdentity.ProjectIdentityPath)"
    }

    while ($true) {
        # powershell.exe -File 启动的子进程中，Read-Host 可能读到空的
        # 管道输入；Console.ReadLine 直接读取当前控制台，现场命令可正常输入。
        [Console]::Write("请输入序号（输入 Q 取消备份）：")
        $Selection = [Console]::ReadLine()
        if ($Selection -match '^[Qq]$') {
            throw "用户取消备份：未选择冲突版本。"
        }

        # 某些远程/自动化终端既不提供 stdin，也禁止显示桌面对话框。
        # 此时不能猜测版本，输出所有可用参数并安全停止。
        if ([string]::IsNullOrWhiteSpace($Selection)) {
            $ConflictArguments = @(
                $DistinctVersions |
                    ForEach-Object { '  -VersionOnConflict "{0}"' -f $_ }
            ) -join "`n"
            throw @"
当前终端不支持交互输入，无法在运行中选择版本。
请重新执行原命令，并在末尾添加以下任意一个参数：
$ConflictArguments
"@
        }

        [int]$SelectedIndex = 0
        $ValidSelection = [int]::TryParse($Selection, [ref]$SelectedIndex) -and
            $SelectedIndex -ge 1 -and $SelectedIndex -le $VersionChoices.Count
        if ($ValidSelection) {
            break
        }

        Write-Host "输入无效，请输入 1 至 $($VersionChoices.Count)，或输入 Q 取消。" -ForegroundColor Yellow
    }

    $Selected = $VersionChoices[$SelectedIndex - 1]
    return [PSCustomObject]@{
        Version = $Selected.Version
        Source = "交互选择：$($Selected.Source)"
    }
}


function Resolve-ApplicationIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$SourceDirectory
    )

    $ProjectIdentity = Read-ProjectRuntimeIdentity -SourceDirectory $SourceDirectory
    $Candidates = @()
    $CandidateSource = "自动查找"

    if (-not [string]::IsNullOrWhiteSpace($ApplicationExecutable)) {
        $Candidates += $ApplicationExecutable
        $CandidateSource = "-ApplicationExecutable"
    }
    else {
        $RunningCandidates = @(
            Get-Process -Name "MTTFTest" -ErrorAction SilentlyContinue |
            ForEach-Object {
                try {
                    if (-not [string]::IsNullOrWhiteSpace($_.Path)) {
                        $_.Path
                    }
                }
                catch {
                    # 无权读取进程路径时保留其他版本来源。
                }
            }
        )
        if ($RunningCandidates.Count -gt 0) {
            $Candidates += $RunningCandidates
            $CandidateSource = "正在运行的 MTTFTest 进程"
        }
        elseif ($null -ne $ProjectIdentity -and
                -not [string]::IsNullOrWhiteSpace($ProjectIdentity.ExecutablePath) -and
                (Test-Path -LiteralPath $ProjectIdentity.ExecutablePath -PathType Leaf)) {
            $Candidates += $ProjectIdentity.ExecutablePath
            $CandidateSource = "项目运行身份记录的 EXE"
        }
        else {
            $Candidates += Join-Path $PSScriptRoot "MTTFTest.exe"
            $Candidates += Join-Path (Get-Location).Path "MTTFTest.exe"
            $Candidates += Join-Path $SourceDirectory "MTTFTest.exe"
        }
    }

    $ResolvedCandidates = @(
        @(
            foreach ($Candidate in ($Candidates | Select-Object -Unique)) {
                if (Test-Path -LiteralPath $Candidate -PathType Leaf) {
                    [IO.Path]::GetFullPath($Candidate)
                }
            }
        ) | Select-Object -Unique
    )

    if ($ResolvedCandidates.Count -gt 1) {
        $ResolvedVersions = @(
            foreach ($ResolvedCandidate in $ResolvedCandidates) {
                $Info = (Get-Item -LiteralPath $ResolvedCandidate).VersionInfo
                $Value = if (-not [string]::IsNullOrWhiteSpace([string]$Info.ProductVersion)) {
                    [string]$Info.ProductVersion
                } else { [string]$Info.FileVersion }
                ConvertTo-NormalizedVersion -Value $Value -SourceDescription $ResolvedCandidate
            }
        ) | Select-Object -Unique
        if ($ResolvedVersions.Count -eq 1) {
            $ResolvedCandidates = @($ResolvedCandidates[0])
        }
    }

    if ($ResolvedCandidates.Count -gt 1) {
        if ([string]::IsNullOrWhiteSpace($Version)) {
            throw @"
检测到多个 MTTFTest.exe，无法安全确定当前程序版本：
$($ResolvedCandidates -join "`n")

请通过 -ApplicationExecutable 指定当前运行的 MTTFTest.exe，或显式传入 -Version。
"@
        }

        return [PSCustomObject]@{
            Path = $null
            ProductVersion = $null
            FileVersion = $null
            VersionSource = "手工参数（发现多个 EXE）"
        }
    }

    if ($ResolvedCandidates.Count -eq 0 -and $null -ne $ProjectIdentity) {
        return [PSCustomObject]@{
            Path = $null
            ProductVersion = $null
            FileVersion = $ProjectIdentity.AssemblyVersion
            VersionSource = "项目 Config\runtime-build-identity.json"
            ProjectIdentityPath = $ProjectIdentity.Path
            ProjectIdentityCapturedUtc = $ProjectIdentity.CapturedUtc
            ProjectVersion = $ProjectIdentity.ProductVersion
        }
    }

    if ($ResolvedCandidates.Count -eq 0) {
        if ([string]::IsNullOrWhiteSpace($Version)) {
            throw @"
未找到当前运行的 MTTFTest.exe，因此无法自动读取版本号。

请将脚本放到 MTTFTest.exe 同一目录、保持程序运行，或传入
-ApplicationExecutable "完整路径\\MTTFTest.exe"。
也可以在离线备份时显式传入 -Version Vx.x.x.x。
"@
        }

        return [PSCustomObject]@{
            Path = $null
            ProductVersion = $null
            FileVersion = $null
            VersionSource = "手工参数"
            ProjectIdentityPath = $null
            ProjectIdentityCapturedUtc = $null
            ProjectVersion = $null
        }
    }

    $ExecutablePath = $ResolvedCandidates[0]
    $VersionInfo = (Get-Item -LiteralPath $ExecutablePath).VersionInfo
    $DetectedVersion = [string]$VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($DetectedVersion)) {
        $DetectedVersion = [string]$VersionInfo.FileVersion
    }

    if ([string]::IsNullOrWhiteSpace($DetectedVersion)) {
        throw "无法从 EXE 读取 ProductVersion/FileVersion：$ExecutablePath"
    }

    $NormalizedDetectedVersion = ConvertTo-NormalizedVersion `
        -Value $DetectedVersion `
        -SourceDescription $ExecutablePath
    if ($null -ne $ProjectIdentity -and
        -not [string]::IsNullOrWhiteSpace($ProjectIdentity.ExecutablePath) -and
        [IO.Path]::GetFullPath($ExecutablePath) -ieq [IO.Path]::GetFullPath($ProjectIdentity.ExecutablePath) -and
        $ProjectIdentity.ExecutableSha256 -match '^[0-9a-fA-F]{64}$') {
        $ActualExecutableSha256 = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash
        if ($ActualExecutableSha256 -ine $ProjectIdentity.ExecutableSha256) {
            throw "项目运行身份中的 EXE SHA-256 与当前文件不一致：$ExecutablePath"
        }
    }

    return [PSCustomObject]@{
        Path = $ExecutablePath
        ProductVersion = [string]$VersionInfo.ProductVersion
        FileVersion = [string]$VersionInfo.FileVersion
        VersionSource = if ($null -ne $ProjectIdentity) {
            "项目运行身份（已与$CandidateSource 交叉校验）"
        } else { "EXE 文件版本" }
        ProjectIdentityPath = if ($null -ne $ProjectIdentity) { $ProjectIdentity.Path } else { $null }
        ProjectIdentityCapturedUtc = if ($null -ne $ProjectIdentity) {
            $ProjectIdentity.CapturedUtc
        } else { $null }
        ProjectVersion = if ($null -ne $ProjectIdentity) {
            $ProjectIdentity.ProductVersion
        } else { $null }
    }
}

Write-Host ""
Write-Host "============================================================"
Write-Host " EPB 增量压缩备份"
Write-Host "============================================================"
Write-Host ""


# ============================================================
# 规范化路径
# ============================================================

$SourceDir = [IO.Path]::GetFullPath($SourceDir).TrimEnd('\')
$OutputDir = [IO.Path]::GetFullPath($OutputDir).TrimEnd('\')
$WorkRoot  = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\')


# ============================================================
# 检查源目录
# ============================================================

if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) {
    throw "源目录不存在：$SourceDir"
}


# ============================================================
# 检查 7-Zip
# ============================================================

if (-not (Test-Path -LiteralPath $SevenZip -PathType Leaf)) {
    throw "未找到 7-Zip：$SevenZip"
}


# ============================================================
# 项目名
#
# D:\EPB_Data\10358-029a
#
# 自动得到：
# 10358-029a
# ============================================================

$ProjectName = Split-Path $SourceDir -Leaf


# ============================================================
# 版本号处理
#
# 数据项目目录本身不保存可信的程序版本；优先读取当前 MTTFTest.exe
# 的 ProductVersion。离线备份或多进程场景才使用手工 -Version。
# ============================================================

$ApplicationIdentity = Resolve-ApplicationIdentity -SourceDirectory $SourceDir
$VersionResolution = Resolve-BackupVersion `
    -RequestedVersion $Version `
    -ConflictVersion $VersionOnConflict `
    -ApplicationIdentity $ApplicationIdentity
$Version = $VersionResolution.Version
$ApplicationIdentity.VersionSource = $VersionResolution.Source


# ============================================================
# 防止工作目录位于源目录内部
# ============================================================

if (
    $WorkRoot.StartsWith(
        "$SourceDir\",
        [StringComparison]::OrdinalIgnoreCase
    )
) {
    throw "WorkRoot 不能放在源目录内部，否则备份文件会被再次识别为数据。"
}


# ============================================================
# 创建目录
# ============================================================

$StateDir = Join-Path $WorkRoot "State"
$StageRoot = Join-Path $WorkRoot "Stage"

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
New-Item -ItemType Directory -Force -Path $StageRoot | Out-Null


# ============================================================
# 状态文件
#
# 注意：
# 状态文件只和项目有关。
#
# 不包含 Version。
#
# 因此：
#
# V2.13.0.28
#     ↓
# V2.13.0.29
#
# 不会重新开始全量备份。
# ============================================================

$ManifestPath = Join-Path $StateDir "${ProjectName}.csv"
$ChainPath = Join-Path $StateDir "${ProjectName}.backup-chain.json"


# ============================================================
# 读取备份链状态
#
# CSV 仅记录“哪些文件已经备份”；链状态明确记录可恢复的基包、上一个
# 增量包和顺序。旧脚本留下 CSV 而没有链状态时，本版本会先创建新的
# 完整基包，避免把不完整的首个增量误当作基包。
# ============================================================

$IsFirstRun = -not (Test-Path -LiteralPath $ManifestPath)
$PreviousChain = $null

if (Test-Path -LiteralPath $ChainPath -PathType Leaf) {
    try {
        $PreviousChain = Get-Content -LiteralPath $ChainPath -Raw |
            ConvertFrom-Json
    }
    catch {
        throw "读取备份链状态失败：$ChainPath`n$($_.Exception.Message)"
    }

    if (
        $PreviousChain.project -ne $ProjectName -or
        [string]::IsNullOrWhiteSpace([string]$PreviousChain.base_backup_id) -or
        [string]::IsNullOrWhiteSpace([string]$PreviousChain.last_backup_id) -or
        [int]$PreviousChain.last_sequence -lt 1
    ) {
        throw "备份链状态内容无效：$ChainPath。请使用 -StartNewBase 创建新的完整基包。"
    }
}

$CreateBaseBackup =
    $StartNewBase.IsPresent -or
    $IsFirstRun -or
    $null -eq $PreviousChain


if ($CreateBaseBackup) {
    $BackupKind = "base"
    $BackupType = "全量基包"
    $BackupId = [Guid]::NewGuid().ToString("N")
    $BaseBackupId = $BackupId
    $PreviousBackupId = $null
    $BackupSequence = 1

    if ($StartNewBase.IsPresent) {
        $BaseReason = "按参数重新建立"
    }
    elseif ($IsFirstRun) {
        $BaseReason = "项目首次备份"
    }
    else {
        $BaseReason = "旧状态迁移，建立可验证链"
    }
}
else {
    $BackupKind = "incremental"
    $BackupType = "增量"
    $BackupId = [Guid]::NewGuid().ToString("N")
    $BaseBackupId = [string]$PreviousChain.base_backup_id
    $PreviousBackupId = [string]$PreviousChain.last_backup_id
    $BackupSequence = [int]$PreviousChain.last_sequence + 1
    $BaseReason = $null
}


# ============================================================
# 显示当前配置
# ============================================================

Write-Host "项目名称 : $ProjectName"
Write-Host "程序版本 : $Version"
Write-Host "版本来源 : $($ApplicationIdentity.VersionSource)"
if (-not [string]::IsNullOrWhiteSpace($ApplicationIdentity.Path)) {
    Write-Host "程序路径 : $($ApplicationIdentity.Path)"
}
if (-not [string]::IsNullOrWhiteSpace($ApplicationIdentity.ProjectIdentityPath)) {
    Write-Host "项目身份 : $($ApplicationIdentity.ProjectIdentityPath)"
    Write-Host "身份时间 : $($ApplicationIdentity.ProjectIdentityCapturedUtc.ToString('o'))"
}
Write-Host "源目录   : $SourceDir"
Write-Host "输出目录 : $OutputDir"
Write-Host "备份类型 : $BackupType"
Write-Host ("备份序号 : {0:D4}" -f $BackupSequence)
if ($CreateBaseBackup) {
    Write-Host "基包来源 : $BaseReason"
}
else {
    Write-Host "基包 ID   : $BaseBackupId"
    Write-Host "上一个包 : $PreviousBackupId"
}
Write-Host ""


# ============================================================
# 读取上一次状态
#
# Key：
#   相对路径
#
# Value：
#   文件长度
#   修改时间
# ============================================================

$PreviousState = @{}


if (-not $IsFirstRun) {

    Write-Host "正在读取上次备份状态..."

    try {
        Import-Csv -LiteralPath $ManifestPath |
            ForEach-Object {

                if (-not [string]::IsNullOrWhiteSpace($_.RelativePath)) {
                    $PreviousState[$_.RelativePath] = [PSCustomObject]@{
                        Length = [Int64]$_.Length

                        LastWriteTimeUtcTicks =
                            [Int64]$_.LastWriteTimeUtcTicks
                    }
                }
            }
    }
    catch {
        throw "读取状态文件失败：$ManifestPath`n$($_.Exception.Message)"
    }
}


# ============================================================
# 扫描当前源目录
# ============================================================

Write-Host "正在扫描源目录..."


$AllSourceFiles = @(
    Get-ChildItem `
        -LiteralPath $SourceDir `
        -File `
        -Recurse `
        -Force `
        -ErrorAction Stop
)

$SourcePrefix = $SourceDir.TrimEnd('\') + '\'
$FileRecords = @()
$FilesByRelativePath = @{}


foreach ($File in $AllSourceFiles) {
    $NormalizedFilePath = [IO.Path]::GetFullPath($File.FullName)

    if (-not $NormalizedFilePath.StartsWith(
        $SourcePrefix,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "源文件超出配置的源目录边界：$NormalizedFilePath"
    }

    # Windows PowerShell 5.1 运行于 .NET Framework，不提供
    # System.IO.Path.GetRelativePath。这里先对绝对路径做前缀边界验证，
    # 再截取相对路径，兼容 5.1 且不会允许路径逃逸。
    $RelativePath = $NormalizedFilePath.Substring($SourcePrefix.Length)

    $Record = [PSCustomObject]@{
        File = $File
        RelativePath = $RelativePath
    }
    $FileRecords += $Record
    $FilesByRelativePath[$RelativePath] = $Record
}


# ============================================================
# 查找新增 / 修改文件
#
# 普通文件按相对路径、长度、修改时间判断。
# SQLite 的 -wal 不直接进入压缩包，但它的变化会触发主数据库在线快照。
# -shm 是可重建的共享内存文件，不进入压缩包，也不写入状态文件。
# ============================================================

$SourceFiles = @()
$CurrentPaths = @{}
$ChangedFiles = @()
$ChangedPathSet = @{}
$SqliteDatabasePaths = @{}
$SqliteWalStateByDatabase = @{}
$SqliteDatabaseTriggeredByWal = @{}


foreach ($Record in $FileRecords) {
    $File = $Record.File
    $RelativePath = $Record.RelativePath
    $SidecarMatch = [regex]::Match(
        $RelativePath,
        '^(.*)(-wal|-shm)$',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase
    )

    if ($SidecarMatch.Success) {
        $DatabaseRelativePath = $SidecarMatch.Groups[1].Value
        $DatabaseRecord = $FilesByRelativePath[$DatabaseRelativePath]
        $DatabaseExtension =
            [IO.Path]::GetExtension($DatabaseRelativePath).ToLowerInvariant()

        $IsSqliteSidecar =
            $null -ne $DatabaseRecord -and
            $DatabaseExtension -in @('.db', '.sqlite', '.sqlite3') -and
            (Test-IsSqliteDatabaseFile -Path $DatabaseRecord.File.FullName)

        if ($IsSqliteSidecar) {
            $SqliteDatabasePaths[$DatabaseRelativePath] = $true

            # SHM 不包含需要独立恢复的数据，旧状态中的 SHM 会在本轮移除。
            if ($SidecarMatch.Groups[2].Value -ieq '-shm') {
                continue
            }

            $CurrentPaths[$RelativePath] = $true
            $WalState = [PSCustomObject]@{
                Length = [Int64]$File.Length
                LastWriteTimeUtcTicks = [Int64]$File.LastWriteTimeUtc.Ticks
            }
            $SqliteWalStateByDatabase[$DatabaseRelativePath] =
                [PSCustomObject]@{
                    RelativePath = $RelativePath
                    State = $WalState
                }

            $OldWal = $PreviousState[$RelativePath]
            if (
                $null -eq $OldWal -or
                $OldWal.Length -ne $File.Length -or
                $OldWal.LastWriteTimeUtcTicks -ne $File.LastWriteTimeUtc.Ticks
            ) {
                $SqliteDatabaseTriggeredByWal[$DatabaseRelativePath] = $true
            }

            continue
        }
    }

    $SourceFiles += $File

    $CurrentPaths[$RelativePath] = $true

    $Extension = [IO.Path]::GetExtension($RelativePath).ToLowerInvariant()
    if (
        $Extension -in @('.db', '.sqlite', '.sqlite3') -and
        (Test-IsSqliteDatabaseFile -Path $File.FullName)
    ) {
        $SqliteDatabasePaths[$RelativePath] = $true
    }


    $Old = $PreviousState[$RelativePath]


    $Changed = $false


    if ($null -eq $Old) {

        # 新文件
        $Changed = $true

    }
    elseif ($Old.Length -ne $File.Length) {

        # 文件大小变化
        $Changed = $true

    }
    elseif (
        $Old.LastWriteTimeUtcTicks -ne
        $File.LastWriteTimeUtc.Ticks
    ) {

        # 修改时间变化
        $Changed = $true
    }


    if ($Changed) {

        $ChangedFiles += [PSCustomObject]@{
            File         = $File
            RelativePath = $RelativePath
        }
        $ChangedPathSet[$RelativePath] = $true
    }
}


foreach ($DatabaseRelativePath in $SqliteDatabaseTriggeredByWal.Keys) {
    if (
        -not $ChangedPathSet.ContainsKey($DatabaseRelativePath) -and
        $FilesByRelativePath.ContainsKey($DatabaseRelativePath)
    ) {
        $ChangedFiles += $FilesByRelativePath[$DatabaseRelativePath]
        $ChangedPathSet[$DatabaseRelativePath] = $true
    }
}


if ($CreateBaseBackup) {
    # 基包必须覆盖当前全部可恢复文件，不能复用旧 CSV 的“已备份”判断。
    $ChangedFiles = @(
        foreach ($File in $SourceFiles) {
            $NormalizedFilePath = [IO.Path]::GetFullPath($File.FullName)
            [PSCustomObject]@{
                File = $File
                RelativePath = $NormalizedFilePath.Substring($SourcePrefix.Length)
            }
        }
    )
    $ChangedPathSet = @{}
    foreach ($Item in $ChangedFiles) {
        $ChangedPathSet[$Item.RelativePath] = $true
    }
}


Write-Host "当前文件总数：$($AllSourceFiles.Count)"
Write-Host "备份候选文件：$($SourceFiles.Count)"
Write-Host ""


# ============================================================
# 如果以前存在的文件已经从源目录消失
#
# 这些路径会写入本轮增量包的 backup-manifest.json；恢复时应按链顺序
# 删除目标目录中的对应文件，才能得到精确的最终目录状态。
# ============================================================

$DeletedStatePaths = @(
    $PreviousState.Keys |
        Where-Object {
            -not $CurrentPaths.ContainsKey($_)
        }
)


# ============================================================
# 没有新增 / 修改
# ============================================================

if ($ChangedFiles.Count -eq 0 -and $DeletedStatePaths.Count -eq 0) {
    Write-Host ""
    Write-Host "============================================================"
    Write-Host " 没有发现新增或修改文件"
    Write-Host "============================================================"
    Write-Host ""
    Write-Host "本次不生成 7z 压缩包。"
    Write-Host ""

    exit 0
}


if ($ChangedFiles.Count -eq 0) {
    Write-Host "发现已删除文件：$($DeletedStatePaths.Count) 个"
    Write-Host "本轮生成仅含删除清单的增量包。"
    Write-Host ""
}


# ============================================================
# 统计变化文件原始大小
# ============================================================

[Int64]$ChangedBytes = 0


foreach ($Item in $ChangedFiles) {
    $ChangedBytes += $Item.File.Length
}


$ChangedSizeGB = $ChangedBytes / 1GB


Write-Host "发现新增 / 修改文件：$($ChangedFiles.Count) 个"
Write-Host ("原始数据量          ：{0:N2} GB" -f $ChangedSizeGB)
Write-Host ""


# ============================================================
# 本次时间
#
# 例如：
#
# 0827_1725
# ============================================================

$Now = Get-Date

$TimeTag = $Now.ToString("MMdd_HHmm")


# ============================================================
# 压缩包文件名
#
# B0001 = 基包；I0002 = 第 2 个链节点（增量）。版本号记录本次程序版本，
# 备份链归属则由包内 backup-manifest.json 的 Base/Previous ID 确认。
# ============================================================

$BackupLabel = if ($BackupKind -eq "base") {
    "B{0:D4}" -f $BackupSequence
}
else {
    "I{0:D4}" -f $BackupSequence
}
$ArchiveBase = "${ProjectName}_${Version}_${BackupLabel}_${TimeTag}"
$ArchivePath = Join-Path $OutputDir "$ArchiveBase.7z"

if (Test-Path -LiteralPath $ArchivePath) {
    $Counter = 1

    do {
        $ArchivePath = Join-Path $OutputDir (
            "{0}_{1:D2}.7z" -f $ArchiveBase, $Counter
        )
        $Counter++
    } while (Test-Path -LiteralPath $ArchivePath)
}

$ArchiveStagingPath =
    "$ArchivePath.staging-$PID-$([Guid]::NewGuid().ToString('N')).7z"
$ArchiveFileName = Split-Path $ArchivePath -Leaf


# ============================================================
# 临时增量目录
# ============================================================

$StageName = "${ProjectName}_${TimeTag}"

$StageDir = Join-Path $StageRoot $StageName


# 防止异常退出遗留下来的同名目录
if (Test-Path -LiteralPath $StageDir) {

    Remove-Item `
        -LiteralPath $StageDir `
        -Recurse `
        -Force
}


New-Item `
    -ItemType Directory `
    -Force `
    -Path $StageDir |
    Out-Null


# ============================================================
# 文件快照复制函数
#
# 为什么不用简单 Copy-Item？
#
# 因为你的程序正在运行，
# 日志和数据文件可能仍然处于写入状态。
#
# 这里以：
#
# FileShare.ReadWrite + FileShare.Delete
#
# 方式打开。
#
# 打开文件以后，首先记录：
#
#   当前文件长度
#
# 本次只复制这一瞬间已经存在的数据。
#
#
# 示例：
#
# 开始复制：
#     1000 MB
#
# 程序继续写：
#     1100 MB
#
# 本轮压缩：
#     1000 MB
#
# 下一轮：
#     检测到文件变为 1100 MB
#
#     ↓
#
#     再次识别为修改文件
#
#
# 注意：
#
# 这仍然属于“文件级增量”。
#
# 某个 20GB 文件增加到 21GB，
# 下一次还是会重新压整个文件，
# 而不是只压新增的 1GB。
# ============================================================

function Copy-FileSnapshot {

    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination
    )


    $DestinationParent = Split-Path $Destination -Parent


    if (-not (Test-Path -LiteralPath $DestinationParent)) {

        New-Item `
            -ItemType Directory `
            -Force `
            -Path $DestinationParent |
            Out-Null
    }


    # --------------------------------------------------------
    # 保存源文件信息
    # --------------------------------------------------------

    $SourceInfo = Get-Item -LiteralPath $Source -Force

    $CreationTimeUtc =
        $SourceInfo.CreationTimeUtc

    $LastWriteTimeUtc =
        $SourceInfo.LastWriteTimeUtc

    $Attributes =
        $SourceInfo.Attributes


    # --------------------------------------------------------
    # 允许读取正在写入的文件
    # --------------------------------------------------------

    $ShareMode =
        [IO.FileShare]::ReadWrite -bor
        [IO.FileShare]::Delete


    $SourceStream = [IO.FileStream]::new(
        $Source,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        $ShareMode
    )


    try {

        # 本轮快照长度
        [Int64]$SnapshotLength =
            $SourceStream.Length


        $DestinationStream = [IO.FileStream]::new(
            $Destination,
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None
        )


        try {

            # 4 MB Buffer
            $Buffer = New-Object byte[] (4MB)

            [Int64]$Remaining =
                $SnapshotLength


            while ($Remaining -gt 0) {

                $ReadSize = [Math]::Min(
                    [Int64]$Buffer.Length,
                    $Remaining
                )


                $Read = $SourceStream.Read(
                    $Buffer,
                    0,
                    [int]$ReadSize
                )


                if ($Read -le 0) {

                    throw "读取文件时提前到达文件结尾：$Source"
                }


                $DestinationStream.Write(
                    $Buffer,
                    0,
                    $Read
                )


                $Remaining -= $Read
            }
        }
        finally {

            $DestinationStream.Dispose()
        }


        # ----------------------------------------------------
        # 保留原始时间
        # ----------------------------------------------------

        try {
            [IO.File]::SetCreationTimeUtc(
                $Destination,
                $CreationTimeUtc
            )
        }
        catch {
            # CreationTime 设置失败不影响主体备份
        }


        try {
            [IO.File]::SetLastWriteTimeUtc(
                $Destination,
                $LastWriteTimeUtc
            )
        }
        catch {
        }


        # ----------------------------------------------------
        # 尝试保留文件属性
        # ----------------------------------------------------

        try {
            [IO.File]::SetAttributes(
                $Destination,
                $Attributes
            )
        }
        catch {
        }


        # ----------------------------------------------------
        # 返回本轮真正压缩的文件状态
        # ----------------------------------------------------

        return [PSCustomObject]@{
            Length =
                $SnapshotLength

            LastWriteTimeUtcTicks =
                $LastWriteTimeUtc.Ticks
        }
    }
    finally {

        $SourceStream.Dispose()
    }
}

$script:ResolvedSQLiteAssemblyPath = $null


function Initialize-SqliteRuntime {
    if ($null -ne ("System.Data.SQLite.SQLiteConnection" -as [type])) {
        return
    }

    $Candidates = @()

    if (-not [string]::IsNullOrWhiteSpace($SQLiteAssemblyPath)) {
        $Candidates += $SQLiteAssemblyPath
    }

    $Candidates += (Join-Path $PSScriptRoot "System.Data.SQLite.dll")
    $Candidates += (Join-Path (Get-Location).Path "System.Data.SQLite.dll")

    Get-Process -Name "MTTFTest" -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                if (-not [string]::IsNullOrWhiteSpace($_.Path)) {
                    $Candidates += Join-Path (Split-Path $_.Path -Parent) `
                        "System.Data.SQLite.dll"
                }
            }
            catch {
                # 无权读取进程路径时继续检查其他候选位置。
            }
        }

    $ResolvedAssembly = $null
    foreach ($Candidate in ($Candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $Candidate -PathType Leaf) {
            $ResolvedAssembly = [IO.Path]::GetFullPath($Candidate)
            break
        }
    }

    if ($null -eq $ResolvedAssembly) {
        throw @"
未找到 System.Data.SQLite.dll，无法对运行中的 index.db 创建一致快照。

现场机不需要安装 Python。请采用以下任一方式：
1. 将本脚本放到 MTTFTest.exe 同一目录；
2. 将 System.Data.SQLite.dll 及 x86/x64\SQLite.Interop.dll 随脚本部署；
3. 通过 -SQLiteAssemblyPath 指定 MTTFTest 目录中的 System.Data.SQLite.dll。
"@
    }

    $AssemblyDirectory = Split-Path $ResolvedAssembly -Parent
    $Architecture = if ([Environment]::Is64BitProcess) { "x64" } else { "x86" }
    $NativeDirectory = Join-Path $AssemblyDirectory $Architecture

    # System.Data.SQLite 需要与当前 PowerShell 位数匹配的 SQLite.Interop.dll。
    # 发布目录通常同时带有 x86/x64 子目录。
    $PathEntries = @($AssemblyDirectory)
    if (Test-Path -LiteralPath $NativeDirectory -PathType Container) {
        $PathEntries = @($NativeDirectory, $AssemblyDirectory)
    }
    $env:Path = (($PathEntries -join ";") + ";" + $env:Path)

    try {
        Add-Type -Path $ResolvedAssembly
    }
    catch {
        throw "加载 System.Data.SQLite 失败：$ResolvedAssembly`n$($_.Exception.Message)"
    }

    if ($null -eq ("System.Data.SQLite.SQLiteConnection" -as [type])) {
        throw "System.Data.SQLite 已加载，但未找到 SQLiteConnection 类型：$ResolvedAssembly"
    }

    $script:ResolvedSQLiteAssemblyPath = $ResolvedAssembly
}


function Get-SqliteIntegrityStatus {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Initialize-SqliteRuntime

    $Connection = $null
    $Command = $null

    try {
        $Builder = New-Object System.Data.SQLite.SQLiteConnectionStringBuilder
        $Builder.DataSource = [string]$Path
        $Builder.ReadOnly = $true
        $Builder.FailIfMissing = $true
        $Builder.Pooling = $false

        $Connection = New-Object System.Data.SQLite.SQLiteConnection
        $Connection.ConnectionString = $Builder.ConnectionString
        $Connection.Open()

        $Command = $Connection.CreateCommand()
        $Command.CommandText = "PRAGMA integrity_check;"
        return [string]$Command.ExecuteScalar()
    }
    finally {
        if ($null -ne $Command) {
            $Command.Dispose()
        }
        if ($null -ne $Connection) {
            $Connection.Dispose()
        }
    }
}


function Copy-SqliteOnlineSnapshot {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    Initialize-SqliteRuntime

    $DestinationParent = Split-Path $Destination -Parent
    if (-not (Test-Path -LiteralPath $DestinationParent)) {
        New-Item -ItemType Directory -Force -Path $DestinationParent | Out-Null
    }

    $SourceInfo = Get-Item -LiteralPath $Source -Force
    $Temporary = "$Destination.sqlite-backup-tmp"
    $SourceConnection = $null
    $DestinationConnection = $null
    $Command = $null

    if (Test-Path -LiteralPath $Temporary) {
        Remove-Item -LiteralPath $Temporary -Force
    }

    try {
        $SourceBuilder = New-Object System.Data.SQLite.SQLiteConnectionStringBuilder
        $SourceBuilder.DataSource = [string]$Source
        $SourceBuilder.ReadOnly = $true
        $SourceBuilder.FailIfMissing = $true
        $SourceBuilder.Pooling = $false

        $DestinationBuilder = New-Object System.Data.SQLite.SQLiteConnectionStringBuilder
        $DestinationBuilder.DataSource = [string]$Temporary
        $DestinationBuilder.Pooling = $false
        $DestinationBuilder.FailIfMissing = $false

        $SourceConnection = New-Object System.Data.SQLite.SQLiteConnection
        $SourceConnection.ConnectionString = $SourceBuilder.ConnectionString
        $SourceConnection.Open()

        $DestinationConnection = New-Object System.Data.SQLite.SQLiteConnection
        $DestinationConnection.ConnectionString = $DestinationBuilder.ConnectionString
        $DestinationConnection.Open()

        $SourceConnection.BackupDatabase(
            $DestinationConnection,
            "main",
            "main",
            -1,
            $null,
            250
        )

        $Command = $DestinationConnection.CreateCommand()
        $Command.CommandText = "PRAGMA journal_mode=DELETE;"
        [void]$Command.ExecuteScalar()
        $Command.CommandText = "PRAGMA integrity_check;"
        $Integrity = [string]$Command.ExecuteScalar()

        if ($Integrity -ine "ok") {
            throw "SQLite integrity_check 未通过：$Source；结果：$Integrity"
        }
    }
    finally {
        if ($null -ne $Command) {
            $Command.Dispose()
        }
        if ($null -ne $DestinationConnection) {
            $DestinationConnection.Dispose()
        }
        if ($null -ne $SourceConnection) {
            $SourceConnection.Dispose()
        }
    }

    try {
        Move-Item -LiteralPath $Temporary -Destination $Destination -Force

        foreach ($Suffix in @("-wal", "-shm")) {
            $Sidecar = "$Destination$Suffix"
            if (Test-Path -LiteralPath $Sidecar) {
                Remove-Item -LiteralPath $Sidecar -Force
            }
        }

        $SnapshotInfo = Get-Item -LiteralPath $Destination
        $Hash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash

        return [PSCustomObject]@{
            Length = [Int64]$SourceInfo.Length
            LastWriteTimeUtcTicks = [Int64]$SourceInfo.LastWriteTimeUtc.Ticks
            SnapshotLength = [Int64]$SnapshotInfo.Length
            Sha256 = $Hash.ToLowerInvariant()
            SqliteIntegrity = "ok"
        }
    }
    finally {
        if (Test-Path -LiteralPath $Temporary) {
            Remove-Item -LiteralPath $Temporary -Force
        }
    }
}


function Test-SnapshotTree {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$ManifestPath
    )

    $Manifest = Get-Content -LiteralPath $ManifestPath -Raw |
        ConvertFrom-Json
    $RootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $RootPrefix = $RootPath + '\'
    $Errors = @()
    $CheckedFiles = 0

    foreach ($Entry in @($Manifest.files)) {
        $RelativePath = [string]$Entry.relative_path
        $WindowsRelativePath = $RelativePath.Replace('/', '\')
        $SnapshotPath = [IO.Path]::GetFullPath(
            (Join-Path $RootPath $WindowsRelativePath)
        )

        if (-not $SnapshotPath.StartsWith(
            $RootPrefix,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            $Errors += "路径超出快照根目录：$RelativePath"
            continue
        }

        if (-not (Test-Path -LiteralPath $SnapshotPath -PathType Leaf)) {
            $Errors += "快照文件缺失：$RelativePath"
            continue
        }

        $CheckedFiles++
        $ActualLength = (Get-Item -LiteralPath $SnapshotPath).Length
        if ($ActualLength -ne [Int64]$Entry.snapshot_length) {
            $Errors += "快照长度不一致：$RelativePath"
        }

        $ActualHash = (Get-FileHash -LiteralPath $SnapshotPath -Algorithm SHA256).Hash
        if ($ActualHash -ine [string]$Entry.sha256) {
            $Errors += "快照 SHA-256 不一致：$RelativePath"
        }

        if ($RelativePath -match '(?i)(-wal|-shm)$') {
            $Errors += "快照中不应包含 SQLite 活动副文件：$RelativePath"
        }

        if (-not [string]::IsNullOrWhiteSpace([string]$Entry.sqlite_integrity)) {
            try {
                $Integrity = Get-SqliteIntegrityStatus -Path $SnapshotPath
                if ($Integrity -ine "ok") {
                    $Errors += "SQLite 完整性检查失败：$RelativePath；$Integrity"
                }
            }
            catch {
                $Errors += "SQLite 快照无法回读：$RelativePath；$($_.Exception.Message)"
            }
        }
    }

    if ([bool]$Manifest.contains_deletions) {
        foreach ($DeletedRelativePathValue in @($Manifest.deleted_paths)) {
            if ($null -eq $DeletedRelativePathValue) {
                continue
            }

            $DeletedRelativePath = [string]$DeletedRelativePathValue
            if ([string]::IsNullOrWhiteSpace($DeletedRelativePath)) {
                $Errors += "删除路径为空。"
                continue
            }

            $DeletedPath = [IO.Path]::GetFullPath(
                (Join-Path $RootPath $DeletedRelativePath.Replace('/', '\'))
            )
            if (-not $DeletedPath.StartsWith(
                $RootPrefix,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                $Errors += "删除路径超出快照根目录：$DeletedRelativePath"
            }
        }
    }

    return [PSCustomObject]@{
        Verified = ($Errors.Count -eq 0)
        CheckedFiles = $CheckedFiles
        Errors = $Errors
    }
}


# ============================================================
# 将本轮新增 / 修改文件复制到临时目录
#
# 保持完整相对路径
# ============================================================

Write-Host "正在创建本轮数据快照..."
Write-Host ""


$SnapshotState = @{}

$FailedFiles = @()
$DisappearedDuringSnapshot = @()

$Index = 0


foreach ($Item in $ChangedFiles) {

    $Index++


    $SourceFile =
        $Item.File.FullName


    $DestinationFile =
        Join-Path `
            $StageDir `
            $Item.RelativePath


    Write-Progress `
        -Activity "正在准备增量数据" `
        -Status "$Index / $($ChangedFiles.Count)  $($Item.RelativePath)" `
        -PercentComplete (
            ($Index / $ChangedFiles.Count) * 100
        )


    $Copied = $false


    # --------------------------------------------------------
    # 单文件最多尝试 3 次
    # --------------------------------------------------------

    for ($Attempt = 1; $Attempt -le 3; $Attempt++) {

        try {

            if ($SqliteDatabasePaths.ContainsKey($Item.RelativePath)) {
                $Snapshot = Copy-SqliteOnlineSnapshot `
                    -Source $SourceFile `
                    -Destination $DestinationFile
            }
            else {
                $Snapshot = Copy-FileSnapshot `
                    -Source $SourceFile `
                    -Destination $DestinationFile
            }


            $SnapshotState[$Item.RelativePath] =
                $Snapshot

            # 记录扫描时看到的 WAL 状态。扫描后的新提交会在下一轮再次
            # 触发数据库快照，因此不会把晚于本次捕获的数据误标为已备份。
            if (
                $SqliteDatabasePaths.ContainsKey($Item.RelativePath) -and
                $SqliteWalStateByDatabase.ContainsKey($Item.RelativePath)
            ) {
                $WalRecord = $SqliteWalStateByDatabase[$Item.RelativePath]
                $SnapshotState[$WalRecord.RelativePath] = $WalRecord.State
            }


            $Copied = $true

            break

        }
        catch {
            # 文件在扫描完成后可能被主程序的数据保留/轮转逻辑删除。
            # 一经确认已不存在，就立即按删除处理，无需浪费重试时间。
            if (-not (Test-Path -LiteralPath $SourceFile -PathType Leaf)) {
                if (Test-Path -LiteralPath $DestinationFile -PathType Leaf) {
                    Remove-Item -LiteralPath $DestinationFile -Force
                }
                $DisappearedDuringSnapshot += [PSCustomObject]@{
                    RelativePath = $Item.RelativePath
                    File = $SourceFile
                    Error = $_.Exception.Message
                }
                break
            }

            if ($Attempt -lt 3) {
                Start-Sleep -Seconds 2
            }
            else {
                $FailedFiles +=
                    [PSCustomObject]@{
                        File =
                            $SourceFile

                        Error =
                            $_.Exception.Message
                    }
            }
        }
    }


    if (-not $Copied) {
        if (@($DisappearedDuringSnapshot | Where-Object {
                $_.RelativePath -eq $Item.RelativePath }).Count -gt 0) {
            Write-Warning "文件在扫描后已删除或轮转，按当前源目录状态跳过：$SourceFile"
        }
        else {
            Write-Warning "无法复制：$SourceFile"
        }
    }
}


Write-Progress `
    -Activity "正在准备增量数据" `
    -Completed


if ($DisappearedDuringSnapshot.Count -gt 0) {
    Write-Warning (
        "扫描后有 {0} 个文件被删除或轮转，已跳过并继续完成备份。" -f
        $DisappearedDuringSnapshot.Count
    )
    $DisappearedDuringSnapshot |
        Select-Object RelativePath, File |
        Format-Table -AutoSize

    if ($BackupKind -eq "incremental") {
        $DeletedStatePaths = @(
            (@($DeletedStatePaths) +
                @($DisappearedDuringSnapshot | ForEach-Object { $_.RelativePath })) |
                Select-Object -Unique
        )
    }
}


# ============================================================
# 如果有任何文件复制失败
#
# 不压缩
# 不更新状态
#
# 防止：
#
# 实际没有备份成功
#
# 但状态文件却认为已经备份
# ============================================================

if ($FailedFiles.Count -gt 0) {

    Write-Host ""
    Write-Host "============================================================"
    Write-Warning "存在无法读取的文件，本次备份终止。"
    Write-Host "============================================================"
    Write-Host ""


    $FailedFiles |
        Format-Table File, Error -AutoSize


    Write-Host ""
    Write-Host "临时目录保留："
    Write-Host $StageDir
    Write-Host ""

    exit 2
}

# ============================================================
# 快照内容 manifest：记录源/快照长度、InProgress、SHA-256 和 SQLite 完整性。
# 同时记录本轮删除路径；恢复时按链顺序应用这些删除操作。
# ============================================================
$ArchiveDeletedPaths = if ($BackupKind -eq "base") {
    @()
}
else {
    @(
        $DeletedStatePaths |
            Sort-Object |
            ForEach-Object { $_.Replace('\', '/') }
    )
}

$ArchiveManifestFiles = @()
foreach ($Item in $ChangedFiles) {
    if (-not $SnapshotState.ContainsKey($Item.RelativePath)) { continue }
    $Snapshot = $SnapshotState[$Item.RelativePath]
    $SnapshotPath = Join-Path $StageDir $Item.RelativePath
    $Hash = if ($Snapshot.PSObject.Properties.Name -contains "Sha256") {
        $Snapshot.Sha256
    } else {
        (Get-FileHash -LiteralPath $SnapshotPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $SnapshotLength = (Get-Item -LiteralPath $SnapshotPath).Length
    $IsInProgress = $Item.RelativePath.EndsWith(
        ".csv.tmp", [StringComparison]::OrdinalIgnoreCase)
    $ArchiveManifestFiles += [ordered]@{
        relative_path = $Item.RelativePath.Replace('\', '/')
        source_length = [Int64]$Snapshot.Length
        snapshot_length = [Int64]$SnapshotLength
        sha256 = [string]$Hash
        in_progress = [bool]$IsInProgress
        sqlite_integrity = if ($Snapshot.PSObject.Properties.Name -contains "SqliteIntegrity") {
            [string]$Snapshot.SqliteIntegrity
        } else { $null }
    }
}
$ArchiveManifest = [ordered]@{
    schema_version = 4
    project = $ProjectName
    application_version = $Version
    application_version_source = $ApplicationIdentity.VersionSource
    application_executable = if ($null -eq $ApplicationIdentity.Path) {
        $null
    }
    else {
        Split-Path $ApplicationIdentity.Path -Leaf
    }
    application_file_version = $ApplicationIdentity.FileVersion
    project_runtime_identity_file = if (
        [string]::IsNullOrWhiteSpace($ApplicationIdentity.ProjectIdentityPath)) {
        $null
    } else { "Config/runtime-build-identity.json" }
    project_runtime_identity_captured_utc = if (
        $null -eq $ApplicationIdentity.ProjectIdentityCapturedUtc) {
        $null
    } else { $ApplicationIdentity.ProjectIdentityCapturedUtc.ToUniversalTime().ToString("o") }
    backup_type = $BackupType
    backup_kind = $BackupKind
    backup_id = $BackupId
    backup_sequence = $BackupSequence
    base_backup_id = $BaseBackupId
    previous_backup_id = $PreviousBackupId
    archive_file_name = $ArchiveFileName
    base_reason = $BaseReason
    captured_utc = [DateTime]::UtcNow.ToString("o")
    snapshot_verified = $true
    excludes_active_sqlite_sidecars = $true
    python_required = $false
    contains_deletions = ($ArchiveDeletedPaths.Count -gt 0)
    deleted_paths = $ArchiveDeletedPaths
    source_files_disappeared_during_snapshot = @(
        $DisappearedDuringSnapshot |
            ForEach-Object { $_.RelativePath.Replace('\', '/') } |
            Sort-Object
    )
    files = $ArchiveManifestFiles
}
$ArchiveManifestPath = Join-Path $StageDir "backup-manifest.json"
$ArchiveManifest | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $ArchiveManifestPath -Encoding UTF8

$Verification = Test-SnapshotTree `
    -Root $StageDir `
    -ManifestPath $ArchiveManifestPath

if (-not $Verification.Verified) {
    $VerificationText = $Verification.Errors -join "`n"
    throw "封存快照回读验证失败：`n$VerificationText`nstaging 已保留：$StageDir"
}


# ============================================================
# 7-Zip 压缩
#
# 完全按你的参数：
#
# -mx=9
# -mmt=4
# -mtm=on
# -mtc=on
# -bsp1
#
#
# 关键点：
#
# Push-Location $StageDir
#
# 然后压缩 ".\*"
#
# 这样 7z 中不会多套一层临时 StageDir 文件夹。
#
# 原始目录相对结构仍然保留。
# ============================================================

Write-Host ""
Write-Host "============================================================"
Write-Host " 开始 7-Zip 压缩"
Write-Host "============================================================"
Write-Host ""
Write-Host "备份链："
Write-Host ("本次节点   : {0} {1:D4}" -f $BackupKind, $BackupSequence)
Write-Host "本次 ID     : $BackupId"
Write-Host "基包 ID     : $BaseBackupId"
if (-not [string]::IsNullOrWhiteSpace($PreviousBackupId)) {
    Write-Host "前一节点 ID : $PreviousBackupId"
}
Write-Host ""
Write-Host "压缩包："
Write-Host $ArchivePath
Write-Host ""


$CompressionStart = Get-Date


Push-Location $StageDir


try {

    & $SevenZip `
        a `
        $ArchiveStagingPath `
        ".\*" `
        "-mx=9" `
        "-mmt=4" `
        "-mtm=on" `
        "-mtc=on" `
        "-bsp1"


    $SevenZipExitCode =
        $LASTEXITCODE
}
finally {

    Pop-Location
}


# ============================================================
# 7-Zip Exit Code
#
# 0 = 正常
# 1 = Warning
#
# 为了保证备份状态绝对可靠，
# 这里只接受 0。
# ============================================================

if ($SevenZipExitCode -ne 0) {

    Write-Host ""
    Write-Warning "7-Zip 压缩失败。"
    Write-Warning "ExitCode = $SevenZipExitCode"
    Write-Host ""

    Write-Host "状态文件没有更新。"
    Write-Host "下次运行仍然会重新备份这些数据。"
    Write-Host ""

    Write-Host "临时目录保留："
    Write-Host $StageDir
    Write-Host ""

    exit $SevenZipExitCode
}

# 归档回读；只有测试、哈希/SQLite/CSV 快照校验均通过后才原子发布正式文件名。
& $SevenZip t $ArchiveStagingPath | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "7-Zip 归档回读失败；staging 已保留：$ArchiveStagingPath"
}
Move-Item -LiteralPath $ArchiveStagingPath -Destination $ArchivePath


$CompressionEnd = Get-Date

$CompressionTime =
    $CompressionEnd - $CompressionStart


# ============================================================
# 压缩成功以后才更新状态
#
# 这是整个脚本非常重要的一点。
# ============================================================

foreach ($RelativePath in $SnapshotState.Keys) {

    $PreviousState[$RelativePath] =
        $SnapshotState[$RelativePath]
}


# 删除已经不存在文件的状态
foreach ($Path in $DeletedStatePaths) {

    $PreviousState.Remove($Path)
}


# ============================================================
# 写入 Manifest
#
# 先写 .tmp
#
# 再原子替换正式状态文件
#
# 避免写状态过程中断电导致状态文件损坏。
# ============================================================

$ManifestTemp =
    "$ManifestPath.tmp"


$ManifestRows =
    foreach (
        $Path in (
            $PreviousState.Keys |
                Sort-Object
        )
    ) {

        [PSCustomObject]@{
            RelativePath =
                $Path

            Length =
                $PreviousState[$Path].Length

            LastWriteTimeUtcTicks =
                $PreviousState[$Path].LastWriteTimeUtcTicks
        }
    }


$ManifestRows |
    Export-Csv `
        -LiteralPath $ManifestTemp `
        -NoTypeInformation `
        -Encoding UTF8


Move-Item `
    -LiteralPath $ManifestTemp `
    -Destination $ManifestPath `
    -Force


# ============================================================
# 写入备份链状态
#
# 仅在归档、回读验证和文件状态写入均成功后更新。下次备份据此确定
# 基包和前一个增量包；每个归档内也保存同样的不可变链信息。
# ============================================================

$NextChain = [ordered]@{
    schema_version = 1
    project = $ProjectName
    base_backup_id = $BaseBackupId
    base_sequence = if ($BackupKind -eq "base") {
        $BackupSequence
    }
    else {
        [int]$PreviousChain.base_sequence
    }
    base_archive_file_name = if ($BackupKind -eq "base") {
        $ArchiveFileName
    }
    else {
        [string]$PreviousChain.base_archive_file_name
    }
    last_backup_id = $BackupId
    last_sequence = $BackupSequence
    last_backup_kind = $BackupKind
    last_archive_file_name = $ArchiveFileName
    last_application_version = $Version
    updated_utc = [DateTime]::UtcNow.ToString("o")
}

$ChainTemp = "$ChainPath.tmp"
$NextChain | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $ChainTemp -Encoding UTF8
Move-Item -LiteralPath $ChainTemp -Destination $ChainPath -Force


# ============================================================
# 清理临时目录
# ============================================================

Remove-Item `
    -LiteralPath $StageDir `
    -Recurse `
    -Force


# ============================================================
# 统计压缩结果
# ============================================================

$ArchiveInfo =
    Get-Item -LiteralPath $ArchivePath


$ArchiveSizeGB =
    $ArchiveInfo.Length / 1GB


$ArchiveSizeMB =
    $ArchiveInfo.Length / 1MB


# ============================================================
# 完成
# ============================================================

Write-Host ""
Write-Host "============================================================"
Write-Host " 备份完成"
Write-Host "============================================================"
Write-Host ""

Write-Host "项目名称   : $ProjectName"
Write-Host "程序版本   : $Version"
Write-Host "备份类型   : $BackupType"

Write-Host ""

Write-Host "文件总数   : $($AllSourceFiles.Count)"
Write-Host "本轮文件   : $($ChangedFiles.Count)"

Write-Host (
    "本轮原始   : {0:N2} GB" -f
    $ChangedSizeGB
)

if ($ArchiveSizeGB -ge 1) {

    Write-Host (
        "压缩包大小 : {0:N2} GB" -f
        $ArchiveSizeGB
    )

}
else {

    Write-Host (
        "压缩包大小 : {0:N2} MB" -f
        $ArchiveSizeMB
    )
}


Write-Host (
    "压缩耗时   : {0:hh\:mm\:ss}" -f
    $CompressionTime
)


Write-Host ""
Write-Host "压缩包："
Write-Host $ArchivePath

Write-Host ""
Write-Host "状态文件："
Write-Host $ManifestPath

Write-Host ""
