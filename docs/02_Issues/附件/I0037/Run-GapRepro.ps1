param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path)
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$binPath = Join-Path $repoPath 'Tests\AdaptiveControlTests\bin\Debug'
$cscPath = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe'
$facadePath = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\netstandard.dll'
$sourcePath = Join-Path $PSScriptRoot 'GapRepro.cs'
$exePath = Join-Path $binPath 'I0037GapRepro.exe'
foreach ($required in @($binPath, $cscPath, $facadePath, $sourcePath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required local build dependency missing: $required" }
}
$compileArgs = @('/nologo', '/target:exe', '/platform:x86', ('/out:' + $exePath),
    ('/r:' + (Join-Path $binPath 'DataOperation.dll')), ('/r:' + (Join-Path $binPath 'Config.dll')),
    ('/r:' + (Join-Path $binPath 'IO.NI.dll')), ('/r:' + (Join-Path $binPath 'System.Buffers.dll')),
    ('/r:' + $facadePath), $sourcePath)
& $cscPath $compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic harness compilation failed.' }
$evidenceRoot = Join-Path $repoPath 'artifacts\I0037-analysis'
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$runPath = Join-Path $evidenceRoot ('gap-run-' + [guid]::NewGuid().ToString('N'))
& $exePath $runPath 2>&1 | Tee-Object -FilePath (Join-Path $evidenceRoot 'gap-repro-result.txt')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic reproduction failed; inspect its output.' }
Write-Output ('Reproduction data retained at: ' + $runPath)
Write-Output 'Exit code 0 confirms the old defect; it does not mean a fix passed.'
