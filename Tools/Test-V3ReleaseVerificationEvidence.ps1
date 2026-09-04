[CmdletBinding()]
param([switch]$RunOriginalUiTests)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$verifyPath = Join-Path $PSScriptRoot 'Verify-Release.ps1'
$buildPath = Join-Path $PSScriptRoot 'Build-Release.ps1'

function Read-ScriptAst([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "脚本语法错误：$Path $parseErrors" }
    return $ast
}

function Read-FunctionBody($Ast, [string]$Name) {
    $functionNodes = @($Ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
    }, $true))
    if ($functionNodes.Count -ne 1) { throw "缺少唯一的生产函数：$Name" }
    return [scriptblock]::Create($functionNodes[0].Extent.Text)
}

# Execute only the side-effect-free production evidence validators, never the
# package verifier/build entry points. All counters below are synthetic fixtures.
$verifyAst = Read-ScriptAst $verifyPath
foreach ($functionName in @('Get-RequiredJsonProperty', 'Assert-CompletePassSummary', 'Assert-VerificationEvidence')) {
    . (Read-FunctionBody $verifyAst $functionName)
}

function New-TestEvidence {
    return [pscustomobject]@{
        solutionRebuild = 'PASS'
        adaptiveControlTests = 'PASS 10/10'
        epbDiskWriterTests = 'PASS 10/10'
        recoveryKernelTests = 'PASS 10/10'
        engineHostIntegrationTests = 'PASS 10/10'
        originalUiTests = 'PASS 10/10'
        powerSupplyDebuggerTests = 'PASS 10/10'
        releaseBuildNoMandatorySoak = 'PASS ReleaseBuildNoMandatorySoak 1/1'
        fieldGateTests = 'Ran 10 tests in 0.1s'
    }
}

$passed = 0
Assert-VerificationEvidence (New-TestEvidence)
$passed++
foreach ($field in @('recoveryKernelTests', 'engineHostIntegrationTests', 'originalUiTests')) {
    foreach ($invalid in @('MISSING', 'NOT_RUN', 'PASS 0/0', 'PASS 9/10', 'OriginalUiTests: 10 passed, 1 failed', 10)) {
        $evidence = New-TestEvidence
        if ($invalid -ceq 'MISSING') { $evidence.PSObject.Properties.Remove($field) }
        else { $evidence.$field = $invalid }
        $rejection = $null
        try { Assert-VerificationEvidence $evidence }
        catch { $rejection = $_.Exception.Message }
        if ([string]::IsNullOrWhiteSpace($rejection) -or $rejection -notmatch [regex]::Escape($field)) {
            throw "发布核验没有正确拒绝 $field / $invalid；实际结果：$rejection"
        }
        $passed++
    }
}

$buildAst = Read-ScriptAst $buildPath
$uiCalls = @($buildAst.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Invoke-CandidateTest'
}, $true) | Where-Object { $_.Extent.Text -match "-Label\s+'OriginalUiTests'" })
if ($uiCalls.Count -ne 1 -or $uiCalls[0].Extent.Text -notmatch '-FilePath\s+\$originalUiTestExe' -or
    $buildAst.Extent.Text -notmatch 'originalUiTests\s*=\s*\$originalUiSummary') {
    throw '发布构建未执行原界面测试，或未把摘要纳入构建身份。'
}
$passed++

if ($RunOriginalUiTests) {
    # Exercise the actual hidden-process/captured-output runner in isolation.
    # This does not enable any completion flag, build a package or run hardware.
    . (Read-FunctionBody $buildAst 'Invoke-CandidateTest')
    $uiExe = Join-Path $repo 'Tests\OriginalUiTests\bin\Release\OriginalUiTests.exe'
    $summary = Invoke-CandidateTest -Label 'OriginalUiTests' -FilePath $uiExe `
        -SuccessPattern '^OriginalUiTests: ([1-9]\d*) passed, 0 failed$'
    if ($summary -notmatch '^OriginalUiTests: ([1-9]\d*) passed, 0 failed$') {
        throw "原界面发布执行器没有得到完整通过摘要：$summary"
    }
    $passed++
}
Write-Output "PASS V3ReleaseVerificationEvidence $passed/$passed"
