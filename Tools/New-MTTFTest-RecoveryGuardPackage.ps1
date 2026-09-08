#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$MainReleaseDirectory = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Get-GuardPackageSourceSnapshot([string]$Repository) {
    $paths = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
    foreach ($relative in @('Build/UnattendedVersion.props', 'Tools/Install-MTTFTest-RecoveryGuard.ps1',
        'Tools/RecoveryGuard-Acceptance.ps1', 'Tools/New-MTTFTest-RecoveryGuardAutomaticPackage.ps1',
        'Tools/RecoveryGuard-Archive.ps1',
        'Tools/Invoke-MTTFTest-RecoveryGuardCommissioning.ps1', 'Tools/Verify-Release.ps1',
        'Tools/New-MTTFTest-RecoveryGuardPackage.ps1', 'docs/RecoveryGuard_安装包使用说明.md',
        'MTTFTest.RecoveryGuard/guard-settings.example.json')) { [void]$paths.Add($relative) }
    foreach ($project in @('MTTFTest.RecoveryGuard/MTTFTest.RecoveryGuard.csproj', 'RecoveryControl/RecoveryControl.csproj')) {
        [void]$paths.Add($project)
        [xml]$projectXml = Get-Content -LiteralPath (Join-Path $Repository $project) -Raw -Encoding UTF8
        foreach ($reference in $projectXml.Project.ItemGroup.ProjectReference) {
            if ($null -ne $reference -and [string]$reference.Include -ne '..\RecoveryControl\RecoveryControl.csproj') {
                throw '源码快照发现未覆盖的项目依赖。'
            }
        }
        foreach ($item in $projectXml.Project.ItemGroup.Compile) {
            $include = [string]$item.Include
            if ([string]::IsNullOrWhiteSpace($include)) { continue }
            if ($include.Contains('*') -or $include.Contains('$') -or [IO.Path]::IsPathRooted($include)) { throw '源码快照不支持动态 Compile 路径。' }
            $full = [IO.Path]::GetFullPath((Join-Path (Split-Path (Join-Path $Repository $project) -Parent) $include))
            $prefix = [IO.Path]::GetFullPath($Repository).TrimEnd('\') + '\'
            if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '源码快照路径越界。' }
            [void]$paths.Add($full.Substring($prefix.Length).Replace('\', '/'))
        }
    }
    $files = @(); $canonical = New-Object Text.StringBuilder
    foreach ($relative in $paths) {
        $file = Get-Item -LiteralPath (Join-Path $Repository $relative) -ErrorAction Stop
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $files += [ordered]@{ path = $relative; bytes = $file.Length; sha256 = $hash }
        [void]$canonical.Append($relative).Append("`t").Append($hash).Append("`n")
    }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical.ToString()))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
    return [ordered]@{ fingerprint = $fingerprint; files = $files }
}
$sourceSnapshot = Get-GuardPackageSourceSnapshot $repo
[xml]$versionSource = Get-Content -LiteralPath (Join-Path $repo 'Build\UnattendedVersion.props')
$version = [string]$versionSource.Project.PropertyGroup.UnattendedProductVersion
$sharedSource = $null
if (-not [string]::IsNullOrWhiteSpace($MainReleaseDirectory)) {
    if ($Configuration -ne 'Release' -or $SkipBuild) { throw '同源Main共享组件要求Release实际构建。' }
    $MainReleaseDirectory = [IO.Path]::GetFullPath($MainReleaseDirectory)
    & (Join-Path $PSScriptRoot 'Verify-Release.ps1') -ReleaseDirectory $MainReleaseDirectory | Out-Null
    $mainIdentity = Get-Content (Join-Path $MainReleaseDirectory 'build-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $sourceCommit = ([string](& git -C $repo rev-parse HEAD)).Trim()
    if ($mainIdentity.gitCommit -ne $sourceCommit -or $mainIdentity.gitDirty -ne $false -or
        $mainIdentity.fileVersion -ne $version -or @(& git -C $repo status --porcelain).Count -ne 0) {
        throw '共享组件必须来自当前干净提交的同版本Main正式包。'
    }
    $sharedSource = Join-Path $MainReleaseDirectory 'MTTFTest.RecoveryControl.dll'
    $sharedSourceHash = (Get-FileHash -LiteralPath $sharedSource).Hash
}
if (-not $SkipBuild) {
    $msbuild = 'D:\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe'
    if (-not (Test-Path -LiteralPath $msbuild)) { $msbuild = (Get-Command msbuild.exe -ErrorAction Stop).Source }
    & $msbuild (Join-Path $repo 'MTTFTest.RecoveryGuard\MTTFTest.RecoveryGuard.csproj') /t:Rebuild /p:Configuration=$Configuration /p:Platform=AnyCPU /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'RecoveryGuard 独立构建失败。' }
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo ('artifacts\RecoveryGuard-' + $version + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output + '.zip'))) { throw '输出已存在；请选择新的目录，不覆盖旧交付物。' }
$binaryRoot = Join-Path $repo "MTTFTest.RecoveryGuard\bin\$Configuration"
foreach ($name in @('MTTFTest.RecoveryGuard.exe', 'MTTFTest.RecoveryControl.dll')) {
    $binary = Get-Item -LiteralPath (Join-Path $binaryRoot $name)
    if ($binary.VersionInfo.FileVersion -ne $version) { throw "组件版本不匹配：$name" }
    if (-not (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($binary.FullName, '.pdb')) -PathType Leaf)) { throw "组件调试符号缺失：$name" }
}
[void](New-Item -ItemType Directory -Path $output)
foreach ($name in @('MTTFTest.RecoveryGuard.exe', 'MTTFTest.RecoveryControl.dll')) {
    $source = Join-Path $binaryRoot $name
    if ($name -eq 'MTTFTest.RecoveryControl.dll' -and $null -ne $sharedSource) { $source = $sharedSource }
    Copy-Item -LiteralPath $source -Destination $output
    Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($source, '.pdb')) -Destination $output
}
if ($null -ne $sharedSource) {
    & (Join-Path $PSScriptRoot 'Verify-Release.ps1') -ReleaseDirectory $MainReleaseDirectory | Out-Null
    if ((Get-FileHash -LiteralPath (Join-Path $output 'MTTFTest.RecoveryControl.dll')).Hash -ne $sharedSourceHash) {
        throw '复制后的共享组件不匹配已校验Main。'
    }
}
Copy-Item -LiteralPath (Join-Path $repo 'MTTFTest.RecoveryGuard\guard-settings.example.json') -Destination (Join-Path $output 'guard-settings.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-MTTFTest-RecoveryGuard.ps1') -Destination $output
foreach ($helper in @('Invoke-MTTFTest-RecoveryGuardCommissioning.ps1','Verify-Release.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $helper) -Destination $output
}
Copy-Item -LiteralPath (Join-Path $repo 'docs\RecoveryGuard_安装包使用说明.md') -Destination (Join-Path $output 'README.md')
$afterSnapshot = Get-GuardPackageSourceSnapshot $repo
if ($afterSnapshot.fingerprint -cne $sourceSnapshot.fingerprint) { throw '打包期间源码或安装输入发生变化，拒绝生成混合版本清单。' }
$files = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ name = $_.Name; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$identity = [ordered]@{
    schemaVersion = 2; product = 'MTTFTest.RecoveryGuard'; version = $version
    configuration = $Configuration; deliveryStage = 'ObserveOnlyCommissioning'; automaticExecutionReady = $false
    supervisedCommissioningAvailable = $true
    gitCommit = [string](& git -C $repo rev-parse HEAD)
    gitDirty = [bool](@(& git -C $repo status --porcelain).Count)
    sourceSnapshot = $sourceSnapshot; builtFromVerifiedInputs = -not [bool]$SkipBuild
    sharedComponentSource = $MainReleaseDirectory
    builtUtc = [DateTime]::UtcNow.ToString('O'); files = $files
}
$identity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'guard-identity.json') -Encoding UTF8
& (Join-Path $output 'Install-MTTFTest-RecoveryGuard.ps1') -Mode Validate -SourceDirectory $output
Compress-Archive -Path (Join-Path $output '*') -DestinationPath ($output + '.zip') -CompressionLevel Optimal
[ordered]@{ directory = $output; archive = $output + '.zip'; sha256 = (Get-FileHash -LiteralPath ($output + '.zip') -Algorithm SHA256).Hash.ToLowerInvariant(); deliveryStage = $identity.deliveryStage } | ConvertTo-Json
