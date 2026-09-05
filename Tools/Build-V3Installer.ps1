[CmdletBinding()]
param(
    [string]$MsBuild = '',
    [string]$SevenZip = '',
    [string]$ReleaseRoot = '',
    [string]$OutputRoot = '',
    [switch]$FormalRelease,
    [string]$FormalApprovalEvidencePath = ''
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repo
$uiGateParameters = @{ RepositoryRoot = $repo }
if (-not $FormalRelease) {
    $uiGateParameters.AllowInstalledUiValidationPending = $true
}
& (Join-Path $PSScriptRoot 'Test-V3OriginalUiReleaseGate.ps1') @uiGateParameters

function Resolve-ExistingExecutable(
    [string]$RequestedPath,
    [string[]]$Candidates,
    [string]$Label) {
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "$Label 不存在：$resolved"
        }
        return $resolved
    }
    foreach ($candidate in @($Candidates | Select-Object -Unique)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw "找不到 $Label；请使用对应参数指定完整路径。"
}

function Assert-FormalApprovalEvidence([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw '生成正式放行包必须提供 -FormalApprovalEvidencePath。'
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "正式批准证据不存在：$resolved"
    }
    $evidence = Get-Content -LiteralPath $resolved -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ([int]$evidence.schemaVersion -ne 1 -or
        -not [bool]$evidence.installedCrashMatrixPassed -or
        -not [bool]$evidence.hardwareMatrixPassed -or
        -not [bool]$evidence.soak168HoursPassed -or
        [string]::IsNullOrWhiteSpace([string]$evidence.approvedBy) -or
        [string]::IsNullOrWhiteSpace([string]$evidence.approvedUtc)) {
        throw '正式批准证据不完整：必须确认安装态强杀矩阵、真实硬件矩阵和 168 小时长稳均通过，并记录批准人和批准时间。'
    }
    $approvedUtc = [DateTime]::MinValue
    if (-not [DateTime]::TryParse(
            [string]$evidence.approvedUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$approvedUtc)) {
        throw '正式批准证据 approvedUtc 不是 ISO-8601 时间。'
    }
    return $resolved
}

$versionProps = Join-Path $repo 'Build\UnattendedVersion.props'
[xml]$versionXml = Get-Content -LiteralPath $versionProps -Raw
$version = ([string]$versionXml.Project.PropertyGroup.UnattendedProductVersion |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1).Trim()
if ($version -ne '3.0.0.0') {
    throw "本脚本只允许打包 V3.0.0.0；当前统一版本为：$version"
}

$gitStatus = @(& git status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 工作区状态。' }
if ($gitStatus.Count -ne 0) {
    throw 'V3 安装包只允许从干净工作区生成；请先提交或移除未提交改动。'
}

$msbuildCandidates = @(
    (Get-Command msbuild.exe -ErrorAction SilentlyContinue).Source,
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
)
$sevenZipCandidates = @(
    (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source,
    (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) '7-Zip\7z.exe'),
    (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) '7-Zip\7z.exe')
)
$msbuildPath = Resolve-ExistingExecutable $MsBuild $msbuildCandidates 'MSBuild.exe'
$sevenZipPath = Resolve-ExistingExecutable $SevenZip $sevenZipCandidates '7z.exe'

if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $ReleaseRoot = Join-Path $repo 'artifacts\releases'
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo 'artifacts\deploy'
}
$releaseRootFull = [IO.Path]::GetFullPath($ReleaseRoot)
$outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
[void](New-Item -ItemType Directory -Path $releaseRootFull -Force)
[void](New-Item -ItemType Directory -Path $outputRootFull -Force)

$formalEvidence = $null
if ($FormalRelease) {
    $formalEvidence = Assert-FormalApprovalEvidence $FormalApprovalEvidencePath
}

$beforeReleaseDirectories = New-Object 'System.Collections.Generic.HashSet[string]' `
    ([StringComparer]::OrdinalIgnoreCase)
foreach ($directory in Get-ChildItem -LiteralPath $releaseRootFull -Directory) {
    [void]$beforeReleaseDirectories.Add($directory.FullName)
}

Write-Host '=== V3 安装包步骤 1/4：严格构建、测试和内层包身份 ===' -ForegroundColor Cyan
$buildParameters = @{
    MsBuild = $msbuildPath
    PackageRoot = $releaseRootFull
}
if (-not $FormalRelease) {
    $buildParameters.FieldValidationCandidate = $true
}
& (Join-Path $PSScriptRoot 'Build-Release.ps1') @buildParameters

$newReleaseDirectories = @(Get-ChildItem -LiteralPath $releaseRootFull -Directory |
    Where-Object { -not $beforeReleaseDirectories.Contains($_.FullName) })
if ($newReleaseDirectories.Count -ne 1) {
    throw "无法唯一确定本次内层发布目录：Count=$($newReleaseDirectories.Count)"
}
$releaseDirectory = $newReleaseDirectories[0].FullName
$identityPath = Join-Path $releaseDirectory 'build-identity.json'
$identity = Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
$expectedReleaseStatus = if ($FormalRelease) {
    'FORMAL_RELEASE'
}
else {
    'FIELD_VALIDATION_CANDIDATE'
}
if ([string]$identity.releaseStatus -ne $expectedReleaseStatus) {
    throw "内层包级别错误：Expected=$expectedReleaseStatus Actual=$($identity.releaseStatus)"
}

Write-Host '=== V3 安装包步骤 2/4：生成 V2.15 同格式快捷安装外层 ===' -ForegroundColor Cyan
$quickParameters = @{
    ReleaseDirectory = $releaseDirectory
    OutputRoot = $outputRootFull
    SevenZip = $sevenZipPath
}
if (-not $FormalRelease) {
    $quickParameters.AllowFieldValidationCandidate = $true
}
& (Join-Path $PSScriptRoot 'New-QuickDeployBundle.ps1') @quickParameters

$shortCommit = ([string]$identity.gitCommit).Substring(0, 12)
$bundleKind = if ($FormalRelease) { '操作员包' } else { '现场验证包' }
$bundleName = "V${version}_${bundleKind}_${shortCommit}_QUICKDEPLOY_R27"
$bundleDirectory = Join-Path $outputRootFull $bundleName
$archive = $bundleDirectory + '.7z'
$archiveHashPath = $archive + '.sha256'
foreach ($required in @(
        $bundleDirectory,
        (Join-Path $bundleDirectory 'Package'),
        (Join-Path $bundleDirectory 'QuickDeploy-Installer.ps1'),
        (Join-Path $bundleDirectory '一键安装正式版.cmd'),
        (Join-Path $bundleDirectory '快捷部署包身份.json'),
        $archive,
        $archiveHashPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "V3 安装包缺少输出：$required"
    }
}

Write-Host '=== V3 安装包步骤 3/4：复核压缩包 SHA-256 ===' -ForegroundColor Cyan
$hashLine = ([IO.File]::ReadAllText($archiveHashPath, [Text.Encoding]::UTF8)).Trim()
$expectedHash = @($hashLine -split '\s+')[0].ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expectedHash -notmatch '^[0-9a-f]{64}$' -or $expectedHash -ne $actualHash) {
    throw "V3 安装包 SHA-256 不一致：Expected=$expectedHash Actual=$actualHash"
}

Write-Host '=== V3 安装包步骤 4/4：写入可回传结果摘要 ===' -ForegroundColor Cyan
$result = [ordered]@{
    schemaVersion = 1
    succeeded = $true
    productVersion = 'V' + $version
    installationClass = $expectedReleaseStatus
    deploymentApproved = [bool]$identity.deploymentApproved
    gitCommit = [string]$identity.gitCommit
    gitBranch = [string]$identity.gitBranch
    gitDirty = [bool]$identity.gitDirty
    releaseDirectory = $releaseDirectory
    bundleDirectory = $bundleDirectory
    archive = $archive
    archiveBytes = (Get-Item -LiteralPath $archive).Length
    archiveSha256 = $actualHash
    formalApprovalEvidence = $formalEvidence
    generatedUtc = [DateTime]::UtcNow.ToString('O')
}
$resultPath = $archive + '.result.json'
[IO.File]::WriteAllText(
    $resultPath,
    ($result | ConvertTo-Json -Depth 4),
    (New-Object Text.UTF8Encoding($false)))

Write-Host '=== V3 安装包生成成功 ===' -ForegroundColor Green
Write-Host "安装包：$archive"
Write-Host "SHA-256：$actualHash"
Write-Host "结果摘要：$resultPath"
if (-not $FormalRelease) {
    Write-Warning '当前为现场验证候选包：可以受控安装验证，但尚未完成正式无人值守放行。'
}
