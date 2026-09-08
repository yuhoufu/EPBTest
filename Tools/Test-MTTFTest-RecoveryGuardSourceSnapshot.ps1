#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'New-MTTFTest-RecoveryGuardPackage.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-GuardPackageSourceSnapshot'
}, $true)
if ($null -eq $definition) { throw 'Snapshot function missing' }
Invoke-Expression $definition.Extent.Text
$baseline = Get-GuardPackageSourceSnapshot $repo
$root = Join-Path ([IO.Path]::GetTempPath()) ('GuardSourceSnapshot-' + [Guid]::NewGuid().ToString('N'))
foreach ($file in $baseline.files) {
    $target = Join-Path $root $file.path
    [void][IO.Directory]::CreateDirectory((Split-Path $target -Parent))
    Copy-Item -LiteralPath (Join-Path $repo $file.path) -Destination $target
}
$copied = Get-GuardPackageSourceSnapshot $root
if ($copied.fingerprint -cne $baseline.fingerprint) { throw 'Snapshot depends on checkout location' }
$passed = 1
foreach ($relative in @('MTTFTest.RecoveryGuard/Program.cs', 'docs/RecoveryGuard_安装包使用说明.md',
    'Tools/Invoke-MTTFTest-RecoveryGuardCommissioning.ps1', 'Tools/Verify-Release.ps1')) {
    $target = Join-Path $root $relative
    [IO.File]::AppendAllText($target, "`n// snapshot fixture change")
    if ((Get-GuardPackageSourceSnapshot $root).fingerprint -ceq $baseline.fingerprint) { throw "Missed input change $relative" }
    Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target -Force
    $passed++
}
$projectPath = Join-Path $root 'MTTFTest.RecoveryGuard/MTTFTest.RecoveryGuard.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$group = $project.Project.ItemGroup | Select-Object -First 1
$item = $project.CreateElement('Compile', $project.DocumentElement.NamespaceURI)
$item.SetAttribute('Include', 'UntrackedFixture.cs')
$null = $group.AppendChild($item)
$project.Save($projectPath)
[IO.File]::WriteAllText((Join-Path $root 'MTTFTest.RecoveryGuard/UntrackedFixture.cs'), 'class UntrackedFixture {}')
$withNew = Get-GuardPackageSourceSnapshot $root
if (-not @($withNew.files | Where-Object { $_.path -eq 'MTTFTest.RecoveryGuard/UntrackedFixture.cs' }).Count) { throw 'Untracked compile item omitted' }
$passed++
$item.SetAttribute('Include', '*.cs'); $project.Save($projectPath)
$rejected = $false
try { $null = Get-GuardPackageSourceSnapshot $root } catch { $rejected = $true }
if (-not $rejected) { throw 'Dynamic compile input accepted without coverage' }
$passed++
[ordered]@{ passed=$passed; inputFiles=$baseline.files.Count; fingerprint=$baseline.fingerprint; evidence=$root; repositoryModified=$false } | ConvertTo-Json
