param(
    [string]$MsBuild = 'D:\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe',
    [string]$PackageRoot = '',
    [ValidateRange(1, 3600)]
    [int]$PersistenceSoakSeconds = 600,
    [switch]$AllowDirtyCandidate
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repo

$releaseProjectPath = Join-Path $repo 'MTTfTest\MTTfTest.csproj'
[xml]$releaseProjectXml = Get-Content -LiteralPath $releaseProjectPath -Raw
$expectedProductVersion = ([string]$releaseProjectXml.Project.PropertyGroup.ApplicationVersion |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($expectedProductVersion)) {
    throw 'MTTfTest.csproj 未声明 ApplicationVersion。'
}
$expectedProductLabel = 'V' + $expectedProductVersion
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

$dirty = @(git status --porcelain)
if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 状态。' }
$isDirty = $dirty.Count -ne 0
if ($isDirty -and -not $AllowDirtyCandidate) {
    throw "拒绝生成现场包：源码树不是干净状态。`n$($dirty -join "`n")"
}

$commitOutput = @(git rev-parse --verify HEAD 2>&1)
if ($LASTEXITCODE -ne 0) { throw '无法读取 Git HEAD。' }
$commit = ((@($commitOutput) -join [Environment]::NewLine)).Trim()
if ($commit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Git HEAD 不是完整 40 位提交 SHA：$commit"
}
$branchOutput = git branch --show-current
if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 分支。' }
$branch = ((@($branchOutput) -join [Environment]::NewLine)).Trim()
if ([string]::IsNullOrWhiteSpace($branch)) { $branch = 'detached' }
$buildUtc = [DateTime]::UtcNow.ToString('O')
$gitDirtyText = $isDirty.ToString().ToLowerInvariant()

function Get-SourceSnapshotFingerprint {
    $status = @(git status --porcelain=v1 --untracked-files=all 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw '无法读取源码快照状态。'
    }
    $changedPaths = @(
        @(git diff --name-only HEAD -- 2>&1)
        @(git ls-files --others --exclude-standard 2>&1)
    )
    if ($LASTEXITCODE -ne 0) {
        throw '无法枚举源码快照文件。'
    }

    $builder = New-Object Text.StringBuilder
    foreach ($line in @($status | Sort-Object)) {
        [void]$builder.Append("STATUS\t").Append([string]$line).Append("`n")
    }
    foreach ($relativePath in @($changedPaths |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique)) {
        $normalized = ([string]$relativePath).Replace('\', '/')
        $fullPath = [IO.Path]::GetFullPath((Join-Path $repo $relativePath))
        $repoPrefix = $repo.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "源码快照文件越界：$relativePath"
        }
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
            [void]$builder.Append("FILE\t").Append($normalized).Append("\t").Append($hash).Append("`n")
        }
        else {
            [void]$builder.Append("MISSING\t").Append($normalized).Append("`n")
        }
    }

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($builder.ToString())
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

$sourceSnapshotFingerprint = Get-SourceSnapshotFingerprint

function Assert-SourceSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)][string]$ExpectedFingerprint,
        [Parameter(Mandatory = $true)][string]$Stage
    )

    $headOutput = @(git rev-parse --verify HEAD 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "$Stage：无法复核 Git HEAD。"
    }
    $currentCommit = ((@($headOutput) -join [Environment]::NewLine)).Trim()
    if (-not [string]::Equals(
            $currentCommit,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Stage：源码 HEAD 已变化，Start=$ExpectedCommit Current=$currentCommit"
    }

    $currentFingerprint = Get-SourceSnapshotFingerprint
    if (-not [string]::Equals(
            $currentFingerprint,
            $ExpectedFingerprint,
            [StringComparison]::Ordinal)) {
        $currentDirty = @(git status --porcelain 2>&1)
        throw "$Stage：源码变更快照已变化，拒绝使用混合源码生成候选。`n$($currentDirty -join "`n")"
    }
}

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

function Invoke-CandidateTest {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$SuccessPattern
    )

    Write-Host "[$Label] $FilePath $($ArgumentList -join ' ')"
    $captured = @(& $FilePath @ArgumentList 2>&1)
    $exitCode = $LASTEXITCODE
    foreach ($line in $captured) { Write-Host ([string]$line) }
    if ($exitCode -ne 0) {
        throw "$Label 失败，ExitCode=$exitCode"
    }
    $summary = @($captured | ForEach-Object { [string]$_ } |
        Where-Object { $_ -match $SuccessPattern } | Select-Object -Last 1)
    if ($summary.Count -eq 0) {
        throw "$Label 未输出预期通过摘要：$SuccessPattern"
    }
    return $summary[0].Trim()
}

# 这些安全、持续运行和背压类位于旧式非 SDK 项目中。目录里存在 .cs 并不代表会参与
# 编译；在正式构建前显式检查，避免 VS 缓存或手工编辑再次生成“类型不存在”的假版本。
Assert-LegacyCompileItems -ProjectRelativePath 'Controller\Controller.csproj' -RequiredItems @(
    'EpbManager.FieldMetrics.cs',
    'EpbManager.DaqLiveness.cs',
    'LatestPairMailbox.cs',
    'ReleasePackageVerifier.cs',
    'UiCurveContinuityPolicy.cs',
    'UiLogDisplayPolicy.cs',
    'TaskSupervisor.cs',
    'RecoveryTaskRegistry.cs'
)
Assert-LegacyCompileItems -ProjectRelativePath 'IO.NI\IO.NI.csproj' -RequiredItems @(
    'CoalescingTaskSupervisor.cs',
    'HostRuntimeProbe.cs'
)
Assert-LegacyCompileItems -ProjectRelativePath 'MTTfTest\MTTfTest.csproj' -RequiredItems @(
    'Editors\ToggleButton.cs',
    'WatchdogRuntime.cs'
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

$solutionPath = Join-Path $repo 'TfTest.sln'
& $MsBuild $solutionPath /t:Restore /m:1 `
    /p:Configuration=Release '/p:Platform=Any CPU' `
    /p:RestorePackagesConfig=true
if ($LASTEXITCODE -ne 0) { throw "依赖还原失败：$LASTEXITCODE" }

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

& $MsBuild $solutionPath /t:Rebuild /m:1 `
    /p:Configuration=Release '/p:Platform=Any CPU' /p:FormalReleaseBuild=true `
    /p:GenerateResourceUsePreserializedResources=false `
    /p:GenerateResourceWarnOnBinaryFormatterUse=false `
    "/p:GitCommit=$commit" "/p:GitBranch=$branch" "/p:GitDirty=$gitDirtyText" `
    "/p:BuildUtc=$buildUtc" "/p:ReleaseConfigSha256=$configHash"
if ($LASTEXITCODE -ne 0) { throw "Release 构建失败：$LASTEXITCODE" }

$exePath = Join-Path $output 'MTTFTest.exe'
$watchdogExePath = Join-Path $output 'MTTFTest.Watchdog.exe'
$watchdogProtocolPath = Join-Path $output 'MTTFTest.Watchdog.Protocol.dll'
$watchdogClientPath = Join-Path $output 'MTTFTest.Watchdog.Client.dll'
foreach ($requiredSidecar in @($watchdogExePath, $watchdogProtocolPath, $watchdogClientPath)) {
    if (-not (Test-Path -LiteralPath $requiredSidecar -PathType Leaf)) {
        throw "Release 构建缺少独立看门狗文件：$requiredSidecar"
    }
}
$actualProductVersion = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
if ($actualProductVersion -ne $expectedProductVersion) {
    throw "版本身份不一致：期望 $expectedProductVersion，实际 $actualProductVersion"
}
$actualFileVersion = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
if ($actualFileVersion -ne $expectedProductVersion) {
    throw "EXE 文件版本身份不一致：期望 $expectedProductVersion，实际 $actualFileVersion"
}
$actualAssemblyName = [Reflection.AssemblyName]::GetAssemblyName($exePath).Name
if ($actualAssemblyName -ne $expectedAssemblyName) {
    throw "EXE 程序集名称不一致：期望 $expectedAssemblyName，实际 $actualAssemblyName"
}

# 编译/还原可能持续较久。进入更长的回归前重新读取 Git，而不是沿用脚本启动时
# 的结论，避免构建期间切换提交或编辑源码后仍把旧 commit 写进正式身份。
Assert-SourceSnapshot -ExpectedCommit $commit `
    -ExpectedFingerprint $sourceSnapshotFingerprint `
    -Stage '回归测试前源码快照校验'

# 正式候选不能只证明主程序“能编译”。以下回归全部成功后才允许写入
# FORMAL_RELEASE_CANDIDATE；任何一项失败都在复制发布目录之前终止。
$adaptiveTestExe = Join-Path $repo 'Tests\AdaptiveControlTests\bin\Release\AdaptiveControlTests.exe'
$diskWriterTestExe = Join-Path $repo 'Tests\EpbDiskWriterTests\bin\Release\EpbDiskWriterTests.exe'
if (-not (Test-Path -LiteralPath $adaptiveTestExe -PathType Leaf)) {
    throw "AdaptiveControlTests 未生成：$adaptiveTestExe"
}
if (-not (Test-Path -LiteralPath $diskWriterTestExe -PathType Leaf)) {
    throw "EpbDiskWriterTests 未生成：$diskWriterTestExe"
}

$adaptiveSummary = Invoke-CandidateTest `
    -Label 'AdaptiveControlTests' `
    -FilePath $adaptiveTestExe `
    -SuccessPattern '^PASS\s+\d+/\d+$'
$diskWriterSummary = Invoke-CandidateTest `
    -Label 'EpbDiskWriterTests' `
    -FilePath $diskWriterTestExe `
    -SuccessPattern '^PASS\s+\d+/\d+$'
$soakSummary = Invoke-CandidateTest `
    -Label "PersistenceSoak(${PersistenceSoakSeconds}s)" `
    -FilePath $adaptiveTestExe `
    -ArgumentList @('--persistence-soak', [string]$PersistenceSoakSeconds) `
    -SuccessPattern '^PASS\s+1/1$'

$powerSupplyProject = Join-Path $repo 'Tests\PowerSupplyDebugger.Tests\PowerSupplyDebugger.Tests.csproj'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$powerSupplyResultsDirectory = [IO.Path]::GetFullPath((Join-Path `
    $tempRoot ('epb-release-power-' + [Guid]::NewGuid().ToString('N'))))
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
if (-not $powerSupplyResultsDirectory.StartsWith(
        $tempPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "电源测试临时目录越界：$powerSupplyResultsDirectory"
}
$powerSupplyTrxName = 'PowerSupplyDebugger.Tests.trx'
$powerSupplyTrxPath = Join-Path $powerSupplyResultsDirectory $powerSupplyTrxName
$powerSupplySummary = $null
try {
    [void](New-Item -ItemType Directory -Path $powerSupplyResultsDirectory)
    $powerSupplyOutput = @(& dotnet test $powerSupplyProject `
        --configuration Release --no-restore --no-build --verbosity minimal `
        --results-directory $powerSupplyResultsDirectory `
        --logger "trx;LogFileName=$powerSupplyTrxName" 2>&1)
    $powerSupplyExitCode = $LASTEXITCODE
    foreach ($line in $powerSupplyOutput) { Write-Host ([string]$line) }
    if ($powerSupplyExitCode -ne 0) {
        throw "PowerSupplyDebugger.Tests 失败，ExitCode=$powerSupplyExitCode"
    }
    if (-not (Test-Path -LiteralPath $powerSupplyTrxPath -PathType Leaf)) {
        throw "PowerSupplyDebugger.Tests 未生成结构化 TRX：$powerSupplyTrxPath"
    }

    [xml]$powerSupplyTrx = [IO.File]::ReadAllText(
        $powerSupplyTrxPath,
        [Text.Encoding]::UTF8)
    $powerSupplyCounters = $powerSupplyTrx.SelectSingleNode(
        "//*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $powerSupplyCounters) {
        throw 'PowerSupplyDebugger.Tests TRX 缺少 Counters。'
    }
    $powerSupplyTotal = [int]$powerSupplyCounters.GetAttribute('total')
    $powerSupplyPassed = [int]$powerSupplyCounters.GetAttribute('passed')
    $powerSupplyFailed = [int]$powerSupplyCounters.GetAttribute('failed')
    if ($powerSupplyTotal -lt 1 -or
        $powerSupplyFailed -ne 0 -or
        $powerSupplyPassed -ne $powerSupplyTotal) {
        throw "PowerSupplyDebugger.Tests TRX 未全通过：" +
              "Total=$powerSupplyTotal Passed=$powerSupplyPassed Failed=$powerSupplyFailed"
    }
    $powerSupplySummary = "PASS $powerSupplyPassed/$powerSupplyTotal"
}
finally {
    if (Test-Path -LiteralPath $powerSupplyResultsDirectory -PathType Container) {
        Remove-Item -LiteralPath $powerSupplyResultsDirectory -Recurse -Force
    }
}

$fieldGateResultsDirectory = [IO.Path]::GetFullPath((Join-Path `
    $tempRoot ('epb-release-field-gate-' + [Guid]::NewGuid().ToString('N'))))
if (-not $fieldGateResultsDirectory.StartsWith(
        $tempPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "现场门禁测试临时目录越界：$fieldGateResultsDirectory"
}
$fieldGateStdOut = Join-Path $fieldGateResultsDirectory 'stdout.log'
$fieldGateStdErr = Join-Path $fieldGateResultsDirectory 'stderr.log'
$fieldGateOutput = @()
$fieldGateExitCode = -1
try {
    [void](New-Item -ItemType Directory -Path $fieldGateResultsDirectory)
    $pythonLauncher = (Get-Command py.exe -ErrorAction Stop).Source
    $fieldGateProcess = Start-Process `
        -FilePath $pythonLauncher `
        -ArgumentList @('-3', '-m', 'unittest', '-v', 'Tools.test_validate_epb_field_gate') `
        -WorkingDirectory $repo `
        -NoNewWindow `
        -RedirectStandardOutput $fieldGateStdOut `
        -RedirectStandardError $fieldGateStdErr `
        -Wait `
        -PassThru
    $fieldGateExitCode = $fieldGateProcess.ExitCode
    $fieldGateOutput = @(
        @([IO.File]::ReadAllLines($fieldGateStdOut, [Text.Encoding]::UTF8)) +
        @([IO.File]::ReadAllLines($fieldGateStdErr, [Text.Encoding]::UTF8)))
    foreach ($line in $fieldGateOutput) { Write-Host ([string]$line) }
    if ($fieldGateExitCode -ne 0) {
        throw "现场门禁测试失败，ExitCode=$fieldGateExitCode"
    }
    $fieldGateSummary = @($fieldGateOutput | ForEach-Object { [string]$_ } |
        Where-Object { $_ -match '^Ran\s+\d+\s+tests?' } | Select-Object -Last 1)
    if ($fieldGateSummary.Count -eq 0) {
        throw '现场门禁测试未输出 unittest 数量摘要。'
    }
}
finally {
    if (Test-Path -LiteralPath $fieldGateResultsDirectory -PathType Container) {
        Remove-Item -LiteralPath $fieldGateResultsDirectory -Recurse -Force
    }
}

# 回归期间也可能发生源码切换或编辑；identity 只能在第二次快照仍与开头一致且
# 工作树完全干净时写入。该门禁有意不受 -AllowDirtyCandidate 绕过。
Assert-SourceSnapshot -ExpectedCommit $commit `
    -ExpectedFingerprint $sourceSnapshotFingerprint `
    -Stage '写入构建身份前源码快照校验'

$verification = [ordered]@{
    solutionRebuild = 'PASS'
    adaptiveControlTests = $adaptiveSummary
    epbDiskWriterTests = $diskWriterSummary
    persistenceSoak = $soakSummary
    persistenceSoakSeconds = $PersistenceSoakSeconds
    powerSupplyDebuggerTests = $powerSupplySummary
    fieldGateTests = $fieldGateSummary[0].Trim()
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
    fileVersion = $actualFileVersion
    assemblyName = $actualAssemblyName
    # bin\Release 只是 VS/MSBuild 暂存区，永远不得直接部署。正式批准状态只写入
    # 后续独立版本目录，防止普通 VS 生成覆盖 DLL 后仍被当作正式候选启动。
    releaseStatus = if ($isDirty) { 'DIRTY_CANDIDATE_NOT_FOR_PRODUCTION' } else { 'BUILD_STAGING_NOT_FOR_DEPLOYMENT' }
    deploymentApproved = $false
    gitCommit = $commit
    gitBranch = $branch
    gitDirty = $isDirty
    buildUtc = $buildUtc
    configSha256 = $configHash
    platform = 'x86'
    verification = $verification
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

if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Join-Path $repo 'artifacts\releases'
}
$packageRootFull = [IO.Path]::GetFullPath($PackageRoot)
[void](New-Item -ItemType Directory -Path $packageRootFull -Force)
$shortCommit = if ($commit.Length -ge 12) { $commit.Substring(0, 12) } else { $commit }
$packageStamp = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss')
$packageName = if ($isDirty) {
    "$expectedProductLabel-$shortCommit-$packageStamp-DIRTY"
}
else {
    "$expectedProductLabel-$shortCommit-$packageStamp"
}
$packageOutput = [IO.Path]::GetFullPath((Join-Path $packageRootFull $packageName))
$stagingOutput = [IO.Path]::GetFullPath((Join-Path $packageRootFull ('.staging-' + [Guid]::NewGuid().ToString('N'))))
$rootPrefix = $packageRootFull.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $packageOutput.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $stagingOutput.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "发布包路径越界：Package=$packageOutput Staging=$stagingOutput Root=$packageRootFull"
}
if (Test-Path -LiteralPath $packageOutput) {
    throw "拒绝覆盖既有不可变发布包：$packageOutput"
}

try {
    [void](New-Item -ItemType Directory -Path $stagingOutput)
    Get-ChildItem -LiteralPath $output -Force |
        Copy-Item -Destination $stagingOutput -Recurse -Force

    $packageIdentityPath = Join-Path $stagingOutput 'build-identity.json'
    $packageChecksumPath = Join-Path $stagingOutput 'SHA256SUMS.txt'
    $identity.releaseStatus = if ($isDirty) {
        'DIRTY_CANDIDATE_NOT_FOR_PRODUCTION'
    }
    else {
        'FORMAL_RELEASE_CANDIDATE'
    }
    $identity.deploymentApproved = -not $isDirty
    $identity | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $packageIdentityPath -Encoding UTF8
    $packageChecksumMap = Get-RecursivePackageFiles -Root $stagingOutput `
        -ExcludedRelativePaths @('SHA256SUMS.txt')
    $packageChecksumLines = @($packageChecksumMap.GetEnumerator() |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.Value -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $($_.Key)"
        })
    [IO.File]::WriteAllLines(
        $packageChecksumPath,
        $packageChecksumLines,
        (New-Object Text.UTF8Encoding($false)))

    $verifyScript = Join-Path $PSScriptRoot 'Verify-Release.ps1'
    if ($isDirty) {
        & $verifyScript -ReleaseDirectory $stagingOutput | Out-Null
    }
    else {
        & $verifyScript -ReleaseDirectory $stagingOutput -RequireDeploymentApproved | Out-Null
    }
    Move-Item -LiteralPath $stagingOutput -Destination $packageOutput
}
catch {
    if (Test-Path -LiteralPath $stagingOutput -PathType Container) {
        Remove-Item -LiteralPath $stagingOutput -Recurse -Force
    }
    throw
}

if ($isDirty) {
    Write-Warning "已生成独立 DIRTY CANDIDATE：仅用于当前代码验证，不得作为正式生产放行包。"
}
else {
    Write-Host "Release 正式候选包已生成并独立校验：$packageOutput"
}
Write-Host "ScratchOutput=$output PackageOutput=$packageOutput Commit=$commit Branch=$branch Dirty=$isDirty ConfigSha256=$configHash BuildUtc=$buildUtc"
