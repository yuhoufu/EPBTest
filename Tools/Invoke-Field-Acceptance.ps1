[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [string]$ReleaseDirectory = '',
    [ValidateRange(0, 100000)][double]$MinimumHours = 2,
    [ValidateSet('full', 'quick')][string]$LogScan = 'full',
    [ValidateSet('full', 'none')][string]$ArtifactScan = 'full',
    [ValidateSet('full', 'none')][string]$DatabaseScan = 'full',
    [string]$OutputDirectory = '',
    [string]$PythonExecutable = ''
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-PythonExecutable {
    param([string]$ExplicitPath)

    $candidates = New-Object 'System.Collections.Generic.List[string]'
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates.Add($ExplicitPath)
    }
    $command = Get-Command python -ErrorAction SilentlyContinue
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
        $candidates.Add($command.Source)
    }
    foreach ($registryRoot in @(
        'HKCU:\Software\Python\PythonCore',
        'HKLM:\Software\Python\PythonCore',
        'HKLM:\Software\WOW6432Node\Python\PythonCore'
    )) {
        foreach ($version in Get-ChildItem -LiteralPath $registryRoot -ErrorAction SilentlyContinue) {
            $install = Get-ItemProperty -LiteralPath ($version.PSPath + '\InstallPath') `
                -ErrorAction SilentlyContinue
            if ($null -ne $install -and -not [string]::IsNullOrWhiteSpace($install.ExecutablePath)) {
                $candidates.Add([string]$install.ExecutablePath)
            }
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        & $candidate -c 'import sys; assert sys.version_info >= (3, 9)' 2>$null
        if ($LASTEXITCODE -eq 0) { return [IO.Path]::GetFullPath($candidate) }
    }
    throw '找不到可用的 Python 3.9+。可通过 -PythonExecutable 显式指定 python.exe。'
}

if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    throw '必须通过 -ReleaseDirectory 指定独立、已校验的版本发布目录；bin\Release 是 VS 暂存区，禁止用于现场验收。'
}
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
if (-not (Test-Path -LiteralPath $data -PathType Container)) {
    throw "数据目录不存在：$data"
}

$verifyScript = Join-Path $PSScriptRoot 'Verify-Release.ps1'
$verifyText = & $verifyScript -ReleaseDirectory $release -RequireDeploymentApproved | Out-String
$releaseIdentity = $verifyText | ConvertFrom-Json
if ($releaseIdentity.verified -ne $true -or
    $releaseIdentity.deploymentApproved -ne $true -or
    $releaseIdentity.gitDirty -ne $false) {
    throw "Release 不是已批准、洁净的正式候选。"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $safeName = ([IO.Path]::GetFileName($data.TrimEnd('\', '/')) -replace '[^0-9A-Za-z._-]', '_')
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $OutputDirectory = Join-Path $repo "artifacts\field-acceptance\$safeName-$stamp"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Path $output -Force)

$validator = Join-Path $PSScriptRoot 'validate_epb_field_gate.py'
$outputMd = Join-Path $output 'field-gate.md'
$outputJson = Join-Path $output 'field-gate.json'
$python = Resolve-PythonExecutable -ExplicitPath $PythonExecutable
$expectedBuildUtc = if ($releaseIdentity.buildUtc -is [DateTime]) {
    $releaseIdentity.buildUtc.ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture)
}
else {
    [string]$releaseIdentity.buildUtc
}
$arguments = @(
    $validator,
    $data,
    '--expected-version', [string]$releaseIdentity.productVersion,
    '--expected-exe-sha256', [string]$releaseIdentity.exeSha256,
    '--expected-config-sha256', [string]$releaseIdentity.configSha256,
    '--expected-git-commit', [string]$releaseIdentity.gitCommit,
    '--expected-build-utc', $expectedBuildUtc,
    '--minimum-hours', $MinimumHours.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--performance-gates', 'required',
    '--log-scan', $LogScan,
    '--artifact-scan', $ArtifactScan,
    '--database-scan', $DatabaseScan,
    '--output-md', $outputMd,
    '--output-json', $outputJson
)

$previousPythonIoEncoding = $env:PYTHONIOENCODING
try {
    $env:PYTHONIOENCODING = 'utf-8'
    & $python @arguments
    $validatorExitCode = $LASTEXITCODE
}
finally {
    if ($null -eq $previousPythonIoEncoding) {
        Remove-Item Env:\PYTHONIOENCODING -ErrorAction SilentlyContinue
    }
    else {
        $env:PYTHONIOENCODING = $previousPythonIoEncoding
    }
}
[pscustomobject]@{
    passed = ($validatorExitCode -eq 0)
    exitCode = $validatorExitCode
    productVersion = $releaseIdentity.productVersion
    exeSha256 = $releaseIdentity.exeSha256
    gitCommit = $releaseIdentity.gitCommit
    minimumHours = $MinimumHours
    dataDirectory = $data
    releaseDirectory = $release
    pythonExecutable = $python
    outputMarkdown = $outputMd
    outputJson = $outputJson
} | ConvertTo-Json -Depth 3
exit $validatorExitCode
