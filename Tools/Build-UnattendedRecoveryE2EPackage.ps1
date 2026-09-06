[CmdletBinding()]
param(
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo 'artifacts\UnattendedRecoveryE2E'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'E2E 测试包只能生成到当前仓库内。'
}

$msbuild = 'D:\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    $msbuild = (Get-Command msbuild.exe -ErrorAction Stop).Source
}
$projects = @(
    'MTTFTest.Watchdog\MTTFTest.Watchdog.csproj',
    'MTTFTest.SessionAgent\MTTFTest.SessionAgent.csproj',
    'Tests\UnattendedRecoveryE2E\UnattendedRecoveryTestMain.csproj',
    'Tests\UnattendedRecoveryE2E\NoHardwareSafetyAgent.csproj'
)
foreach ($project in $projects) {
    & $msbuild (Join-Path $repo $project) /t:Build `
        /p:Configuration=Release /p:Platform=AnyCPU /m /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "E2E 测试宿主构建失败：$project"
    }
}

if (Test-Path -LiteralPath $output) {
    $resolved = [IO.Path]::GetFullPath($output)
    if ($resolved -eq $repo -or -not $resolved.StartsWith(
            (Join-Path $repo 'artifacts') + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理非 artifacts E2E 目录：$resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $output 'Config') -Force | Out-Null

$mainOutput = Join-Path $repo 'Tests\UnattendedRecoveryE2E\bin\Release'
$watchdogOutput = Join-Path $repo 'MTTFTest.Watchdog\bin\Release'
$sessionOutput = Join-Path $repo 'MTTFTest.SessionAgent\bin\Release'
foreach ($source in @(
        (Join-Path $mainOutput 'MTTFTest.exe'),
        (Join-Path $mainOutput 'MTTFTest.pdb'),
        (Join-Path $mainOutput 'MTTFTest.Watchdog.Client.dll'),
        (Join-Path $mainOutput 'MTTFTest.Watchdog.Protocol.dll'),
        (Join-Path $watchdogOutput 'MTTFTest.Watchdog.exe'),
        (Join-Path $watchdogOutput 'MTTFTest.Watchdog.pdb'),
        (Join-Path $sessionOutput 'MTTFTest.SessionAgent.exe'),
        (Join-Path $sessionOutput 'MTTFTest.SessionAgent.pdb'),
        (Join-Path $mainOutput 'MTTFTest.SafetyAgent.exe'),
        (Join-Path $mainOutput 'MTTFTest.SafetyAgent.pdb'))) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "E2E 测试包缺少构建输出：$source"
    }
    Copy-Item -LiteralPath $source -Destination $output -Force
}

foreach ($name in @(
        'AIConfig.xml', 'AOConfig.xml', 'DOConfig.xml',
        'PowerSupplyConfig.xml', 'TestConfig.xml')) {
    Copy-Item -LiteralPath (Join-Path $repo "MTTfTest\Config\$name") `
        -Destination (Join-Path $output "Config\$name") -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'MTTfTest\Config\AlarmConfig.xml') `
    -Destination (Join-Path $output 'Config\AlarmConfig.xml') -Force
New-Item -ItemType File `
    -Path (Join-Path $output 'MTTFTest.UnattendedMode.required') `
    -Force | Out-Null

$identity = [ordered]@{
    schemaVersion = 1
    testOnly = $true
    productionRelease = $false
    version = '2.17.2.0'
    builtUtc = [DateTime]::UtcNow.ToString('O')
    files = @(
        Get-ChildItem -LiteralPath $output -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    path = $_.FullName.Substring($output.Length + 1)
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                }
            })
}
$identity | ConvertTo-Json -Depth 8 | Set-Content `
    -LiteralPath (Join-Path $output 'e2e-package-identity.json') `
    -Encoding UTF8
Write-Output "PASS UnattendedRecoveryE2EPackage $output"
