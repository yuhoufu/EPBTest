<#
.SYNOPSIS
一键构建、校验并生成 MTTFTest 正式 7z 交付包。

.DESCRIPTION
自动识别 Visual Studio MSBuild 和产品版本，顺序调用 Build-Release.ps1 与
New-QuickDeployBundle.ps1。只有 Release 校验、7z 完整性测试和外层 SHA-256
均成功时，才以退出码 0 结束。

.EXAMPLE
.\Tools\New-FormalRelease7z.ps1

.EXAMPLE
.\Tools\New-FormalRelease7z.ps1 -OutputRoot 'D:\MTTFTest-Deliveries'
#>
[CmdletBinding()]
param(
    [string]$MsBuild = '',
    [string]$OutputRoot = '',
    [switch]$AllowDirtyCandidate
)

$ErrorActionPreference = 'Stop'
$startedUtc = [DateTime]::UtcNow
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previousOutputEncoding = $OutputEncoding
$previousConsoleOutputEncoding = [Console]::OutputEncoding
$previousConsoleInputEncoding = [Console]::InputEncoding
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$OutputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom
[Console]::InputEncoding = $utf8NoBom

function Restore-ConsoleEncoding {
    Set-Variable -Name OutputEncoding -Scope Script -Value $previousOutputEncoding
    [Console]::OutputEncoding = $previousConsoleOutputEncoding
    [Console]::InputEncoding = $previousConsoleInputEncoding
}

function Write-Stage {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Write-Failure {
    param([Parameter(Mandatory = $true)]$ErrorRecord)
    Write-Host "`n=== 交付包生成失败 ===" -ForegroundColor Red
    Write-Host "原因：$($ErrorRecord.Exception.Message)" -ForegroundColor Red
    if ($ErrorRecord.InvocationInfo -and $ErrorRecord.InvocationInfo.PositionMessage) {
        Write-Host $ErrorRecord.InvocationInfo.PositionMessage -ForegroundColor DarkRed
    }
    Write-Host '未显示“交付成功”前，请勿交付 artifacts 目录中的任何新文件。' -ForegroundColor Yellow
}

function Resolve-MsBuildPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $fullPath = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "指定的 MSBuild 不存在：$fullPath"
        }
        return $fullPath
    }

    $candidates = New-Object 'System.Collections.Generic.List[string]'
    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $found = @(& $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null)
            foreach ($item in $found) {
                if (-not [string]::IsNullOrWhiteSpace([string]$item)) {
                    [void]$candidates.Add(([string]$item).Trim())
                }
            }
        }
    }
    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
        [void]$candidates.Add($command.Source)
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw '未找到 MSBuild.exe。请安装 Visual Studio/Build Tools，或使用 -MsBuild 指定完整路径。'
}

function Invoke-ReleaseScript {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$ScriptArguments,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name 脚本不存在：$Path"
    }
    Write-Host "执行：$Path" -ForegroundColor DarkGray
    return @(& $Path @ScriptArguments 2>&1 | ForEach-Object {
        Write-Host ([string]$_)
        $_
    })
}

try {
    Set-Location -LiteralPath $repo
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = Join-Path $repo 'artifacts'
    }
    $outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
    $releaseRoot = Join-Path $outputRootFull 'releases'
    $deployRoot = Join-Path $outputRootFull 'deploy'
    $msbuildPath = Resolve-MsBuildPath $MsBuild

    Write-Host 'MTTFTest 一键正式 7z 交付包' -ForegroundColor Green
    Write-Host "仓库：$repo"
    Write-Host "MSBuild：$msbuildPath"
    Write-Host "输出根目录：$outputRootFull"
    if ($AllowDirtyCandidate) {
        Write-Warning '已启用 AllowDirtyCandidate：生成物仅可用于代码验证，禁止现场交付。'
    }

    Write-Stage '阶段 1/3：构建并校验正式 Release'
    $buildArguments = @{
        MsBuild = $msbuildPath
        PackageRoot = $releaseRoot
    }
    if ($AllowDirtyCandidate) { $buildArguments.AllowDirtyCandidate = $true }
    $buildOutput = Invoke-ReleaseScript -Path (Join-Path $PSScriptRoot 'Build-Release.ps1') `
        -ScriptArguments $buildArguments -Name 'Release 构建'
    $buildSummary = @($buildOutput | ForEach-Object { [string]$_ } |
        Where-Object { $_ -match 'PackageOutput=' } | Select-Object -Last 1)
    if ($buildSummary.Count -ne 1 -or
        $buildSummary[0] -notmatch 'PackageOutput=(.+?)\s+Commit=') {
        throw 'Release 构建未输出可识别的 PackageOutput，拒绝继续压缩。'
    }
    $releaseDirectory = $Matches[1].Trim()
    if (-not (Test-Path -LiteralPath $releaseDirectory -PathType Container)) {
        throw "Release 构建报告的目录不存在：$releaseDirectory"
    }

    $identityPath = Join-Path $releaseDirectory 'build-identity.json'
    if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf)) {
        throw "Release 缺少身份文件：$identityPath"
    }
    $identity = Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$identity.productVersion)) {
        throw 'Release 身份文件未声明 productVersion。'
    }
    Write-Host "[OK] Release 校验通过：$($identity.productVersion)" -ForegroundColor Green
    Write-Host "     ReleaseDirectory=$releaseDirectory"

    Write-Stage '阶段 2/3：生成并回读验证 7z 交付包'
    $bundleOutput = Invoke-ReleaseScript -Path (Join-Path $PSScriptRoot 'New-QuickDeployBundle.ps1') `
        -ScriptArguments @{ ReleaseDirectory = $releaseDirectory; OutputRoot = $deployRoot } `
        -Name '7z 交付包'
    $archiveLine = @($bundleOutput | ForEach-Object { [string]$_ } |
        Where-Object { $_ -match '^7-Zip Ultra 压缩包已生成并通过测试：' } |
        Select-Object -Last 1)
    if ($archiveLine.Count -ne 1) {
        throw '7z 脚本未输出“已生成并通过测试”结果，拒绝报告成功。'
    }
    $archive = $archiveLine[0].Substring($archiveLine[0].IndexOf('：') + 1).Trim()
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "7z 脚本报告的归档不存在：$archive"
    }
    $archiveHashFile = $archive + '.sha256'
    if (-not (Test-Path -LiteralPath $archiveHashFile -PathType Leaf)) {
        throw "7z 外层哈希文件不存在：$archiveHashFile"
    }
    $expectedHash = ((Get-Content -LiteralPath $archiveHashFile -Raw -Encoding UTF8).Trim() -split '\s+')[0].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expectedHash -notmatch '^[0-9a-f]{64}$' -or $actualHash -ne $expectedHash) {
        throw "7z SHA-256 复核失败：Expected=$expectedHash Actual=$actualHash"
    }

    Write-Stage '阶段 3/3：写入交付摘要'
    $archiveInfo = Get-Item -LiteralPath $archive
    $result = [ordered]@{
        result = 'PASS'
        productVersion = [string]$identity.productVersion
        releaseDirectory = $releaseDirectory
        archive = $archive
        archiveBytes = $archiveInfo.Length
        archiveSha256 = $actualHash
        archiveSha256File = $archiveHashFile
        gitCommit = [string]$identity.gitCommit
        buildUtc = [string]$identity.buildUtc
        createdUtc = [DateTime]::UtcNow.ToString('O')
        elapsedSeconds = [Math]::Round(([DateTime]::UtcNow - $startedUtc).TotalSeconds, 1)
        dirtyCandidate = [bool]$identity.gitDirty
    }
    $resultPath = $archive + '.result.json'
    $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $resultPath -Encoding UTF8

    Write-Host "`n=== 交付成功 ===" -ForegroundColor Green
    Write-Host "版本：$($result.productVersion)"
    Write-Host "提交：$($result.gitCommit)"
    Write-Host "7z：$($result.archive)" -ForegroundColor Green
    Write-Host "SHA-256：$($result.archiveSha256)"
    Write-Host "哈希文件：$($result.archiveSha256File)"
    Write-Host "交付摘要：$resultPath"
    Write-Host "大小：$([Math]::Round($result.archiveBytes / 1MB, 2)) MiB；耗时：$($result.elapsedSeconds) 秒"
    Write-Host '请将 .7z 与同名 .sha256 文件成对复制到目标电脑，并在目标电脑本地完整解压后运行。' -ForegroundColor Yellow
    Restore-ConsoleEncoding
    exit 0
}
catch {
    Write-Failure $_
    Restore-ConsoleEncoding
    exit 1
}
