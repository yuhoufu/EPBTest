param(
    [string]$ReleaseDirectory = '',
    [switch]$RequireDeploymentApproved
)

$ErrorActionPreference = 'Stop'

$expectedAssemblyName = 'MTTFTest'
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

function Get-RequiredJsonProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Scope
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Scope 缺少必要字段：$Name"
    }
    return $property.Value
}

function Assert-CompletePassSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Value
    )

    if ($Value -isnot [string] -or
        $Value -notmatch '^PASS\s+([1-9]\d*)/([1-9]\d*)$') {
        throw "verification.$Name 不是完整通过摘要：$Value"
    }
    if ([long]$Matches[1] -ne [long]$Matches[2]) {
        throw "verification.$Name 存在未通过用例：$Value"
    }
}

function Assert-VerificationEvidence {
    param(
        [Parameter(Mandatory = $true)]$Verification,
        [Parameter(Mandatory = $true)][bool]$FormalCandidate
    )

    if ($null -eq $Verification -or $Verification -isnot [pscustomobject]) {
        throw 'identity.verification 缺失或类型错误。'
    }

    $solutionRebuild = Get-RequiredJsonProperty $Verification 'solutionRebuild' 'identity.verification'
    if ($solutionRebuild -ne 'PASS') {
        throw "verification.solutionRebuild 未通过：$solutionRebuild"
    }

    Assert-CompletePassSummary 'adaptiveControlTests' (
        Get-RequiredJsonProperty $Verification 'adaptiveControlTests' 'identity.verification')
    Assert-CompletePassSummary 'epbDiskWriterTests' (
        Get-RequiredJsonProperty $Verification 'epbDiskWriterTests' 'identity.verification')

    $persistenceSoak = Get-RequiredJsonProperty $Verification 'persistenceSoak' 'identity.verification'
    if ($persistenceSoak -isnot [string] -or $persistenceSoak -ne 'PASS 1/1') {
        throw "verification.persistenceSoak 未通过：$persistenceSoak"
    }
    $soakSeconds = Get-RequiredJsonProperty $Verification 'persistenceSoakSeconds' 'identity.verification'
    if (($soakSeconds -isnot [int] -and $soakSeconds -isnot [long]) -or
        [long]$soakSeconds -lt 1 -or [long]$soakSeconds -gt 3600) {
        throw "verification.persistenceSoakSeconds 非法：$soakSeconds"
    }
    if ($FormalCandidate -and [long]$soakSeconds -lt 600) {
        throw "正式候选持久化浸泡不足 600 秒：$soakSeconds"
    }

    Assert-CompletePassSummary 'powerSupplyDebuggerTests' (
        Get-RequiredJsonProperty $Verification 'powerSupplyDebuggerTests' 'identity.verification')

    $fieldGateSummary = Get-RequiredJsonProperty $Verification 'fieldGateTests' 'identity.verification'
    if ($fieldGateSummary -isnot [string] -or
        $fieldGateSummary -notmatch '^Ran\s+[1-9]\d*\s+tests?\s+in\s+\d+(?:\.\d+)?s$') {
        throw "verification.fieldGateTests 不是完整通过摘要：$fieldGateSummary"
    }
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    if ($RequireDeploymentApproved) {
        throw '正式候选校验必须通过 -ReleaseDirectory 指向独立版本目录；拒绝默认使用可被 VS 覆盖的 bin\Release。'
    }
    $ReleaseDirectory = Join-Path $repo 'MTTfTest\bin\Release'
}
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $release -PathType Container)) {
    throw "Release 目录不存在：$release"
}

$identityPath = Join-Path $release 'build-identity.json'
$checksumPath = Join-Path $release 'SHA256SUMS.txt'
$exePath = Join-Path $release 'MTTFTest.exe'
$watchdogExePath = Join-Path $release 'MTTFTest.Watchdog.exe'
$watchdogProtocolPath = Join-Path $release 'MTTFTest.Watchdog.Protocol.dll'
foreach ($required in @($identityPath, $checksumPath, $exePath, $watchdogExePath, $watchdogProtocolPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Release 缺少必要文件：$required"
    }
}

$identityJson = Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8
$identity = $identityJson | ConvertFrom-Json
$identityFileVersion = Get-RequiredJsonProperty $identity 'fileVersion' 'identity'
$identityAssemblyName = Get-RequiredJsonProperty $identity 'assemblyName' 'identity'
$expectedProductVersion = [string]$identityFileVersion
$expectedProductLabel = 'V' + $expectedProductVersion
if ([string]::IsNullOrWhiteSpace($expectedProductVersion) -or
    $identity.productVersion -ne $expectedProductLabel) {
    throw "identity 产品版本与文件版本不自洽：Product=$($identity.productVersion) File=$identityFileVersion"
}
if ($identityAssemblyName -ne $expectedAssemblyName) {
    throw "identity 程序集名称错误：$identityAssemblyName"
}

$versionInfo = (Get-Item -LiteralPath $exePath).VersionInfo
$actualProductVersion = $versionInfo.ProductVersion
$actualFileVersion = $versionInfo.FileVersion
$actualAssemblyName = [Reflection.AssemblyName]::GetAssemblyName($exePath).Name
if ($actualProductVersion -ne $expectedProductVersion -or
    $actualProductVersion -ne $identity.productVersion.Substring(1)) {
    throw "EXE 产品版本错误：Actual=$actualProductVersion Identity=$($identity.productVersion)"
}
if ($actualFileVersion -ne $expectedProductVersion -or
    $actualFileVersion -ne $identityFileVersion) {
    throw "EXE 文件版本错误：Actual=$actualFileVersion Identity=$identityFileVersion"
}
if ($actualAssemblyName -ne $expectedAssemblyName -or
    $actualAssemblyName -ne $identityAssemblyName) {
    throw "EXE 程序集名称错误：Actual=$actualAssemblyName Identity=$identityAssemblyName"
}
if ($identity.platform -ne 'x86') {
    throw "identity 平台不是 x86：$($identity.platform)"
}

$gitCommit = Get-RequiredJsonProperty $identity 'gitCommit' 'identity'
if ($gitCommit -isnot [string] -or $gitCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "identity.gitCommit 不是完整 40 位提交 SHA：$gitCommit"
}
$buildUtcMatches = [Text.RegularExpressions.Regex]::Matches(
    $identityJson,
    '"buildUtc"\s*:\s*"([^"\\]*)"',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if ($buildUtcMatches.Count -ne 1) {
    throw "identity.buildUtc 必须且只能出现一次：Count=$($buildUtcMatches.Count)"
}
$buildUtcText = $buildUtcMatches[0].Groups[1].Value
$parsedBuildUtc = [DateTimeOffset]::MinValue
$buildUtcValid = $buildUtcText -is [string] -and
    $buildUtcText.EndsWith('Z', [StringComparison]::Ordinal) -and
    [DateTimeOffset]::TryParseExact(
        $buildUtcText,
        'O',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$parsedBuildUtc) -and
    $parsedBuildUtc.Offset -eq [TimeSpan]::Zero
if (-not $buildUtcValid) {
    throw "identity.buildUtc 不是 UTC Round-trip 时间：$buildUtcText"
}
if ($identity.gitDirty -isnot [bool]) {
    throw "identity.gitDirty 必须是 JSON 布尔值：$($identity.gitDirty)"
}
if ($identity.deploymentApproved -isnot [bool]) {
    throw "identity.deploymentApproved 必须是 JSON 布尔值：$($identity.deploymentApproved)"
}

$isFormalCandidate = $identity.releaseStatus -eq 'FORMAL_RELEASE_CANDIDATE'
$stateIsCoherent = switch ([string]$identity.releaseStatus) {
    'FORMAL_RELEASE_CANDIDATE' {
        $identity.deploymentApproved -eq $true -and $identity.gitDirty -eq $false
        break
    }
    'BUILD_STAGING_NOT_FOR_DEPLOYMENT' {
        $identity.deploymentApproved -eq $false -and $identity.gitDirty -eq $false
        break
    }
    'DIRTY_CANDIDATE_NOT_FOR_PRODUCTION' {
        $identity.deploymentApproved -eq $false -and $identity.gitDirty -eq $true
        break
    }
    default { $false }
}
if (-not $stateIsCoherent) {
    throw "identity 放行状态组合非法：Status=$($identity.releaseStatus) " +
          "Approved=$($identity.deploymentApproved) Dirty=$($identity.gitDirty)"
}
if ($RequireDeploymentApproved -and -not $isFormalCandidate) {
    throw "该包不是可部署正式候选：Status=$($identity.releaseStatus) " +
          "Approved=$($identity.deploymentApproved) Dirty=$($identity.gitDirty)"
}

$verification = Get-RequiredJsonProperty $identity 'verification' 'identity'
Assert-VerificationEvidence -Verification $verification -FormalCandidate $isFormalCandidate

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
    fileVersion = $actualFileVersion
    assemblyName = $actualAssemblyName
    releaseStatus = $identity.releaseStatus
    deploymentApproved = [bool]$identity.deploymentApproved
    gitCommit = $identity.gitCommit
    gitDirty = [bool]$identity.gitDirty
    buildUtc = $buildUtcText
    configSha256 = $identity.configSha256
    verification = 'PASS'
    identityFileCount = @($identity.files).Count
    checksumFileCount = $checksums.Count
    exeSha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$result | ConvertTo-Json -Depth 3
