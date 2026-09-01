[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,
    [string]$OutputRoot = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $release -PathType Container)) {
    throw "候选包目录不存在：$release"
}
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') `
    -ReleaseDirectory $release -RequireDeploymentApproved | Out-Null
$identity = Get-Content -LiteralPath (Join-Path $release 'build-identity.json') `
    -Raw | ConvertFrom-Json
if ([string]$identity.releaseStatus -ne 'FIELD_CANDIDATE_PENDING_168H') {
    throw "快捷部署只接受现场候选包：$($identity.releaseStatus)"
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo 'artifacts\deploy'
}
$outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
[void](New-Item -ItemType Directory -Path $outputRootFull -Force)
$shortCommit = ([string]$identity.gitCommit).Substring(0, 12)
$name = "V2.14.0.0_快捷部署包_${shortCommit}_FIELD_CANDIDATE"
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
    $identitySummary = [ordered]@{
        schemaVersion = 1
        productVersion = [string]$identity.productVersion
        releaseStatus = [string]$identity.releaseStatus
        deploymentApproved = [bool]$identity.deploymentApproved
        gitCommit = [string]$identity.gitCommit
        buildUtc = [string]$identity.buildUtc
        configSha256 = [string]$identity.configSha256
        innerBuildIdentitySha256 = (Get-FileHash -LiteralPath `
            (Join-Path $staging 'Package\build-identity.json') -Algorithm SHA256).Hash.ToLowerInvariant()
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
