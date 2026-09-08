#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TestScript,
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [switch]$GuardViaSystemTask
)

$ErrorActionPreference = 'Stop'
$startedUtc = [DateTime]::UtcNow
$outputPath = [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($ResultPath), '.log')
$exitCode = 1
$failure = $null
try {
    $parameters = @{
        PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
        ConfirmIsolatedEnvironment = $true
        EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
        GuardViaSystemTask = [bool]$GuardViaSystemTask
    }
    $output = @(& ([IO.Path]::GetFullPath($TestScript)) @parameters 2>&1)
    $output | Set-Content -LiteralPath $outputPath -Encoding UTF8
    $exitCode = 0
}
catch {
    $failure = $_.Exception.ToString()
    @($output) + @($_ | Out-String) | Set-Content -LiteralPath $outputPath -Encoding UTF8
}
finally {
    $record = [ordered]@{
        schemaVersion = 1
        startedUtc = $startedUtc.ToString('O')
        completedUtc = [DateTime]::UtcNow.ToString('O')
        computer = $env:COMPUTERNAME
        account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        testScriptSha256 = (Get-FileHash -LiteralPath $TestScript -Algorithm SHA256).Hash
        packageIdentitySha256 = (Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'e2e-package-identity.json') -Algorithm SHA256).Hash
        guardViaSystemTask = [bool]$GuardViaSystemTask
        exitCode = $exitCode
        failure = $failure
        outputPath = $outputPath
    }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath),
        ($record | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
}
exit $exitCode
