#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-MTTFTest-RecoveryGuard.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-GuardMainComponents'
}, $true)
if ($null -eq $definition) { throw 'Component preflight missing' }
Invoke-Expression $definition.Extent.Text
$source = Join-Path $repo 'MTTfTest/bin/Release'
$required = @('MTTFTest.exe', 'MTTFTest.Watchdog.exe', 'MTTFTest.SessionAgent.exe', 'MTTFTest.SafetyAgent.exe',
    'MTTFTest.SafetyHardware.dll', 'MTTFTest.Watchdog.Protocol.dll', 'MTTFTest.Watchdog.Client.dll',
    'MTTFTest.RecoveryControl.dll', 'Controller.dll')
$root = Join-Path ([IO.Path]::GetTempPath()) ('GuardComponentPreflight-' + [Guid]::NewGuid().ToString('N'))
$results = @()
foreach ($case in @('Valid', 'MissingAgent', 'DirtyIdentity', 'StagingIdentity', 'WrongProtocol', 'DuplicateEntry', 'BinaryTampered', 'WrongAssembly', 'GuardCoreMismatch', 'MissingMarker')) {
    $folder = Join-Path $root $case
    $guard = Join-Path $folder 'guard'
    [void][IO.Directory]::CreateDirectory($guard)
    $entries = foreach ($name in $required) {
        $path = Join-Path $folder $name
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $path
        [pscustomobject]@{ name=$name; fileVersion='3.0.0.0'; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
    Copy-Item -LiteralPath (Join-Path $folder 'MTTFTest.RecoveryControl.dll') -Destination $guard
    [IO.File]::WriteAllText((Join-Path $folder 'MTTFTest.UnattendedMode.required'), '')
    # Synthetic metadata belongs only to copied fixtures; never a release approval.
    $identity = [ordered]@{ deploymentApproved=$true; gitDirty=$false; releaseStatus='FORMAL_RELEASE'; fileVersion='3.0.0.0'; assemblyName='MTTFTest';
        mainExecutableSha256=(Get-FileHash -LiteralPath (Join-Path $folder 'MTTFTest.exe') -Algorithm SHA256).Hash;
        watchdogSchema=7; sessionAgentSchema=8; componentIdentities=@($entries); fixtureOnly=$true }
    switch ($case) {
        'MissingAgent' { [IO.File]::Delete((Join-Path $folder 'MTTFTest.SessionAgent.exe')) }
        'DirtyIdentity' { $identity.gitDirty = $true }
        'StagingIdentity' { $identity.releaseStatus = 'BUILD_STAGING_NOT_FOR_DEPLOYMENT' }
        'WrongProtocol' { $identity.sessionAgentSchema = 7 }
        'DuplicateEntry' { $identity.componentIdentities += $entries[0] }
        'BinaryTampered' { [IO.File]::AppendAllText((Join-Path $folder 'MTTFTest.SessionAgent.exe'), 'tamper') }
        'WrongAssembly' {
            $path = Join-Path $folder 'MTTFTest.SessionAgent.exe'
            Copy-Item -LiteralPath (Join-Path $folder 'Controller.dll') -Destination $path -Force
            ($entries | Where-Object { $_.name -eq 'MTTFTest.SessionAgent.exe' }).sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
        'GuardCoreMismatch' { [IO.File]::AppendAllText((Join-Path $guard 'MTTFTest.RecoveryControl.dll'), 'different-build') }
        'MissingMarker' { [IO.File]::Delete((Join-Path $folder 'MTTFTest.UnattendedMode.required')) }
    }
    $identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $folder 'build-identity.json') -Encoding UTF8
    $rejected = $false; $reason = ''
    try { Assert-GuardMainComponents (Join-Path $folder 'MTTFTest.exe') $guard '3.0.0.0' }
    catch { $rejected = $true; $reason = $_.Exception.Message }
    if ($rejected -ne ($case -ne 'Valid')) { throw "$case unexpected preflight outcome: $reason" }
    $results += [ordered]@{ name=$case; passed=$true; rejected=$rejected; reason=$reason }
}
[ordered]@{ passed=$results.Count; cases=$results; evidence=$root; installationPerformed=$false; fixtureApprovalOnly=$true } | ConvertTo-Json -Depth 6
