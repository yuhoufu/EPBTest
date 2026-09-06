param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$PackageRoot = ''
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$expectedOutput = [IO.Path]::GetFullPath(
    (Join-Path $repo 'MTTfTest\bin\Release')).TrimEnd('\', '/')
if (-not [string]::Equals(
        $output,
        $expectedOutput,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "VS2022 candidate must seal the main Release output. Expected=$expectedOutput Actual=$output"
}
if (-not (Test-Path -LiteralPath $output -PathType Container)) {
    throw "Release output directory does not exist: $output"
}

$exePath = Join-Path $output 'MTTFTest.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Release executable does not exist: $exePath"
}

$expectedConfigs = @(
    'Config/AIConfig.xml',
    'Config/AlarmConfig.xml',
    'Config/AOConfig.xml',
    'Config/DOConfig.xml',
    'Config/PowerSupplyConfig.xml',
    'Config/TestConfig.xml',
    'Config/UIConfig.xml'
)

function Get-RelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return $Path.Substring($output.Length).TrimStart('\', '/').Replace('\', '/')
}

function Test-RuntimeGeneratedPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $normalized = $RelativePath.Replace('\', '/').TrimStart('/')
    foreach ($directory in @('DataStore', 'Data', 'log', 'IncidentSnapshots-Fallback')) {
        if ($normalized.StartsWith(
                $directory + '/',
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        return -join ($algorithm.ComputeHash($stream) |
            ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Get-AggregateConfigHash {
    param([Parameter(Mandatory = $true)]$ConfigFiles)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = New-Object IO.MemoryStream
    try {
        foreach ($entry in $ConfigFiles.GetEnumerator()) {
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

$actualConfigs = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' `
    ([StringComparer]::Ordinal)
$configDirectory = Join-Path $output 'Config'
foreach ($file in Get-ChildItem -LiteralPath $configDirectory -Filter '*.xml' -File) {
    $relative = 'Config/' + $file.Name
    $actualConfigs.Add($relative, $file.FullName)
}
$missingConfigs = @($expectedConfigs | Where-Object { -not $actualConfigs.ContainsKey($_) })
$unexpectedConfigs = @($actualConfigs.Keys | Where-Object { $_ -notin $expectedConfigs })
if ($missingConfigs.Count -ne 0 -or $unexpectedConfigs.Count -ne 0 -or
    $actualConfigs.Count -ne $expectedConfigs.Count) {
    throw "VS2022 config file set mismatch. Missing=$($missingConfigs -join ',') " +
          "Unexpected=$($unexpectedConfigs -join ',')"
}

$gitCommit = ((@(git -C $repo rev-parse --verify HEAD 2>&1) -join [Environment]::NewLine)).Trim()
if ($LASTEXITCODE -ne 0 -or $gitCommit -notmatch '^[0-9a-fA-F]{40}$') {
    $gitCommit = 'unknown'
}
$gitBranch = ((@(git -C $repo branch --show-current 2>&1) -join [Environment]::NewLine)).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitBranch)) {
    $gitBranch = 'unknown'
}
$gitStatus = @(git -C $repo status --porcelain 2>&1)
$gitDirty = $LASTEXITCODE -ne 0 -or $gitStatus.Count -ne 0
$buildUtc = [DateTime]::UtcNow.ToString('O')
$productVersion = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
$fileVersion = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($exePath).Name
$productLabel = 'V' + $productVersion
$configSha256 = Get-AggregateConfigHash $actualConfigs

if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Join-Path $repo 'artifacts\vs2022'
}
$packageRootFull = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
[void](New-Item -ItemType Directory -Path $packageRootFull -Force)
$rootPrefix = $packageRootFull + [IO.Path]::DirectorySeparatorChar
$stamp = [DateTime]::Now.ToString('yyyyMMdd_HHmmss_fff')
$packageName = "$productLabel-VS2022-$stamp"
$packageOutput = [IO.Path]::GetFullPath((Join-Path $packageRootFull $packageName))
$stagingOutput = [IO.Path]::GetFullPath(
    (Join-Path $packageRootFull ('.staging-' + [Guid]::NewGuid().ToString('N'))))
if (-not $packageOutput.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $stagingOutput.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'VS2022 candidate output escaped its package root.'
}
if (Test-Path -LiteralPath $packageOutput) {
    throw "VS2022 candidate already exists: $packageOutput"
}

try {
    [void](New-Item -ItemType Directory -Path $stagingOutput)
    foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File) {
        $relative = Get-RelativePath $file.FullName
        if ($relative -in @('build-identity.json', 'SHA256SUMS.txt') -or
            (Test-RuntimeGeneratedPath $relative)) {
            continue
        }
        $destination = Join-Path $stagingOutput $relative.Replace('/', '\')
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }

    $manifestFiles = @(Get-ChildItem -LiteralPath $stagingOutput -Recurse -File |
        Sort-Object { $_.FullName.Substring($stagingOutput.Length).Replace('\', '/') } |
        ForEach-Object {
            [ordered]@{
                name = $_.FullName.Substring($stagingOutput.Length).TrimStart('\', '/').Replace('\', '/')
                bytes = $_.Length
                sha256 = Get-Sha256 $_.FullName
            }
        })
    $identity = [ordered]@{
        productVersion = $productLabel
        fileVersion = $fileVersion
        assemblyName = $assemblyName
        releaseStatus = 'VS2022_RELEASE_CANDIDATE'
        deploymentApproved = $false
        gitCommit = $gitCommit
        gitBranch = $gitBranch
        gitDirty = $gitDirty
        buildUtc = $buildUtc
        configSha256 = $configSha256
        platform = 'x86'
        verification = [ordered]@{
            solutionRebuild = 'VS_BUILD_ONLY'
            adaptiveControlTests = 'NOT_RUN'
            epbDiskWriterTests = 'NOT_RUN'
            persistenceSoak = 'NOT_RUN'
            powerSupplyDebuggerTests = 'NOT_RUN'
            fieldGateTests = 'NOT_RUN'
        }
        files = $manifestFiles
    }
    $identityPath = Join-Path $stagingOutput 'build-identity.json'
    [IO.File]::WriteAllText(
        $identityPath,
        ($identity | ConvertTo-Json -Depth 5),
        (New-Object Text.UTF8Encoding($false)))

    $checksumLines = @(Get-ChildItem -LiteralPath $stagingOutput -Recurse -File |
        Sort-Object { $_.FullName.Substring($stagingOutput.Length).Replace('\', '/') } |
        ForEach-Object {
            $relative = $_.FullName.Substring($stagingOutput.Length).TrimStart('\', '/').Replace('\', '/')
            "$(Get-Sha256 $_.FullName)  $relative"
        })
    [IO.File]::WriteAllLines(
        (Join-Path $stagingOutput 'SHA256SUMS.txt'),
        $checksumLines,
        (New-Object Text.UTF8Encoding($false)))

    Move-Item -LiteralPath $stagingOutput -Destination $packageOutput
    Write-Host "VS2022 standalone candidate created: $packageOutput"
    Write-Host 'This candidate passed recursive file sealing only. It is not production-approved.'
}
catch {
    if (Test-Path -LiteralPath $stagingOutput -PathType Container) {
        Remove-Item -LiteralPath $stagingOutput -Recurse -Force
    }
    throw
}
