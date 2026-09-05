[CmdletBinding()]
param([string]$InstallerPath = '')
$ErrorActionPreference = 'Stop'
$installer = if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'
} else { [IO.Path]::GetFullPath($InstallerPath) }
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$errors)
if (@($errors).Count) { throw 'UninstallScriptParseFailed' }
foreach ($name in @('Resolve-SafeDirectory', 'Stop-InstalledProcess',
        'Stop-InstalledRuntimeProcesses', 'Remove-InstalledProgramFiles')) {
    $definition = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true))
    if ($definition.Count -ne 1) { throw "UninstallFunctionMissing:$name" }
    Invoke-Expression $definition[0].Extent.Text
}
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'ExpectedUninstallRejection' }
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('EPB-Uninstall-' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $fixture 'MTTFTest'
$current = Join-Path $root 'Current'
$helper = $null
try {
    [void](New-Item -ItemType Directory -Path (Join-Path $current 'x86') -Force)
    $source = Join-Path $fixture 'Lock.cs'
    [IO.File]::WriteAllText($source, @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
class LockFixture {
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibrary(string path);
    static int Main(string[] args) {
        if (LoadLibrary(args[0]) == IntPtr.Zero) return 2;
        File.WriteAllText(args[1], "ready");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
'@)
    $executable = Join-Path $current 'MTTFTest.EngineHost.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    & $compiler /nologo /target:winexe /platform:x86 "/out:$executable" $source
    if ($LASTEXITCODE -ne 0) { throw 'UninstallFixtureCompileFailed' }
    $sqlite = Join-Path $current 'x86\SQLite.Interop.dll'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\MTTfTest\bin\Release\x86\SQLite.Interop.dll') -Destination $sqlite
    $marker = Join-Path $fixture 'ready'
    $helper = Start-Process -FilePath $executable -ArgumentList @('"' + $sqlite + '"', '"' + $marker + '"') -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $marker)) {
        if ($helper.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'SQLiteLockFixtureNotReady' }
        Start-Sleep -Milliseconds 50
    }
    Assert-Rejected { Remove-Item -LiteralPath $sqlite -Force -ErrorAction Stop }
    # Only enumerate this isolated helper. Never inspect/stop an installed runtime.
    & {
        function Get-CimInstance {
            param($ClassName, $Filter, $ErrorAction)
            if ($Filter -eq "Name='MTTFTest.EngineHost.exe'" -and -not $helper.HasExited) {
                [pscustomobject]@{ProcessId=$helper.Id; ExecutablePath=$executable}
            }
        }
        Stop-InstalledRuntimeProcesses $root
    }
    if (-not $helper.HasExited) { throw 'EngineHostLeftRunning' }
    Remove-InstalledProgramFiles $root
    if (Test-Path -LiteralPath $root) { throw 'SqliteStillLockedAfterUninstall' }
    Remove-InstalledProgramFiles $root
    Write-Output 'PASS UninstallStopsEngineHostAndReleasesRealSqliteModule 1/1'
    Write-Output 'PASS InterruptedUninstallCanBeRepeated 1/1'
    & {
        function Get-CimInstance { [pscustomobject]@{ProcessId=12345;ExecutablePath=''} }
        function Stop-Process { throw 'MustNotKillUnknownProcess' }
        Assert-Rejected { Stop-InstalledProcess $root 'MTTFTest.EngineHost.exe' }
    }
    & {
        function Get-CimInstance { [pscustomobject]@{ProcessId=12345;ExecutablePath='C:\Foreign\MTTFTest.EngineHost.exe'} }
        function Get-Process { throw 'MustNotTouchForeignProcess' }
        Stop-InstalledProcess $root 'MTTFTest.EngineHost.exe'
    }
    Assert-Rejected { Remove-InstalledProgramFiles $fixture }
    foreach ($failureMode in @('Denied', 'Timeout')) {
        & {
            $waitingProcess = [pscustomobject]@{Path=$executable; Handle=1}
            $waitingProcess | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $false }
            $waitingProcess | Add-Member ScriptMethod Dispose { }
            function Get-CimInstance { [pscustomobject]@{ProcessId=12345;ExecutablePath=$executable} }
            function Get-Process { return $waitingProcess }
            function Stop-Process { if ($failureMode -eq 'Denied') { throw 'AccessDenied' } }
            Assert-Rejected { Stop-InstalledProcess $root 'MTTFTest.EngineHost.exe' }
        }
    }
    Write-Output 'PASS UninstallStopFailureAndExitTimeoutCannotProceed 2/2'
    $text = [IO.File]::ReadAllText($installer)
    if (-not $text.Contains('Stop-InstalledRuntimeProcesses $root') -or
        $text.IndexOf('Stop-InstalledRuntimeProcesses $root') -ge $text.IndexOf('Remove-InstalledProgramFiles $root')) {
        throw 'UninstallMustJoinRuntimeBeforeDeletingFiles'
    }
    Write-Output 'PASS UninstallRejectsUnknownIdentityAndProtectsForeignPaths 3/3'
}
finally {
    if ($null -ne $helper) {
        if (-not $helper.HasExited) { $helper.Kill(); [void]$helper.WaitForExit(5000) }
        $helper.Dispose()
    }
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\EPB-Uninstall-'
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if ($resolvedFixture.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedFixture)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
