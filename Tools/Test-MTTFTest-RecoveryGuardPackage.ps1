#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($PackageDirectory)
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$root = [IO.Path]::GetFullPath((Join-Path $tempBase ('EPB-GuardPackage-' + [Guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '测试目录越界。' }
$installer = Join-Path $PSScriptRoot 'Install-MTTFTest-RecoveryGuard.ps1'
$results = New-Object 'System.Collections.Generic.List[object]'

function Register-ScheduledTask { throw 'Validate 不得注册计划任务。' }
function Unregister-ScheduledTask { throw 'Validate 不得卸载计划任务。' }
function New-ScheduledTaskAction { throw 'Validate 不得创建任务动作。' }

function Save-Identity([string]$Folder, $Identity) {
    $Identity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Folder 'guard-identity.json') -Encoding UTF8
}
function Refresh-SettingsHash([string]$Folder, $Settings) {
    $path = Join-Path $Folder 'guard-settings.json'
    $Settings | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding UTF8
    $identity = Get-Content -LiteralPath (Join-Path $Folder 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $entry = $identity.files | Where-Object { $_.name -eq 'guard-settings.json' }
    $entry.bytes = (Get-Item -LiteralPath $path).Length
    $entry.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Save-Identity $Folder $identity
}
function Run-Case([string]$Name, [scriptblock]$Change, [string]$ExpectedError, [string]$RequestedMode = '') {
    $folder = Join-Path $root $Name
    [void](New-Item -ItemType Directory -Path $folder -Force)
    Get-ChildItem -LiteralPath $source -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $folder }
    & $Change $folder
    $install = Join-Path $folder 'NeverInstalled'
    $failure = ''
    $arguments = @{ Mode='Validate'; SourceDirectory=$folder; InstallRoot=$install }
    if ($RequestedMode) { $arguments.RecoveryMode = $RequestedMode }
    try { $null = & $installer @arguments }
    catch { $failure = $_.Exception.Message }
    if ([string]::IsNullOrEmpty($ExpectedError)) {
        if ($failure) { throw "$Name 意外失败：$failure" }
    } elseif (-not $failure.Contains($ExpectedError)) { throw "$Name 未得到预期拒绝：$failure" }
    if (Test-Path -LiteralPath $install) { throw "$Name 的 Validate 修改了安装目录。" }
    $results.Add([ordered]@{ name = $Name; passed = $true; rejected = [bool]$failure })
}
try {
    $archivePath = $source + '.zip'
    $archiveVerified = $false
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $expectedFiles = @(Get-ChildItem -LiteralPath $source -File)
            if ($archive.Entries.Count -ne $expectedFiles.Count) { throw 'ZIP 文件数与已校验目录不一致。' }
            foreach ($file in $expectedFiles) {
                $entry = $archive.GetEntry($file.Name)
                if ($null -eq $entry -or $entry.Length -ne $file.Length) { throw "ZIP 文件缺失或长度不符：$($file.Name)" }
                $stream = $entry.Open()
                try {
                    if ((Get-FileHash -InputStream $stream -Algorithm SHA256).Hash -ne
                        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw "ZIP 内容哈希不符：$($file.Name)" }
                } finally { $stream.Dispose() }
            }
            $archiveVerified = $true
            $results.Add([ordered]@{ name = 'ArchiveMatchesVerifiedDirectory'; passed = $true; rejected = $false })
        } finally { $archive.Dispose() }
    }
    Run-Case 'Valid' { param($folder) } ''
    Run-Case 'UnreleasedActivation' { param($folder) } '未开放自动恢复' 'RecoverStalled'
    $sourceIdentity = Get-Content -LiteralPath (Join-Path $source 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($sourceIdentity.schemaVersion -eq 2) {
        Run-Case 'MissingSymbols' { param($folder)
            Remove-Item -LiteralPath (Join-Path $folder 'MTTFTest.RecoveryGuard.pdb')
        } '文件缺失'
        Run-Case 'TamperedReadme' { param($folder)
            [IO.File]::AppendAllText((Join-Path $folder 'README.md'), 'tamper')
        } '文件损坏'
        Run-Case 'UnknownManifestSchema' { param($folder)
            $identity = Get-Content -LiteralPath (Join-Path $folder 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $identity.schemaVersion = 3
            Save-Identity $folder $identity
        } '交付阶段不受支持'
    }
    Run-Case 'TamperedBinary' { param($folder)
        [IO.File]::AppendAllText((Join-Path $folder 'MTTFTest.RecoveryControl.dll'), 'tamper')
    } '文件损坏'
    Run-Case 'TraversalEntry' { param($folder)
        $identity = Get-Content -LiteralPath (Join-Path $folder 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $identity.files[0].name = '..\escaped.exe'
        Save-Identity $folder $identity
    } '非预期文件'
    Run-Case 'VersionMismatch' { param($folder)
        $identity = Get-Content -LiteralPath (Join-Path $folder 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $identity.version = '99.0.0.0'
        Save-Identity $folder $identity
    } '版本不一致'
    Run-Case 'RecoveryModeNotReady' { param($folder)
        $settings = Get-Content -LiteralPath (Join-Path $folder 'guard-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $settings.Mode = 2
        Refresh-SettingsHash $folder $settings
    } '必须为 ObserveOnly'
    Run-Case 'WrongExpiry' { param($folder)
        $settings = Get-Content -LiteralPath (Join-Path $folder 'guard-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $settings.SupervisionExpirySeconds = 3601
        Refresh-SettingsHash $folder $settings
    } '60 分钟'
    Run-Case 'InvalidTimingRelation' { param($folder)
        $settings = Get-Content -LiteralPath (Join-Path $folder 'guard-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $settings.ScanSeconds = 0
        Refresh-SettingsHash $folder $settings
    } '配置校验失败'
    [ordered]@{ passed = $results.Count; cases = $results; archiveVerified = $archiveVerified; installationPerformed = $false } | ConvertTo-Json -Depth 6
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
exit 0
