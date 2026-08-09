param(
    [string]$MsBuild = 'D:\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe',
    [switch]$AllowDirtyCandidate
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repo

$expectedProductVersion = '2.12.0.22'
$expectedProductLabel = 'V2.12.0.22'
$expectedPublishedConfigs = @(
    'Config/AIConfig.xml',
    'Config/AlarmConfig.xml',
    'Config/AOConfig.xml',
    'Config/DOConfig.xml',
    'Config/PowerSupplyConfig.xml',
    'Config/TestConfig.xml',
    'Config/UIConfig.xml'
)

$dirty = @(git status --porcelain)
if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 状态。' }
$isDirty = $dirty.Count -ne 0
if ($isDirty -and -not $AllowDirtyCandidate) {
    throw "拒绝生成现场包：源码树不是干净状态。`n$($dirty -join "`n")"
}

$commit = (git rev-parse HEAD).Trim()
$branch = (git branch --show-current).Trim()
if ([string]::IsNullOrWhiteSpace($branch)) { $branch = 'detached' }
$buildUtc = [DateTime]::UtcNow.ToString('O')
$gitDirtyText = $isDirty.ToString().ToLowerInvariant()

function Assert-LegacyCompileItems {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRelativePath,
        [Parameter(Mandatory = $true)][string[]]$RequiredItems
    )

    $projectPath = Join-Path $repo $ProjectRelativePath
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "旧式项目文件不存在：$ProjectRelativePath"
    }

    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
    $compileItems = @($projectXml.Project.ItemGroup.Compile |
        ForEach-Object { [string]$_.Include } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $projectDirectory = Split-Path -Parent $projectPath

    foreach ($requiredItem in $RequiredItems) {
        $sourcePath = Join-Path $projectDirectory $requiredItem
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "关键源码文件不存在：$ProjectRelativePath -> $requiredItem"
        }
        if ($compileItems -notcontains $requiredItem) {
            throw "关键源码未加入旧式项目 Compile 清单：$ProjectRelativePath -> $requiredItem"
        }
    }
}

function New-OrdinalPathMap {
    return New-Object 'System.Collections.Generic.SortedDictionary[string,string]' `
        ([StringComparer]::Ordinal)
}

function Get-AggregateFileHash {
    param([Parameter(Mandatory = $true)]$PathMap)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = New-Object IO.MemoryStream
    try {
        foreach ($entry in $PathMap.GetEnumerator()) {
            $nameBytes = [Text.Encoding]::UTF8.GetBytes(
                $entry.Key.ToLowerInvariant() + "`n")
            $stream.Write($nameBytes, 0, $nameBytes.Length)
            $bytes = [IO.File]::ReadAllBytes($entry.Value)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.WriteByte(10)
        }
        $stream.Position = 0
        return -join ($algorithm.ComputeHash($stream) |
            ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Get-RecursivePackageFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string[]]$ExcludedRelativePaths = @()
    )

    $map = New-OrdinalPathMap
    $excluded = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $ExcludedRelativePaths) { [void]$excluded.Add($name) }
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        $relative = $file.FullName.Substring($Root.TrimEnd('\', '/').Length).TrimStart('\', '/').Replace('\', '/')
        if ($excluded.Contains($relative)) { continue }
        if ($map.ContainsKey($relative)) { throw "发布相对路径重复：$relative" }
        $map.Add($relative, $file.FullName)
    }
    return ,$map
}

# 这些安全、持续运行和背压类位于旧式非 SDK 项目中。目录里存在 .cs 并不代表会参与
# 编译；在正式构建前显式检查，避免 VS 缓存或手工编辑再次生成“类型不存在”的假版本。
Assert-LegacyCompileItems -ProjectRelativePath 'Controller\Controller.csproj' -RequiredItems @(
    'EpbManager.FieldMetrics.cs',
    'LatestPairMailbox.cs',
    'UiCurveContinuityPolicy.cs',
    'UiLogDisplayPolicy.cs',
    'TaskSupervisor.cs'
)
Assert-LegacyCompileItems -ProjectRelativePath 'IO.NI\IO.NI.csproj' -RequiredItems @(
    'CoalescingTaskSupervisor.cs',
    'HostRuntimeProbe.cs'
)
Assert-LegacyCompileItems -ProjectRelativePath 'MTTfTest\MTTfTest.csproj' -RequiredItems @(
    'Editors\ToggleButton.cs'
)

$mainProjectPath = Join-Path $repo 'MTTfTest\MTTfTest.csproj'
[xml]$mainProjectXml = Get-Content -LiteralPath $mainProjectPath -Raw
$mainProjectDirectory = Split-Path -Parent $mainProjectPath
$configSources = New-OrdinalPathMap
foreach ($contentItem in @($mainProjectXml.Project.ItemGroup.Content)) {
    $copyMode = [string]$contentItem.CopyToOutputDirectory
    if ([string]::IsNullOrWhiteSpace($copyMode)) { continue }
    $include = [Uri]::UnescapeDataString([string]$contentItem.Include)
    if (-not $include.StartsWith('Config\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $include.EndsWith('.xml', [StringComparison]::OrdinalIgnoreCase)) { continue }
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $mainProjectDirectory $include))
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "项目声明发布的配置不存在：$include"
    }
    $relativePath = $include.Replace('\', '/')
    if ($configSources.ContainsKey($relativePath)) {
        throw "项目发布配置相对路径重复：$relativePath"
    }
    $configSources.Add($relativePath, $sourcePath)
}
if ($configSources.Count -eq 0) {
    throw 'MTTfTest.csproj 中没有声明复制到输出目录的 Config XML。'
}
$missingConfigs = @($expectedPublishedConfigs |
    Where-Object { -not $configSources.ContainsKey($_) })
$unexpectedConfigs = @($configSources.Keys |
    Where-Object { $_ -notin $expectedPublishedConfigs })
if ($missingConfigs.Count -ne 0 -or $unexpectedConfigs.Count -ne 0 -or
    $configSources.Count -ne $expectedPublishedConfigs.Count) {
    throw "项目发布配置集合不合规：Missing=$($missingConfigs -join ',') " +
          "Unexpected=$($unexpectedConfigs -join ',')"
}
$configHash = Get-AggregateFileHash $configSources

if (-not (Test-Path -LiteralPath $MsBuild -PathType Leaf)) {
    throw "MSBuild 不存在：$MsBuild"
}

# 现场包只由主程序及其项目依赖组成。解决方案还包含独立的
# PowerSupplyDebugger 工具，其 RuntimeIdentifier 不属于现场 x86 主程序包。
# 必须从空目录构建：MSBuild Rebuild 只清理仍在项目清单内的输出，已经取消的
# 旧依赖 DLL 会原样残留并被后续哈希清单误收录。
$output = [IO.Path]::GetFullPath((Join-Path $repo 'MTTfTest\bin\Release'))
$expectedOutput = [IO.Path]::GetFullPath((Join-Path $repo 'MTTfTest\bin\Release'))
if (-not [string]::Equals($output, $expectedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝清理非预期发布目录：$output"
}
if (Test-Path -LiteralPath $output -PathType Container) {
    Get-ChildItem -LiteralPath $output -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
}
$identityPath = Join-Path $output 'build-identity.json'
$checksumPath = Join-Path $output 'SHA256SUMS.txt'

& $MsBuild (Join-Path $repo 'MTTfTest\MTTfTest.csproj') /t:Rebuild /m `
    /p:Configuration=Release /p:Platform=AnyCPU `
    "/p:GitCommit=$commit" "/p:GitBranch=$branch" "/p:GitDirty=$gitDirtyText" `
    "/p:BuildUtc=$buildUtc" "/p:ReleaseConfigSha256=$configHash"
if ($LASTEXITCODE -ne 0) { throw "Release 构建失败：$LASTEXITCODE" }

$exePath = Join-Path $output 'MTTFTest.exe'
$actualProductVersion = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
if ($actualProductVersion -ne $expectedProductVersion) {
    throw "版本身份不一致：期望 $expectedProductVersion，实际 $actualProductVersion"
}

$publishedConfigs = New-OrdinalPathMap
$publishedConfigDirectory = Join-Path $output 'Config'
foreach ($file in Get-ChildItem -LiteralPath $publishedConfigDirectory -Filter '*.xml' -File) {
    $relativePath = 'Config/' + $file.Name
    $publishedConfigs.Add($relativePath, $file.FullName)
}
$missingOutputConfigs = @($expectedPublishedConfigs |
    Where-Object { -not $publishedConfigs.ContainsKey($_) })
$unexpectedOutputConfigs = @($publishedConfigs.Keys |
    Where-Object { $_ -notin $expectedPublishedConfigs })
if ($missingOutputConfigs.Count -ne 0 -or $unexpectedOutputConfigs.Count -ne 0 -or
    $publishedConfigs.Count -ne $expectedPublishedConfigs.Count) {
    throw "输出配置集合不合规：Missing=$($missingOutputConfigs -join ',') " +
          "Unexpected=$($unexpectedOutputConfigs -join ',')"
}
$publishedConfigHash = Get-AggregateFileHash $publishedConfigs
if ($publishedConfigHash -ne $configHash) {
    throw "发布配置与编译身份不一致：Source=$configHash Output=$publishedConfigHash"
}

$files = Get-RecursivePackageFiles -Root $output `
    -ExcludedRelativePaths @('build-identity.json', 'SHA256SUMS.txt')
$manifestFiles = foreach ($entry in $files.GetEnumerator()) {
    $file = Get-Item -LiteralPath $entry.Value
    [ordered]@{
        name = $entry.Key
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$identity = [ordered]@{
    productVersion = $expectedProductLabel
    releaseStatus = if ($isDirty) { 'DIRTY_CANDIDATE_NOT_FOR_PRODUCTION' } else { 'FORMAL_RELEASE_CANDIDATE' }
    deploymentApproved = -not $isDirty
    gitCommit = $commit
    gitBranch = $branch
    gitDirty = $isDirty
    buildUtc = $buildUtc
    configSha256 = $configHash
    platform = 'x86'
    files = @($manifestFiles)
}
$identity | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $identityPath -Encoding UTF8
$checksumFileMap = Get-RecursivePackageFiles -Root $output `
    -ExcludedRelativePaths @('SHA256SUMS.txt')
$checksumFiles = @($checksumFileMap.GetEnumerator() |
    ForEach-Object {
        [ordered]@{
            name = $_.Key
            sha256 = (Get-FileHash -LiteralPath $_.Value -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
$checksumLines = @($checksumFiles |
    ForEach-Object { "$($_.sha256)  $($_.name)" })
[IO.File]::WriteAllLines(
    $checksumPath,
    $checksumLines,
    (New-Object Text.UTF8Encoding($false)))

if ($isDirty) {
    Write-Warning "已生成 DIRTY CANDIDATE：仅用于当前代码现场验证，不得作为正式生产放行包。"
}
else {
    Write-Host "Release 正式候选包已生成：$output"
}
Write-Host "Output=$output Commit=$commit Branch=$branch Dirty=$isDirty ConfigSha256=$configHash BuildUtc=$buildUtc"
