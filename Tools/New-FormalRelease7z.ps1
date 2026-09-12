<#
.SYNOPSIS
像 Visual Studio 一样执行 Release 重建，并直接生成 7z 包。

.EXAMPLE
.\Tools\New-FormalRelease7z.ps1

.EXAMPLE
.\Tools\New-FormalRelease7z.ps1 -OutputRoot 'D:\MTTFTest-Packages'
#>
[CmdletBinding()]
param(
    [string]$MsBuild = '',
    [string]$SevenZip = '',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$startedUtc = [DateTime]::UtcNow
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repo 'MTTfTest\MTTfTest.csproj'
$releaseDirectory = Join-Path $repo 'MTTfTest\bin\Release'
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
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Resolve-MsBuildPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "指定的 MSBuild 不存在：$resolved"
        }
        return $resolved
    }

    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $found = @(& $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1)
            if ($found.Count -eq 1 -and
                (Test-Path -LiteralPath ([string]$found[0]) -PathType Leaf)) {
                return [IO.Path]::GetFullPath(([string]$found[0]).Trim())
            }
        }
    }

    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return [IO.Path]::GetFullPath($command.Source)
    }
    throw '未找到 MSBuild.exe。请安装 Visual Studio/Build Tools，或使用 -MsBuild 指定路径。'
}

function Resolve-SevenZipPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "指定的 7z.exe 不存在：$resolved"
        }
        return $resolved
    }

    $candidates = @(
        (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source,
        (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) '7-Zip\7z.exe'),
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) '7-Zip\7z.exe')
    )
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not [string]::IsNullOrWhiteSpace([string]$candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw '未找到 7z.exe。请安装 7-Zip，或使用 -SevenZip 指定路径。'
}

function Get-GitText {
    param([string[]]$ArgumentList)
    try {
        $value = @(& git -C $repo @ArgumentList 2>$null)
        if ($LASTEXITCODE -eq 0) { return ((@($value) -join "`n").Trim()) }
    }
    catch { }
    return 'unknown'
}

try {
    Set-Location -LiteralPath $repo
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "主项目不存在：$projectPath"
    }
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = Join-Path $repo 'artifacts\deploy'
    }
    $outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
    [void](New-Item -ItemType Directory -Path $outputRootFull -Force)

    $msbuildPath = Resolve-MsBuildPath $MsBuild
    $sevenZipPath = Resolve-SevenZipPath $SevenZip
    Write-Host 'MTTFTest 简易 Release 打包' -ForegroundColor Green
    Write-Host "项目：$projectPath"
    Write-Host "MSBuild：$msbuildPath"
    Write-Host "7-Zip：$sevenZipPath"
    Write-Host "输出：$outputRootFull"

    Write-Stage '步骤 1/3：执行 VS Release 重建'
    & $msbuildPath $projectPath /restore /t:Rebuild /m:1 `
        /p:Configuration=Release /p:Platform=AnyCPU
    if ($LASTEXITCODE -ne 0) {
        throw "Release 重建失败，MSBuild ExitCode=$LASTEXITCODE"
    }

    $mainExecutable = Join-Path $releaseDirectory 'MTTFTest.exe'
    if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
        throw "编译完成但没有找到主程序：$mainExecutable"
    }
    $version = (Get-Item -LiteralPath $mainExecutable).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "无法从 MTTFTest.exe 识别四段版本号：$version"
    }

    $quickDeploySource = Join-Path $PSScriptRoot 'QuickDeploy'
    if (-not (Test-Path -LiteralPath $quickDeploySource -PathType Container)) {
        throw "维护脚本目录不存在：$quickDeploySource"
    }
    foreach ($file in Get-ChildItem -LiteralPath $quickDeploySource -File) {
        Copy-Item -LiteralPath $file.FullName -Destination $releaseDirectory -Force
    }
    $installerSource = Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'
    $installerDestination = Join-Path $releaseDirectory 'QuickDeploy-Installer.ps1'
    [IO.File]::WriteAllText(
        $installerDestination,
        [IO.File]::ReadAllText($installerSource, [Text.Encoding]::UTF8),
        (New-Object Text.UTF8Encoding($true)))
    foreach ($commandFile in Get-ChildItem -LiteralPath $releaseDirectory -Filter '*.cmd' -File) {
        [IO.File]::WriteAllLines(
            $commandFile.FullName,
            [IO.File]::ReadAllLines($commandFile.FullName, [Text.Encoding]::UTF8),
            $utf8NoBom)
    }
    [void](New-Item -ItemType File -Path `
        (Join-Path $releaseDirectory 'MTTFTest.UnattendedMode.required') -Force)

    $requiredFiles = @(
        'MTTFTest.exe',
        'Controller.dll',
        'Config.dll',
        'MTTFTest.Watchdog.exe',
        'MTTFTest.SessionAgent.exe',
        'MTTFTest.SafetyAgent.exe',
        'MTTFTest.Watchdog.Protocol.dll',
        'Config\TestConfig.xml',
        'QuickDeploy-Installer.ps1',
        '一键安装正式版.cmd',
        '一键修复.cmd',
        '一键卸载.cmd',
        '检查运行状态.cmd',
        '快捷部署说明.md',
        'MTTFTest.UnattendedMode.required'
    )
    $missing = @($requiredFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $releaseDirectory $_) -PathType Leaf)
    })
    if ($missing.Count -gt 0) {
        throw "Release 输出不完整，缺少：$($missing -join ', ')"
    }
    Write-Host "[OK] Release 重建完成，自动识别版本 V$version" -ForegroundColor Green

    Write-Stage '步骤 2/3：压缩并测试 7z'
    $stamp = [DateTime]::Now.ToString('yyyyMMdd_HHmmss')
    $archiveName = "MTTFTest_V${version}_${stamp}.7z"
    $archivePath = [IO.Path]::GetFullPath((Join-Path $outputRootFull $archiveName))
    if (Test-Path -LiteralPath $archivePath) {
        throw "目标压缩包已存在：$archivePath"
    }

    Push-Location $releaseDirectory
    try {
        & $sevenZipPath a -t7z $archivePath '.\*' -mx=5 -mmt=on -sccUTF-8
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip 压缩失败，ExitCode=$LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
    & $sevenZipPath t $archivePath -sccUTF-8
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip 完整性测试失败，ExitCode=$LASTEXITCODE"
    }

    Write-Stage '步骤 3/3：生成哈希和结果摘要'
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashPath = $archivePath + '.sha256'
    [IO.File]::WriteAllText(
        $hashPath,
        "$archiveHash  $archiveName`r`n",
        $utf8NoBom)
    $commit = Get-GitText @('rev-parse', '--short=12', 'HEAD')
    $dirty = (Get-GitText @('status', '--porcelain')) -ne ''
    $archiveInfo = Get-Item -LiteralPath $archivePath
    $result = [ordered]@{
        result = 'PASS'
        productVersion = 'V' + $version
        configuration = 'Release'
        platform = 'x86'
        sourceDirectory = $releaseDirectory
        archive = $archivePath
        archiveBytes = $archiveInfo.Length
        archiveSha256 = $archiveHash
        gitCommit = $commit
        gitDirty = $dirty
        createdUtc = [DateTime]::UtcNow.ToString('O')
        elapsedSeconds = [Math]::Round(([DateTime]::UtcNow - $startedUtc).TotalSeconds, 1)
    }
    $resultPath = $archivePath + '.result.json'
    $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $resultPath -Encoding UTF8

    Write-Host "`n=== 打包成功 ===" -ForegroundColor Green
    Write-Host "版本：V$version"
    Write-Host "7z：$archivePath" -ForegroundColor Green
    Write-Host "SHA-256：$archiveHash"
    Write-Host "哈希文件：$hashPath"
    Write-Host "结果摘要：$resultPath"
    Write-Host "大小：$([Math]::Round($archiveInfo.Length / 1MB, 2)) MiB"
    if ($dirty) {
        Write-Warning '当前 Git 工作区有未提交改动；压缩包已生成，请自行确认是否用于正式交付。'
    }
    Restore-ConsoleEncoding
    exit 0
}
catch {
    Write-Host "`n=== 打包失败 ===" -ForegroundColor Red
    Write-Host "原因：$($_.Exception.Message)" -ForegroundColor Red
    if ($_.InvocationInfo -and $_.InvocationInfo.PositionMessage) {
        Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor DarkRed
    }
    Write-Host '退出码：1；未显示“打包成功”时，请勿使用本轮压缩包。' -ForegroundColor Yellow
    Restore-ConsoleEncoding
    exit 1
}
