param(
    [string]$MsBuild = 'D:\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe'
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repo

$dirty = @(git status --porcelain)
if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 状态。' }
if ($dirty.Count -ne 0) {
    throw "拒绝生成现场包：源码树不是干净状态。`n$($dirty -join "`n")"
}

$commit = (git rev-parse HEAD).Trim()
$branch = (git branch --show-current).Trim()
if ([string]::IsNullOrWhiteSpace($branch)) { $branch = 'detached' }
$buildUtc = [DateTime]::UtcNow.ToString('O')

$configFiles = @(Get-ChildItem -LiteralPath (Join-Path $repo 'MTTfTest\Config') -Filter '*.xml' -File |
    Sort-Object Name)
if ($configFiles.Count -eq 0) {
    throw '现场配置目录 MTTfTest\Config 中没有可纳入版本身份的 XML。'
}
$sha = [Security.Cryptography.SHA256]::Create()
$configStream = New-Object IO.MemoryStream
try {
    foreach ($file in $configFiles) {
        $nameBytes = [Text.Encoding]::UTF8.GetBytes($file.Name.ToLowerInvariant() + "`n")
        $configStream.Write($nameBytes, 0, $nameBytes.Length)
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $configStream.Write($bytes, 0, $bytes.Length)
        $configStream.WriteByte(10)
    }
    $configStream.Position = 0
    $configHash = -join ($sha.ComputeHash($configStream) | ForEach-Object { $_.ToString('x2') })
}
finally {
    $configStream.Dispose()
    $sha.Dispose()
}

if (-not (Test-Path -LiteralPath $MsBuild -PathType Leaf)) {
    throw "MSBuild 不存在：$MsBuild"
}

# 现场包只由主程序及其项目依赖组成。解决方案还包含独立的
# PowerSupplyDebugger 工具，其 RuntimeIdentifier 不属于现场 x86 主程序包。
& $MsBuild (Join-Path $repo 'MTTfTest\MTTfTest.csproj') /t:Rebuild /m `
    /p:Configuration=Release /p:Platform=AnyCPU `
    "/p:GitCommit=$commit" "/p:GitBranch=$branch" /p:GitDirty=false `
    "/p:BuildUtc=$buildUtc" "/p:ReleaseConfigSha256=$configHash"
if ($LASTEXITCODE -ne 0) { throw "Release 构建失败：$LASTEXITCODE" }

$output = Join-Path $repo 'MTTfTest\bin\Release'
$files = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name)
$manifestFiles = foreach ($file in $files) {
    [ordered]@{
        name = $file.Name
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$identity = [ordered]@{
    productVersion = 'V2.10.2.7'
    gitCommit = $commit
    gitBranch = $branch
    gitDirty = $false
    buildUtc = $buildUtc
    configSha256 = $configHash
    platform = 'x86'
    files = @($manifestFiles)
}
$identity | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $output 'build-identity.json') -Encoding UTF8
$checksumFiles = @(Get-ChildItem -LiteralPath $output -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name |
    ForEach-Object {
        [ordered]@{
            name = $_.Name
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
$checksumFiles | ForEach-Object { "$($_.sha256)  $($_.name)" } |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII

Write-Host "Release 现场包已生成：$output"
Write-Host "Commit=$commit Branch=$branch ConfigSha256=$configHash BuildUtc=$buildUtc"
