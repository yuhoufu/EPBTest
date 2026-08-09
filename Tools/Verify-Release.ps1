param(
    [string]$ReleaseDirectory = '',
    [switch]$RequireDeploymentApproved
)

$ErrorActionPreference = 'Stop'

$expectedProductVersion = '2.12.0.20'
$expectedProductLabel = 'V2.12.0.20'
$expectedPublishedConfigs = @(
    'Config/AIConfig.xml',
    'Config/AlarmConfig.xml',
    'Config/AOConfig.xml',
    'Config/DOConfig.xml',
    'Config/PowerSupplyConfig.xml',
    'Config/TestConfig.xml',
    'Config/UIConfig.xml'
)

function New-OrdinalPathMap {
    return New-Object 'System.Collections.Generic.SortedDictionary[string,string]' `
        ([StringComparer]::Ordinal)
}

function Get-SafePackagePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ([IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "发布清单包含不安全相对路径：$RelativePath"
    }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath.Replace('/', '\')))
    $rootPrefix = $Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "发布清单路径越界：$RelativePath"
    }
    return $fullPath
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

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $repo 'MTTfTest\bin\Release'
}
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $release -PathType Container)) {
    throw "Release 目录不存在：$release"
}

$identityPath = Join-Path $release 'build-identity.json'
$checksumPath = Join-Path $release 'SHA256SUMS.txt'
$exePath = Join-Path $release 'MTTFTest.exe'
foreach ($required in @($identityPath, $checksumPath, $exePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Release 缺少必要文件：$required"
    }
}

$identity = Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($identity.productVersion -ne $expectedProductLabel) {
    throw "identity 产品版本错误：$($identity.productVersion)"
}
$actualProductVersion = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
if ($actualProductVersion -ne $expectedProductVersion) {
    throw "EXE 产品版本错误：$actualProductVersion"
}
if ($identity.platform -ne 'x86') {
    throw "identity 平台不是 x86：$($identity.platform)"
}
if ($RequireDeploymentApproved -and
    ($identity.deploymentApproved -ne $true -or $identity.gitDirty -ne $false -or
     $identity.releaseStatus -ne 'FORMAL_RELEASE_CANDIDATE')) {
    throw "该包不是可部署正式候选：Status=$($identity.releaseStatus) " +
          "Approved=$($identity.deploymentApproved) Dirty=$($identity.gitDirty)"
}

$manifestNames = New-Object 'System.Collections.Generic.HashSet[string]' `
    ([StringComparer]::OrdinalIgnoreCase)
foreach ($file in @($identity.files)) {
    if ([string]::IsNullOrWhiteSpace($file.name) -or
        $file.name -in @('build-identity.json', 'SHA256SUMS.txt')) {
        throw "identity 包含非法/自引用文件项：$($file.name)"
    }
    if (-not $manifestNames.Add([string]$file.name)) {
        throw "identity 文件名重复：$($file.name)"
    }
    $path = Get-SafePackagePath -Root $release -RelativePath $file.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "identity 文件缺失：$($file.name)"
    }
    $actualLength = (Get-Item -LiteralPath $path).Length
    if ($actualLength -ne [long]$file.bytes) {
        throw "identity 文件长度不匹配：$($file.name)"
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$file.sha256).ToLowerInvariant()) {
        throw "identity 文件哈希不匹配：$($file.name)"
    }
}

$actualManifestFiles = Get-RecursivePackageFiles -Root $release `
    -ExcludedRelativePaths @('build-identity.json', 'SHA256SUMS.txt')
foreach ($name in $actualManifestFiles.Keys) {
    if (-not $manifestNames.Contains($name)) { throw "identity 缺少文件：$name" }
}
if ($manifestNames.Count -ne $actualManifestFiles.Count) {
    throw "identity 文件数量不匹配：Manifest=$($manifestNames.Count) Actual=$($actualManifestFiles.Count)"
}

$publishedConfigs = New-OrdinalPathMap
$publishedConfigDirectory = Join-Path $release 'Config'
foreach ($file in Get-ChildItem -LiteralPath $publishedConfigDirectory -Filter '*.xml' -File) {
    $publishedConfigs.Add('Config/' + $file.Name, $file.FullName)
}
$missingConfigs = @($expectedPublishedConfigs |
    Where-Object { -not $publishedConfigs.ContainsKey($_) })
$unexpectedConfigs = @($publishedConfigs.Keys |
    Where-Object { $_ -notin $expectedPublishedConfigs })
if ($missingConfigs.Count -ne 0 -or $unexpectedConfigs.Count -ne 0 -or
    $publishedConfigs.Count -ne $expectedPublishedConfigs.Count) {
    throw "发布配置集合不合规：Missing=$($missingConfigs -join ',') " +
          "Unexpected=$($unexpectedConfigs -join ',')"
}
$actualConfigHash = Get-AggregateFileHash $publishedConfigs
if ($actualConfigHash -ne ([string]$identity.configSha256).ToLowerInvariant()) {
    throw "发布配置聚合哈希不匹配：Identity=$($identity.configSha256) Actual=$actualConfigHash"
}

$checksums = New-Object 'System.Collections.Generic.Dictionary[string,string]' `
    ([StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $checksumPath -Encoding UTF8) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') {
        throw "SHA256SUMS 格式错误：$line"
    }
    $name = $Matches[2]
    if ($checksums.ContainsKey($name)) { throw "SHA256SUMS 文件名重复：$name" }
    $path = Get-SafePackagePath -Root $release -RelativePath $name
    $checksums.Add($name, $Matches[1].ToLowerInvariant())
}
$expectedChecksumFiles = Get-RecursivePackageFiles -Root $release `
    -ExcludedRelativePaths @('SHA256SUMS.txt')
$expectedChecksumNames = @($expectedChecksumFiles.Keys)
foreach ($name in $expectedChecksumNames) {
    if (-not $checksums.ContainsKey($name)) { throw "SHA256SUMS 缺少文件：$name" }
    $actualHash = (Get-FileHash -LiteralPath $expectedChecksumFiles[$name] -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $checksums[$name]) { throw "SHA256SUMS 哈希不匹配：$name" }
}
foreach ($name in $checksums.Keys) {
    if ($name -notin $expectedChecksumNames) { throw "SHA256SUMS 存在多余文件：$name" }
}

$result = [ordered]@{
    verified = $true
    productVersion = $identity.productVersion
    releaseStatus = $identity.releaseStatus
    deploymentApproved = [bool]$identity.deploymentApproved
    gitCommit = $identity.gitCommit
    gitDirty = [bool]$identity.gitDirty
    buildUtc = $identity.buildUtc
    configSha256 = $identity.configSha256
    identityFileCount = @($identity.files).Count
    checksumFileCount = $checksums.Count
    exeSha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$result | ConvertTo-Json -Depth 3
