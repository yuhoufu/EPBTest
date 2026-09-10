$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Test-RecoveryGuardPolicyOnJxcq.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$function = $ast.Find({ param($n)
    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-PolicyInputFiles'
}, $true)
. ([scriptblock]::Create($function.Extent.Text))
# Execute the actual local reverification statements only, never the remote entry point.
$text = [IO.File]::ReadAllText($scriptPath)
$start = $text.IndexOf('    $finalLocalNames = @(')
$end = $text.IndexOf('    [pscustomobject]@{ Configuration =', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Reverification block missing.' }
$verify = [scriptblock]::Create($text.Substring($start, $end - $start))
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('epb-policy-input-' + [guid]::NewGuid().ToString('N'))
$passed = 0
foreach ($scenario in @('unchanged', 'added', 'missing', 'same-size-change', 'renamed')) {
    $source = Join-Path $fixtureRoot $scenario
    foreach ($dir in @('x86', 'x64', 'Config')) {
        [void](New-Item -ItemType Directory -Path (Join-Path $source $dir) -Force)
    }
    $payload = Join-Path $source 'fixture.dll'
    [IO.File]::WriteAllText($payload, 'aaaa')
    $manifest = @([pscustomobject]@{ Name='fixture.dll'; Length=(Get-Item $payload).Length;
        Sha256=(Get-FileHash -LiteralPath $payload).Hash })
    switch ($scenario) {
        'added' { [IO.File]::WriteAllText((Join-Path $source 'Config/added.xml'), '<x/>') }
        'missing' { [IO.File]::Move($payload, (Join-Path $fixtureRoot 'missing-preserved.dll')) }
        'same-size-change' { [IO.File]::WriteAllText($payload, 'bbbb') }
        'renamed' { [IO.File]::Move($payload, (Join-Path $source 'renamed.dll')) }
    }
    $failure = $null
    try { & $verify } catch { $failure = $_.Exception.Message }
    if ($scenario -eq 'unchanged') {
        if ($failure) { throw $failure }
    } elseif ($failure -notlike 'Local input*changed during JXCQ validation*') {
        throw "Expected identity rejection for $scenario; actual=$failure"
    }
    $passed++
}
"PASS $passed/5 local input reverification; Evidence=$fixtureRoot"
