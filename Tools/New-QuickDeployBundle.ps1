[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,
    [string]$OutputRoot = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$bundleRevision = 9
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $release -PathType Container)) {
    throw "候选包目录不存在：$release"
}
$mainExecutable = Join-Path $release 'MTTFTest.exe'
if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
    throw "程序目录缺少 MTTFTest.exe：$release"
}
$identityPath = Join-Path $release 'build-identity.json'
$identity = if (Test-Path -LiteralPath $identityPath -PathType Leaf) {
    Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
} else {
    [pscustomobject]@{
        productVersion = 'V' + (Get-Item -LiteralPath $mainExecutable).VersionInfo.ProductVersion
        releaseStatus = 'OPERATOR_MANAGED'
        deploymentApproved = $true
        gitCommit = 'operator-managed'
        buildUtc = [DateTime]::UtcNow.ToString('O')
        configSha256 = ''
    }
}
$bundleSourceCommit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($bundleSourceCommit)) {
    throw '无法读取快捷部署器源码提交身份。'
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo 'artifacts\deploy'
}
$outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
[void](New-Item -ItemType Directory -Path $outputRootFull -Force)
$identityCommit = [string]$identity.gitCommit
$shortCommit = if ($identityCommit.Length -ge 12) {
    $identityCommit.Substring(0, 12)
} else {
    ($identityCommit -replace '[^0-9A-Za-z._-]', '_')
}
$safeVersion = ([string]$identity.productVersion -replace '[^0-9A-Za-z._-]', '_')
$name = "${safeVersion}_操作员包_${shortCommit}_QUICKDEPLOY_R$bundleRevision"
$output = [IO.Path]::GetFullPath((Join-Path $outputRootFull $name))
$prefix = $outputRootFull.TrimEnd('\', '/') + '\'
if (-not $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "快捷部署输出路径越界：$output"
}
if (Test-Path -LiteralPath $output) {
    if (-not $Force) { throw "快捷部署包已存在：$output" }
    $resolved = [IO.Path]::GetFullPath($output).TrimEnd('\', '/')
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除越界路径：$resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
$staging = Join-Path $outputRootFull ('.quick-staging-' + [Guid]::NewGuid().ToString('N'))
try {
    [void](New-Item -ItemType Directory -Path $staging)
    Copy-Item -LiteralPath $release -Destination (Join-Path $staging 'Package') `
        -Recurse -Force
    $quickSource = Join-Path $PSScriptRoot 'QuickDeploy'
    foreach ($file in Get-ChildItem -LiteralPath $quickSource -File) {
        Copy-Item -LiteralPath $file.FullName -Destination $staging -Force
    }
    # 外层安装器可以独立修复部署逻辑，同时保持已验证的内层程序包完全不可变。
    # 使用 UTF-8 BOM 兼容 Windows PowerShell 5.1；批处理仍显式按 UTF-8 读取。
    $quickInstallerSource = Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'
    $quickInstallerDestination = Join-Path $staging 'QuickDeploy-Installer.ps1'
    [IO.File]::WriteAllText(
        $quickInstallerDestination,
        [IO.File]::ReadAllText($quickInstallerSource, [Text.Encoding]::UTF8),
        (New-Object Text.UTF8Encoding($true)))
    # cmd.exe on older field PCs can split UTF-8 batch commands when the file
    # only contains LF.  Always materialize the outer launchers as UTF-8/CRLF.
    foreach ($commandFile in Get-ChildItem -LiteralPath $staging -Filter '*.cmd' -File) {
        [IO.File]::WriteAllLines(
            $commandFile.FullName,
            [IO.File]::ReadAllLines($commandFile.FullName),
            (New-Object Text.UTF8Encoding($false)))
    }
    $buildUtcText = if ($identity.buildUtc -is [DateTime]) {
        ([DateTime]$identity.buildUtc).ToUniversalTime().ToString('O')
    }
    else {
        [string]$identity.buildUtc
    }
    $identitySummary = [ordered]@{
        schemaVersion = 2
        bundleRevision = $bundleRevision
        bundleSourceCommit = $bundleSourceCommit
        productVersion = [string]$identity.productVersion
        releaseStatus = [string]$identity.releaseStatus
        deploymentApproved = [bool]$identity.deploymentApproved
        packageGitCommit = [string]$identity.gitCommit
        packageBuildUtc = $buildUtcText
        configSha256 = [string]$identity.configSha256
        packageManagement = 'OPERATOR_MANAGED'
    }
    $identitySummary | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath `
        (Join-Path $staging '快捷部署包身份.json') -Encoding UTF8
    $hashLines = Get-ChildItem -LiteralPath $staging -File -Recurse |
        Where-Object { $_.Name -ne '快捷部署包-SHA256.txt' } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($staging.TrimEnd('\').Length + 1).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
    [IO.File]::WriteAllLines(
        (Join-Path $staging '快捷部署包-SHA256.txt'),
        @($hashLines),
        (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $staging -Destination $output
}
catch {
    if (Test-Path -LiteralPath $staging -PathType Container) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    throw
}
Write-Host "快捷部署包已生成：$output"

function Resolve-SevenZipExecutable {
    $candidates = @(
        (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
        (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe')
    )
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw '找不到本机 7-Zip 7z.exe，无法生成 Ultra 交付压缩包。'
}

$sevenZip = Resolve-SevenZipExecutable
$archive = $output + '.7z'
$archiveHashFile = $archive + '.sha256'
foreach ($target in @($archive, $archiveHashFile)) {
    if (Test-Path -LiteralPath $target) {
        if (-not $Force) { throw "快捷部署压缩包已存在：$target" }
        $resolved = [IO.Path]::GetFullPath($target)
        if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝删除越界压缩文件：$resolved"
        }
        Remove-Item -LiteralPath $resolved -Force
    }
}

$bundleDirectoryName = [IO.Path]::GetFileName($output)
Push-Location $outputRootFull
try {
    & $sevenZip a -t7z $archive $bundleDirectoryName `
        -mx=9 -m0=LZMA2:d=128m:fb=273:mf=bt4 `
        -ms=on -mqs=on -mmt=on -myx=9 -sccUTF-8
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip Ultra 压缩失败：Exit=$LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

& $sevenZip t $archive -sccUTF-8
if ($LASTEXITCODE -ne 0) {
    throw "7-Zip 完整性测试失败：Exit=$LASTEXITCODE"
}
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    $archiveHashFile,
    "$archiveHash  $([IO.Path]::GetFileName($archive))`r`n",
    (New-Object Text.UTF8Encoding($false)))
Write-Host "7-Zip Ultra 压缩包已生成并通过测试：$archive"
Write-Host "ArchiveBytes=$((Get-Item -LiteralPath $archive).Length) SHA256=$archiveHash"
