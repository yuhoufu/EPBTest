param([string]$Repository='D:\Github\wanxiang\EPBTest')
$ErrorActionPreference='Stop'
# 使用现场提交的真实许可栅栏；状态类型仅为编译未调用的 CanCreateRuntime 提供桩。
# 不加载应用，不连接硬件，不运行现场程序。
$source=(& git -C $Repository show '1185a09:Controller/ChannelExecutionFence.cs') -join "`n"
if($LASTEXITCODE -ne 0){throw 'Cannot read baseline source'}
$stub=@'
namespace Controller {
    internal enum ChannelRuntimeState { NotEnabled, AlarmStopped, InterlockStopped, ManualStopped, Completed, StartBlocked, SystemFault }
    internal class ChannelRuntimeStateChangedEvent { internal ChannelRuntimeState State { get; set; } }
    public static class I0049FenceReplay {
        public static bool[] Run() {
            var fence = new ChannelExecutionFence();
            var original = fence.Authorize(7, 1);
            bool before = fence.IsCurrent(original);
            bool revoked = fence.RevokeIfCurrent(original);
            bool oldCurrent = fence.IsCurrent(original);
            bool recapturedCurrent = fence.IsCurrent(fence.Capture(7));
            return new[] { before, revoked, oldCurrent, recapturedCurrent };
        }
    }
}
'@
Add-Type -TypeDefinition ($source + "`n" + $stub)
$actual=[Controller.I0049FenceReplay]::Run()
if(-not $actual[0] -or -not $actual[1] -or $actual[2] -or $actual[3]) {throw 'Unexpected fence result'}
$e=Get-Content (Join-Path $PSScriptRoot 'findings.json') -Raw | ConvertFrom-Json
$hash=$e.ReceiptSafetyAgentHash
$ordinal=[string]::Equals($hash,$hash.ToUpperInvariant(),[StringComparison]::Ordinal)
$digest=[string]::Equals($hash,$hash.ToUpperInvariant(),[StringComparison]::OrdinalIgnoreCase)
if($ordinal -or -not $digest){throw 'Unexpected hash comparison'}
$result=[ordered]@{
    SourceCommit='1185a09d62e84b1982c648168b371118d17b9ce4';
    OriginalPermitCurrentBeforeRetirement=$actual[0];RevokeSucceeded=$actual[1];
    OldPermitCurrentAfterRetirement=$actual[2];RecapturedPermitCurrentAfterRetirement=$actual[3];
    SameDigestOrdinalEquals=$ordinal;SameDigestCaseInsensitiveEquals=$digest;
    Scope='Actual ChannelExecutionFence in isolation and exact hash predicate; not a hardware/end-to-end test';
    Result='PASS'
}
$result | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'predicate-replay.json') -Encoding utf8
$result | ConvertTo-Json
