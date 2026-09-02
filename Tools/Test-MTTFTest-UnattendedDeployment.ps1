[CmdletBinding()]
param(
    [string]$InstallerPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'
}
$installer = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "无人值守安装脚本不存在：$installer"
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $installer,
    [ref]$tokens,
    [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) {
    throw "无人值守安装脚本无法解析：$(@($parseErrors)[0].Message)"
}

$installerText = [IO.File]::ReadAllText($installer, [Text.Encoding]::UTF8)
foreach ($removedGate in @(
        'Read-And-VerifyPackage',
        'Protect-MachineJson',
        'Write-SlotDescriptor',
        'Assert-SlotDescriptor',
        'package-pointer.v5.json')) {
    if ($installerText.Contains($removedGate)) {
        throw "安装脚本仍包含已取消的复杂包门禁：$removedGate"
    }
}
foreach ($required in @(
        'Assert-RequiredProgramFiles',
        'Stop-InstalledSessionAgent',
        'Assert-InstalledMainStopped',
        'Initialize-RuntimeConfig',
        'Test-CurrentSlotReplacementRequired',
        'MTTFTestAutoStart',
        'MTTFTest.FirstRun.configured',
        "'Configure'",
        'MTTFTest.exe',
        'MTTFTest.Watchdog.exe',
        'MTTFTest.SessionAgent.exe')) {
    if (-not $installerText.Contains($required)) {
        throw "安装脚本缺少最小运行检查：$required"
    }
}
Write-Output 'PASS SimpleUnattendedDeploymentContract 1/1'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('EPBTest-QuickDeploy-' + [Guid]::NewGuid().ToString('N'))
try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'QuickDeploy') -File |
        Copy-Item -Destination $testRoot -Force
    Copy-Item -LiteralPath $installer `
        -Destination (Join-Path $testRoot 'QuickDeploy-Installer.ps1') -Force
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'MTTFTest.exe'),
        'parse-only',
        (New-Object Text.UTF8Encoding($false)))

    $previousParseOnly = $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY
    try {
        $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY = '1'
        $commandCases = @(
            @('一键安装正式版.cmd', 'QUICKDEPLOY_INSTALL_PARSE_PASS'),
            @('一键修复.cmd', 'QUICKDEPLOY_REPAIR_PARSE_PASS'),
            @('一键卸载.cmd', 'QUICKDEPLOY_UNINSTALL_PARSE_PASS'))
        foreach ($case in $commandCases) {
            $commandPath = Join-Path $testRoot $case[0]
            $output = @(& $env:ComSpec /d /c "`"$commandPath`"" 2>&1)
            if ($LASTEXITCODE -ne 0 -or
                (@($output | Where-Object { [string]$_ -eq $case[1] }).Count -ne 1)) {
                throw "快捷部署批处理解析失败：$($case[0]); Exit=$LASTEXITCODE; Output=$($output -join ' | ')"
            }
        }
    }
    finally {
        $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY = $previousParseOnly
    }
    Write-Output 'PASS QuickDeployCommandParse 3/3'
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
