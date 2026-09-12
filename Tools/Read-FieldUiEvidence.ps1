#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$ExpectedProcessId
)
$ErrorActionPreference='Stop'
# PS 5.1 returns a JSON array as one pipeline object. Assign first, then enumerate.
$parsed=Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json
if($null -eq $parsed){throw 'UiEvidenceMissing'}
$items=@($parsed)
if($items.Count -eq 0){throw 'UiEvidenceEmpty'}
foreach($item in $items){
    if($null -eq $item -or $item.Name -isnot [string] -or $item.Type -isnot [string] -or
        $item.Offscreen -isnot [bool] -or $item.Enabled -isnot [bool]){throw 'UiEvidenceShapeInvalid'}
}
$pidText='[PID '+$ExpectedProcessId+']'
$windows=@($items|Where-Object {$_.Type -ceq 'ControlType.Window' -and -not $_.Offscreen -and $_.Name.Contains($pidText)})
[pscustomobject]@{
    EvidencePath=[IO.Path]::GetFullPath($Path)
    EvidenceSha256=(Get-FileHash -LiteralPath $Path).Hash
    ExpectedProcessId=$ExpectedProcessId
    ElementCount=$items.Count
    VisibleMatchingWindows=@($windows|ForEach-Object {$_.Name})
    WindowWasObserved=($windows.Count -gt 0)
    LiveHealthProven=$false
    RollbackAuthorized=$false
    Note='历史窗口证据只证明采样时看到窗口。无匹配窗口不等于启动失败；须复核采样身份、时效和实际业务状态。'
}|ConvertTo-Json -Depth 4
