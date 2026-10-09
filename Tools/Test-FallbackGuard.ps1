[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ExecutablePath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($root) | Out-Null
$exe = [IO.Path]::GetFullPath($ExecutablePath)
$session = [Guid]::NewGuid().ToString('N')
$arguments = @('--project-directory', $root, '--session-id', $session)
$child = $null
$started = 0L
try {
    & $exe --enable @arguments
    if ($LASTEXITCODE -ne 0) { throw '启用失败' }
    $settingsPath = Join-Path $root "fallback-settings-$session.json"
    $settings = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
    if (-not $settings.Enabled -or $settings.Active) { throw '候选默认必须只观察' }
    $line = '--run --project-directory "{0}" --session-id {1}' -f $root,$session
    $child = Start-Process -FilePath $exe -ArgumentList $line -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'guard-stdout.log') -RedirectStandardError (Join-Path $root 'guard-stderr.log')
    $null = $child.Handle
    $started = $child.StartTime.ToUniversalTime().Ticks
    Start-Sleep -Seconds 2
    $child.Refresh()
    if ($child.HasExited) { throw '观察器提前退出' }
    $private = [long]$child.PrivateMemorySize64
    if ($private -gt 128MB) { throw '观察器超过内存目标' }
    & $exe --disable @arguments
    if ($LASTEXITCODE -ne 0 -or -not $child.WaitForExit(5000)) { throw '禁用后未按时退出' }
    $exitCode = $child.ExitCode
    if ($exitCode -ne 0) { throw "退出码=$exitCode" }
    & $exe --run @arguments
    if ($LASTEXITCODE -ne 0) { throw '禁用状态下任务入口异常' }
    $report = [ordered]@{ computer=[string]$env:COMPUTERNAME; powershell=[string]$PSVersionTable.PSVersion; utc=[DateTime]::UtcNow.ToString('O'); session=$session; defaultObservation=$true; independentDisable=$true; disabledTaskDoesNotRevive=$true; privateBytes=$private; childExited=$true; hardware='NOT_VERIFIED'; taskRegistration='NOT_EXECUTED'; activeTakeover='NOT_VERIFIED' }
    $report | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $root 'fallback-validation.json') -Encoding UTF8
    Write-Output 'PASS 独立启用/禁用、默认观察、后台退出、禁用不复活；未执行硬件或任务注册。'
}
finally {
    if ($child) {
        $child.Refresh()
        if (-not $child.HasExited -and $child.StartTime.ToUniversalTime().Ticks -eq $started) {
            # This test creates an isolated observer with no ledger, hardware
            # or ownership transaction. Clean up only its verified PID.
            $child.Kill()
            if (-not $child.WaitForExit(5000)) { throw '本地测试观察器未清理' }
        }
        $child.Dispose()
    }
}
