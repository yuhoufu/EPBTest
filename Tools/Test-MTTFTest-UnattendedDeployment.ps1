[CmdletBinding()]
param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1')
)

$ErrorActionPreference = 'Stop'
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

$requiredFunctions = @(
    'Protect-MachineJson',
    'Unprotect-MachineJson',
    'Write-SlotDescriptor',
    'Assert-SlotDescriptor')
foreach ($name in $requiredFunctions) {
    $definition = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            [string]::Equals($node.Name, $name, [StringComparison]::Ordinal)
    }, $true) | Select-Object -First 1)
    if ($definition.Count -ne 1) {
        throw "安装脚本缺少唯一函数：$name"
    }
    Invoke-Expression $definition[0].Extent.Text
}

$installerText = [IO.File]::ReadAllText($installer, [Text.Encoding]::UTF8)
if ($installerText -notmatch "Write-SlotDescriptor\s+\`$staging\s+'Current'\s+\`$stagingIdentity\s+\`$current") {
    throw 'Current 安装流程没有把正式槽路径传给封印函数。'
}
if ($installerText -notmatch "Write-SlotDescriptor\s+\`$staging\s+'LastKnownGood'\s+\`$identity\s+\`$lkg") {
    throw 'LastKnownGood 晋升流程没有把正式槽路径传给封印函数。'
}

Add-Type -AssemblyName System.Security
$expectedVersion = '2.14.1.0'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('EPBTest-SlotContract-' + [Guid]::NewGuid().ToString('N'))

function Assert-SealedRoot(
    [string]$SlotName,
    [string]$StagingPath,
    [string]$FinalPath) {
    [void](New-Item -ItemType Directory -Path $StagingPath)
    [IO.File]::WriteAllText(
        (Join-Path $StagingPath 'build-identity.json'),
        '{"productVersion":"V2.14.1.0"}',
        (New-Object Text.UTF8Encoding($false)))
    $identity = [pscustomobject]@{
        releaseStatus = 'FIELD_CANDIDATE_PENDING_168H'
        configSha256 = ('a' * 64)
        files = @([pscustomobject]@{
            name = 'build-identity.json'
            bytes = (Get-Item -LiteralPath (Join-Path $StagingPath 'build-identity.json')).Length
            sha256 = (Get-FileHash -LiteralPath `
                (Join-Path $StagingPath 'build-identity.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    Write-SlotDescriptor $StagingPath $SlotName $identity $FinalPath
    Move-Item -LiteralPath $StagingPath -Destination $FinalPath
    Assert-SlotDescriptor $FinalPath $SlotName

    $envelope = Get-Content -LiteralPath (Join-Path $FinalPath 'package-slot.v5.json') `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    $clear = [Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$envelope.ProtectedPayloadBase64),
        [Text.Encoding]::UTF8.GetBytes('MTTFTest.PackageSlot.Schema5'),
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    $payload = [Text.Encoding]::UTF8.GetString($clear) | ConvertFrom-Json
    $expectedRoot = [IO.Path]::GetFullPath($FinalPath).TrimEnd('\', '/')
    $actualRoot = [IO.Path]::GetFullPath([string]$payload.RootPath).TrimEnd('\', '/')
    if (-not [string]::Equals($expectedRoot, $actualRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$SlotName 封印路径错误：Expected=$expectedRoot Actual=$actualRoot"
    }
    if ($actualRoot.IndexOf('staging', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "$SlotName 仍封印了 staging 路径：$actualRoot"
    }
}

try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    Assert-SealedRoot `
        -SlotName 'Current' `
        -StagingPath (Join-Path $testRoot '.current-staging-test') `
        -FinalPath (Join-Path $testRoot 'Current')
    Assert-SealedRoot `
        -SlotName 'LastKnownGood' `
        -StagingPath (Join-Path $testRoot '.lkg-staging-test') `
        -FinalPath (Join-Path $testRoot 'LastKnownGood')
    Write-Output 'PASS UnattendedDeploymentSlotContract 2/2'

    $quickRoot = Join-Path $testRoot '快捷部署测试'
    [void](New-Item -ItemType Directory -Path $quickRoot)
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'QuickDeploy') -File |
        Copy-Item -Destination $quickRoot -Force
    Copy-Item -LiteralPath $installer `
        -Destination (Join-Path $quickRoot 'QuickDeploy-Installer.ps1') -Force
    $packageRoot = Join-Path $quickRoot 'Package'
    [void](New-Item -ItemType Directory -Path $packageRoot)
    [IO.File]::WriteAllText(
        (Join-Path $packageRoot 'build-identity.json'),
        '{"productVersion":"V2.14.1.0"}',
        (New-Object Text.UTF8Encoding($false)))

    $previousParseOnly = $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY
    try {
        $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY = '1'
        $commandCases = @(
            @('一键安装现场候选.cmd', 'QUICKDEPLOY_INSTALL_PARSE_PASS'),
            @('一键修复.cmd', 'QUICKDEPLOY_REPAIR_PARSE_PASS'),
            @('一键卸载.cmd', 'QUICKDEPLOY_UNINSTALL_PARSE_PASS'))
        foreach ($case in $commandCases) {
            $commandPath = Join-Path $quickRoot $case[0]
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
