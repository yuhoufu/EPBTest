using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class WatchdogTransportHardeningTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("Attached返回冻结Sidecar身份并保持协议v4往返", AttachedIdentityRoundTrips, ref passed);
            Run("协议版本严格限制为v4", ProtocolVersionMustBeExactV4, ref passed);
            Run("Watchdog超过20KiB长帧完整性往返", LongIntegrityFrameRoundTrips, ref passed);
            Run("Watchdog半帧与校验篡改明确拒绝", PartialOrCorruptFrameIsRejected, ref passed);
            Run("真实Sidecar进程拒绝v2/0/v3并接受v4", RealSidecarRejectsInvalidProtocolVersions, ref passed);
            Run("真实Sidecar缺失或非法challenge nonce时启动失败",
                RealSidecarRejectsMissingOrInvalidLaunchNonce, ref passed);
            Run("真实Sidecar同权威新管道重连不重置Stop域",
                RealSidecarAllowsSameAuthorityReconnect, ref passed);
            Run("恢复许可Committed后同PID与代次重连保持主进程",
                RealSidecarAllowsCommittedRecoveryReconnect, ref passed);
            Run("真实Sidecar附着断开/待握手死亡/启动失败50次句柄回基线",
                RealSidecarAttachReconnectAndStartupFailureReturnsToBaseline, ref passed);
            Run("生产句柄所有权50次循环回到基线", ProcessHandleOwnerReturnsToBaseline, ref passed);
            Run("Sidecar权威身份必须同时匹配Session/PID/Start/Nonce",
                AuthorityIdentityRequiresCompleteEvidence, ref passed);
            Run("传输阶段常量与SessionAttach预算保持单一策略值",
                TransportPolicyBudgetsAreExplicit, ref passed);
            Run("心跳超时但UI响应时只退休管道且不接管",
                ResponsiveUiSuppressesHeartbeatTakeover, ref passed);
            Run("0.5/3/6秒传输延迟矩阵仅在6秒启用UI证据",
                TransportDelayMatrixUsesIndependentUiEvidence, ref passed);
            Run("UI连续三次无响应才确认心跳接管",
                UiProbeRequiresThreeFailures, ref passed);
            Run("同一进程代次的UI失活确认只发布一次",
                UiFailureConfirmationIsLatchedPerProcess, ref passed);
            Run("进程退出与PID身份失配均立即确认接管",
                ProcessExitAndIdentityMismatchAreImmediate, ref passed);
            Run("MainUiReady前保留15秒启动宽限",
                StartupGracePreventsEarlyTakeover, ref passed);
            Run("MainUiReady和UI失败证据跨同进程管道重连保留",
                MainUiReadySurvivesConnectionReconnect, ref passed);
            Run("Host发送队列生命周期消息优先且与普通流量隔离",
                HostSendQueuePrioritizesLifecycleMessages, ref passed);
            Run("Host连续100代发送队列精确隔离",
                HundredSendQueueGenerationsAreIsolated, ref passed);
            Run("重连退避固定为250/500/1000/2000/5000且最多8次",
                ReconnectBackoffIsBounded, ref passed);
            Run("旧权威死亡后只允许一个新pending helper",
                DeadAuthorityAllowsOneReplacement, ref passed);
            Run("pending helper超过5秒仍保持单一reservation",
                SlowPendingHelperIsIdempotent, ref passed);
            Run("非法Attached不清除pending，随后合法Attached可接管",
                InvalidAttachedDoesNotSuppressFallback, ref passed);
            Run("pending challenge nonce冻结且错误nonce不清除reservation",
                PendingChallengeNonceCannotBeReplaced, ref passed);
            Run("pending缺失/错误/过期nonce均拒绝且四元组仅精确匹配",
                PendingNonceMustBePresentAndExact, ref passed);
            Run("旧generation/迟到Attached被拒绝",
                LateAttachedGenerationIsRejected, ref passed);
            Run("旧authority的nonce/start不能覆盖新握手",
                AuthorityIdentityCannotBeReplacedByStaleAttached, ref passed);
            Run("并发重连只产生一个pending helper",
                ConcurrentPendingReservationIsSingle, ref passed);
            return passed;
        }

        private static void ResponsiveUiSuppressesHeartbeatTakeover()
        {
            var probe = new ControlledUiProbe(
                WatchdogUiProbeStatus.Responsive,
                WatchdogUiProbeStatus.Responsive);
            var supervisor = new WatchdogApplicationLivenessSupervisor(probe);
            supervisor.ResetForNewProcess(7, 1000);
            supervisor.ObserveMainUiReady(7);
            var first = supervisor.Evaluate(
                6, true, 42, 100, 7, 3, 7000, 1000);
            var second = supervisor.Evaluate(
                7, true, 42, 100, 7, 3, 8000, 1000);
            Assert(first.SuppressHeartbeatTakeover && first.RetireConnection &&
                   !first.HeartbeatUnresponsiveConfirmed &&
                   second.SuppressHeartbeatTakeover && !second.RetireConnection,
                "UI响应未抑制心跳误接管，或同一连接被重复退休");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 0,
                    false, false, false, 90, true,
                    formalProgressStalled: true),
                "UI存活证据错误抑制了正式机械进度停滞接管");
        }

        private static void TransportDelayMatrixUsesIndependentUiEvidence()
        {
            var supervisor = new WatchdogApplicationLivenessSupervisor(
                new ControlledUiProbe(WatchdogUiProbeStatus.Responsive));
            supervisor.ResetForNewProcess(8, 1000);
            supervisor.ObserveMainUiReady(8);
            var halfSecond = supervisor.Evaluate(
                0.5, true, 42, 100, 8, 1, 1500, 1000);
            var threeSeconds = supervisor.Evaluate(
                3, true, 42, 100, 8, 1, 4000, 1000);
            var sixSeconds = supervisor.Evaluate(
                6, true, 42, 100, 8, 1, 7000, 1000);
            Assert(halfSecond.ProbeStatus == WatchdogUiProbeStatus.NotRequired &&
                   threeSeconds.ProbeStatus == WatchdogUiProbeStatus.NotRequired &&
                   !halfSecond.HeartbeatUnresponsiveConfirmed &&
                   !threeSeconds.HeartbeatUnresponsiveConfirmed &&
                   sixSeconds.ProbeStatus == WatchdogUiProbeStatus.Responsive &&
                   sixSeconds.SuppressHeartbeatTakeover &&
                   sixSeconds.RetireConnection,
                "传输延迟矩阵未将5秒前心跳怀疑与5秒后独立UI证据分层");
        }

        private static void UiProbeRequiresThreeFailures()
        {
            var probe = new ControlledUiProbe(
                WatchdogUiProbeStatus.Unresponsive,
                WatchdogUiProbeStatus.Unresponsive,
                WatchdogUiProbeStatus.Unresponsive);
            var supervisor = new WatchdogApplicationLivenessSupervisor(probe);
            supervisor.ResetForNewProcess(9, 1000);
            supervisor.ObserveMainUiReady(9);
            var first = supervisor.Evaluate(6, true, 42, 100, 9, 1, 7000, 1000);
            var second = supervisor.Evaluate(7, true, 42, 100, 9, 1, 8000, 1000);
            var third = supervisor.Evaluate(8, true, 42, 100, 9, 1, 9000, 1000);
            Assert(first.SuppressHeartbeatTakeover && second.SuppressHeartbeatTakeover &&
                   !first.HeartbeatUnresponsiveConfirmed &&
                   !second.HeartbeatUnresponsiveConfirmed &&
                   third.HeartbeatUnresponsiveConfirmed &&
                   third.ConsecutiveFailures == 3,
                "UI探针未严格执行连续三次失败门禁");
        }

        private static void UiFailureConfirmationIsLatchedPerProcess()
        {
            var supervisor = new WatchdogApplicationLivenessSupervisor(
                new ControlledUiProbe(
                    WatchdogUiProbeStatus.Unresponsive,
                    WatchdogUiProbeStatus.Unresponsive,
                    WatchdogUiProbeStatus.Unresponsive,
                    WatchdogUiProbeStatus.Unresponsive));
            supervisor.ResetForNewProcess(90, 1000);
            supervisor.ObserveMainUiReady(90);
            supervisor.Evaluate(6, true, 42, 100, 90, 1, 7000, 1000);
            supervisor.Evaluate(7, true, 42, 100, 90, 1, 8000, 1000);
            var confirmed = supervisor.Evaluate(8, true, 42, 100, 90, 1, 9000, 1000);
            var repeated = supervisor.Evaluate(9, true, 42, 100, 90, 1, 10000, 1000);
            Assert(confirmed.HeartbeatUnresponsiveConfirmed && confirmed.ReportEvent &&
                   repeated.HeartbeatUnresponsiveConfirmed && !repeated.ReportEvent &&
                   repeated.ConsecutiveFailures == 3,
                "HeartbeatUnresponsiveConfirmed在同一进程代次被重复发布");
        }

        private static void ProcessExitAndIdentityMismatchAreImmediate()
        {
            var exited = new WatchdogApplicationLivenessSupervisor(
                new ControlledUiProbe(WatchdogUiProbeStatus.Responsive));
            exited.ResetForNewProcess(10, 1000);
            var processExit = exited.Evaluate(
                0.5, false, 42, 100, 10, 1, 1500, 1000);

            var mismatch = new WatchdogApplicationLivenessSupervisor(
                new ControlledUiProbe(WatchdogUiProbeStatus.IdentityMismatch));
            mismatch.ResetForNewProcess(11, 1000);
            mismatch.ObserveMainUiReady(11);
            var identityMismatch = mismatch.Evaluate(
                6, true, 42, 100, 11, 1, 7000, 1000);
            Assert(processExit.HeartbeatUnresponsiveConfirmed &&
                   processExit.ProbeStatus == WatchdogUiProbeStatus.ProcessExited &&
                   identityMismatch.HeartbeatUnresponsiveConfirmed &&
                   identityMismatch.ProbeStatus == WatchdogUiProbeStatus.IdentityMismatch,
                "进程退出或PID启动时间身份失配未绕过三次普通UI失败门禁立即接管");
        }

        private static void StartupGracePreventsEarlyTakeover()
        {
            var probe = new ControlledUiProbe(
                WatchdogUiProbeStatus.WindowMissing,
                WatchdogUiProbeStatus.WindowMissing,
                WatchdogUiProbeStatus.WindowMissing);
            var supervisor = new WatchdogApplicationLivenessSupervisor(probe);
            supervisor.ResetForNewProcess(11, 1000);
            var grace = supervisor.Evaluate(6, true, 42, 100, 11, 1, 15000, 1000);
            var first = supervisor.Evaluate(15, true, 42, 100, 11, 1, 16000, 1000);
            var second = supervisor.Evaluate(16, true, 42, 100, 11, 1, 17000, 1000);
            var third = supervisor.Evaluate(17, true, 42, 100, 11, 1, 18000, 1000);
            Assert(grace.ProbeStatus == WatchdogUiProbeStatus.StartupGrace &&
                   grace.SuppressHeartbeatTakeover &&
                   !first.HeartbeatUnresponsiveConfirmed &&
                   !second.HeartbeatUnresponsiveConfirmed &&
                   third.HeartbeatUnresponsiveConfirmed,
                "MainUiReady前启动宽限或宽限后的三次失败门禁不正确");
        }

        private static void MainUiReadySurvivesConnectionReconnect()
        {
            var supervisor = new WatchdogApplicationLivenessSupervisor(
                new ControlledUiProbe(
                    WatchdogUiProbeStatus.Unresponsive,
                    WatchdogUiProbeStatus.Unresponsive,
                    WatchdogUiProbeStatus.Unresponsive));
            supervisor.ResetForNewProcess(12, 1000);
            supervisor.ObserveMainUiReady(12);
            var beforeReconnect = supervisor.Evaluate(
                6, true, 42, 100, 12, 1, 7000, 1000);
            var afterReconnect = supervisor.Evaluate(
                7, true, 42, 100, 12, 2, 8000, 1000);
            var confirmed = supervisor.Evaluate(
                8, true, 42, 100, 12, 2, 9000, 1000);
            Assert(beforeReconnect.ProbeStatus == WatchdogUiProbeStatus.Unresponsive &&
                   afterReconnect.ProbeStatus == WatchdogUiProbeStatus.Unresponsive &&
                   afterReconnect.ConsecutiveFailures == 2 &&
                   confirmed.HeartbeatUnresponsiveConfirmed &&
                   confirmed.ConsecutiveFailures == 3,
                "同进程重连错误重置MainUiReady或连续UI失败证据");
        }

        private static void HostSendQueuePrioritizesLifecycleMessages()
        {
            using (var stream = new MemoryStream())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
            {
                var owner = new WatchdogHostSendQueueOwner(stream, writer, 5);
                var normal = new WatchdogHostSendRequest("Ping", "normal", false);
                var lifecycle = new WatchdogHostSendRequest(
                    WatchdogMessageType.RequestStopAll,
                    "lifecycle",
                    true);
                Assert(owner.TryEnqueue(normal) && owner.TryEnqueue(lifecycle),
                    "Host发送队列拒绝正常容量内消息");
                WatchdogHostSendRequest first;
                WatchdogHostSendRequest second;
                Assert(owner.TryDequeue(out first) && ReferenceEquals(first, lifecycle) &&
                       owner.TryDequeue(out second) && ReferenceEquals(second, normal),
                    "生命周期消息未越过普通流量优先发送");
                owner.StopAccepting();
                Assert(!owner.TryEnqueue(new WatchdogHostSendRequest("Ping", "late", false)),
                    "连接退休后发送队列仍接纳新消息");
            }
        }

        private static void HundredSendQueueGenerationsAreIsolated()
        {
            for (var generation = 1; generation <= 100; generation++)
            {
                using (var stream = new MemoryStream())
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
                {
                    var owner = new WatchdogHostSendQueueOwner(stream, writer, generation);
                    var request = new WatchdogHostSendRequest(
                        "Ping",
                        "generation-" + generation,
                        false);
                    Assert(owner.TryEnqueue(request),
                        "发送队列在正常容量内拒绝代次" + generation);
                    owner.StopAccepting();
                    Assert(request.Completion.Task.IsCompleted &&
                           !request.Completion.Task.GetAwaiter().GetResult() &&
                           !owner.TryEnqueue(new WatchdogHostSendRequest(
                               "Ping", "late-" + generation, false)),
                        "退休代次仍接纳发送或未完成遗留请求：" + generation);
                }
            }
        }

        private sealed class ControlledUiProbe : IWatchdogUiProbe
        {
            private readonly Queue<WatchdogUiProbeStatus> _statuses;

            internal ControlledUiProbe(params WatchdogUiProbeStatus[] statuses)
            {
                _statuses = new Queue<WatchdogUiProbeStatus>(statuses ?? Array.Empty<WatchdogUiProbeStatus>());
            }

            public WatchdogUiProbeResult Probe(
                int processId,
                long processStartUtcTicks,
                int timeoutMs)
            {
                return new WatchdogUiProbeResult
                {
                    Status = _statuses.Count > 0
                        ? _statuses.Dequeue()
                        : WatchdogUiProbeStatus.Responsive,
                    Detail = "Controlled"
                };
            }
        }

        private static void TransportPolicyBudgetsAreExplicit()
        {
            Assert(WatchdogTransportPolicy.PipeConnect5000 == 5000 &&
                   WatchdogTransportPolicy.Guard5500 == 5500 &&
                   WatchdogTransportPolicy.Handshake5000 == 5000 &&
                   WatchdogTransportPolicy.ConnectFailureJoin1000 == 1000 &&
                   WatchdogTransportPolicy.ReconnectInitial250 == 250 &&
                   WatchdogTransportPolicy.SidecarLaunchAllowance5000 == 5000 &&
                   WatchdogTransportPolicy.LaunchClosureJoin1000 == 1000 &&
                   WatchdogTransportPolicy.AttachDispatchGuard1000 == 1000 &&
                   WatchdogTransportPolicy.ProcessExitJoin1000 == 1000 &&
                   WatchdogTransportPolicy.HeartbeatSuspectMs == 3000 &&
                   WatchdogTransportPolicy.HeartbeatTimeoutMs == 5000 &&
                   WatchdogTransportPolicy.UiProbeTimeoutMs == 500 &&
                   WatchdogTransportPolicy.ClientAckRetireJitterMs == 250 &&
                   WatchdogTransportPolicy.ClientHeartbeatAckRetireMs == 5750,
                "传输原子阶段常量发生漂移");
            Assert(WatchdogTransportPolicy.ConnectAttemptBudgetMs == 6500 &&
                   WatchdogTransportPolicy.GuardedConnectBudgetMs == 11100 &&
                   WatchdogTransportPolicy.HandshakeBudgetMs == 19000 &&
                   WatchdogTransportPolicy.RecoveryConnectBudgetMs == 75600 &&
                   WatchdogTransportPolicy.ReconnectBackoffBudgetMs == 23750 &&
                   WatchdogTransportPolicy.ReconnectSupervisorBudgetMs == 75750 &&
                   WatchdogTransportPolicy.SessionAttachDeadline == 158850 &&
                   WatchdogTransportPolicy.SessionAttachDeadlineMs == 158850,
                "SessionAttach预算未采用评审后的固定值");
            Assert(WatchdogTransportPolicy.ReconnectBackoffBudgetMs ==
                   250 + 500 + 1000 + 2000 + (5000 * 4),
                "重连退避总预算与8次退避表不一致");
        }

        private static void LongIntegrityFrameRoundTrips()
        {
            var message = new WatchdogMessage
            {
                ProtocolVersion = WatchdogProtocol.Version,
                Type = WatchdogMessageType.Heartbeat,
                SessionId = "long-frame-session",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Reason = new string('安', 25000)
            };
            var payload = WatchdogProtocol.Serialize(message);
            var frame = WatchdogWireFrame.Encode(payload);
            string decoded;
            string failure;
            var decodedSuccessfully = WatchdogWireFrame.TryDecode(frame, out decoded, out failure);
            Assert(Encoding.UTF8.GetByteCount(payload) > 20 * 1024 &&
                   decodedSuccessfully &&
                   string.Equals(decoded, payload, StringComparison.Ordinal),
                "长帧没有按长度与SHA-256完整解码：" + failure);
            var roundTrip = WatchdogProtocol.Deserialize(frame);
            Assert(roundTrip != null && roundTrip.Reason == message.Reason &&
                   roundTrip.CorrelationId == message.CorrelationId,
                "长帧协议往返丢失字段");
        }

        private static void PartialOrCorruptFrameIsRejected()
        {
            var payload = WatchdogProtocol.Serialize(new WatchdogMessage
            {
                ProtocolVersion = WatchdogProtocol.Version,
                Type = WatchdogMessageType.Heartbeat,
                SessionId = "partial-frame-session",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Reason = new string('x', 4096)
            });
            var frame = WatchdogWireFrame.Encode(payload);
            var truncated = frame.Substring(0, frame.Length - 17);
            Assert(!WatchdogWireFrame.TryDecode(truncated, out _, out var partialFailure) &&
                   (partialFailure == "FramePayloadIncomplete" ||
                    partialFailure == "FrameLengthMismatch"),
                "EOF半帧未被分类为传输完整性失败：" + partialFailure);

            var chars = frame.ToCharArray();
            var payloadIndex = frame.LastIndexOf('|') + 1;
            chars[payloadIndex + 8] = chars[payloadIndex + 8] == 'A' ? 'B' : 'A';
            var corrupt = new string(chars);
            Assert(!WatchdogWireFrame.TryDecode(corrupt, out _, out var checksumFailure) &&
                   checksumFailure == "FrameChecksumMismatch",
                "已篡改完整帧未被SHA-256拒绝：" + checksumFailure);
        }

        private static void AttachedIdentityRoundTrips()
        {
            var message = new WatchdogMessage
            {
                Type = WatchdogMessageType.Attached,
                SessionId = "session-transport",
                SidecarProcessId = 321,
                SidecarProcessStartUtcTicks = 987654321,
                SidecarStartUtcTicks = 987654321,
                StartUtcTicks = 987654321,
                SidecarAuthoritySessionId = "session-transport",
                SidecarSessionId = "session-transport",
                SidecarInstanceNonce = "nonce-1",
                InstanceNonce = "nonce-1"
            };
            var roundTrip = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(message));
            Assert(WatchdogProtocol.Version == 4 &&
                   roundTrip.ProtocolVersion == 4 &&
                   roundTrip.Type == WatchdogMessageType.Attached &&
                   roundTrip.SessionId == "session-transport" &&
                   roundTrip.SidecarProcessId == 321 &&
                   roundTrip.SidecarProcessStartUtcTicks == 987654321 &&
                   roundTrip.SidecarStartUtcTicks == 987654321 &&
                   roundTrip.StartUtcTicks == 987654321 &&
                   roundTrip.SidecarAuthoritySessionId == "session-transport" &&
                   roundTrip.SidecarSessionId == "session-transport" &&
                   roundTrip.SidecarInstanceNonce == "nonce-1" &&
                   roundTrip.InstanceNonce == "nonce-1",
                "Attached身份字段未能按v4协议往返");
        }

        private static void AuthorityIdentityRequiresCompleteEvidence()
        {
            Assert(WatchdogProcessIdentityPolicy.IsValidChallengeNonce(
                       "0123456789abcdef0123456789abcdef") &&
                   !WatchdogProcessIdentityPolicy.IsValidChallengeNonce("nonce-1") &&
                   !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(
                       "0123456789abcdef0123456789abcde"),
                "128-bit challenge nonce格式门禁错误");
            Assert(WatchdogProcessIdentityPolicy.HasAuthoritativeIdentity(
                       "session-transport", "session-transport", 321, 987654321, "nonce-1"),
                "完整身份不能被识别为权威");
            Assert(!WatchdogProcessIdentityPolicy.HasAuthoritativeIdentity(
                       "session-transport", "other-session", 321, 987654321, "nonce-1") &&
                   !WatchdogProcessIdentityPolicy.HasAuthoritativeIdentity(
                       "session-transport", "session-transport", 0, 987654321, "nonce-1") &&
                   !WatchdogProcessIdentityPolicy.HasAuthoritativeIdentity(
                       "session-transport", "session-transport", 321, 0, "nonce-1") &&
                   !WatchdogProcessIdentityPolicy.HasAuthoritativeIdentity(
                       "session-transport", "session-transport", 321, 987654321, ""),
                "缺失Session/PID/Start/Nonce时错误授予权威");
        }

        private static void ProtocolVersionMustBeExactV4()
        {
            Assert(WatchdogProtocol.IsSupportedVersion(WatchdogProtocol.Version),
                "当前v4协议被错误拒绝");
            Assert(!WatchdogProtocol.IsSupportedVersion(0) &&
                   !WatchdogProtocol.IsSupportedVersion(2) &&
                   !WatchdogProtocol.IsSupportedVersion(3),
                "v2/0/v3协议没有被严格拒绝");

            foreach (var version in new[] { 0, 2, 3 })
            {
                var message = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(
                    new WatchdogMessage
                    {
                        ProtocolVersion = version,
                        Type = WatchdogMessageType.Attached,
                        SessionId = "session-version"
                    }));
                Assert(message != null &&
                       !WatchdogProtocol.IsSupportedVersion(message.ProtocolVersion),
                    "非法协议版本在往返后被视为可握手：" + version);
            }
        }

        private static void ProcessHandleOwnerReturnsToBaseline()
        {
            var baseline = SidecarProcessHandleOwner.LiveOwnedCount;
            var processHandleBaseline = CurrentProcessHandleCount();
            var command = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            for (var index = 0; index < 50; index++)
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = "/c exit 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                Assert(process != null, "测试helper进程启动失败，iteration=" + index);
                var owner = new SidecarProcessHandleOwner(process);
                process.WaitForExit(2000);
                owner.Dispose();
                owner.Dispose();
                Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                    "重复Dispose后owned handle未回到基线，iteration=" + index);
                AssertProcessHandleCountBounded(
                    processHandleBaseline,
                    "重复Dispose后OS handle未回到有界基线，iteration=" + index);
            }
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                "50次helper循环后owned handle泄漏");
            AssertProcessHandleCountBounded(
                processHandleBaseline,
                "50次helper循环后OS handle超出有界基线");
        }

        private static void RealSidecarRejectsInvalidProtocolVersions()
        {
            var sidecarPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "MTTFTest.Watchdog.exe");
            Assert(File.Exists(sidecarPath), "测试输出目录缺少真实Sidecar：" + sidecarPath);
            // Use a fresh process and a fresh pipe for every version.  This
            // proves that an invalid connection cannot poison a later v4
            // connection, and does not accidentally accept v4 after the
            // reader has already observed an invalid frame on the same pipe.
            foreach (var version in new[] { 0, 2, 3, WatchdogProtocol.Version })
            {
                var projectDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "MTTFTest.Transport." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(projectDirectory);
                // The production Journal emergency path intentionally accepts
                // only the canonical GUID session token.
                var session = Guid.NewGuid().ToString("N");
                var pipeName = "MTTFTest.Transport." + Guid.NewGuid().ToString("N");
                var launchNonce = Guid.NewGuid().ToString("N");
                Process sidecar = null;
                NamedPipeClientStream pipe = null;
                try
                {
                    using (var current = Process.GetCurrentProcess())
                    {
                        var executable = current.MainModule?.FileName ??
                                         AppDomain.CurrentDomain.BaseDirectory + "AdaptiveControlTests.exe";
                        var startTicks = current.StartTime.ToUniversalTime().Ticks;
                        var bootstrap = DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                            projectDirectory,
                            session,
                            current.Id,
                            startTicks,
                            RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit);
                        Assert(bootstrap.Succeeded,
                            "真实Sidecar strict authority初始化失败，version=" + version +
                            ";reason=" + bootstrap.Reason);
                        sidecar = Process.Start(new ProcessStartInfo
                        {
                            FileName = sidecarPath,
                            Arguments = string.Join(" ",
                                "--parent-pid", current.Id.ToString(),
                                "--parent-start-ticks", startTicks.ToString(),
                                "--session", QuoteArg(session),
                                "--pipe", QuoteArg(pipeName),
                                "--executable", QuoteArg(executable),
                                "--journal-directory", QuoteArg(projectDirectory),
                                "--sidecar-instance-nonce", launchNonce),
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                    }
                    Assert(sidecar != null, "真实Sidecar启动失败，version=" + version);
                    pipe = new NamedPipeClientStream(
                        ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.Connect(5000);
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true)
                    {
                        AutoFlush = true
                    })
                {
                    SendAttach(writer, session, pipeName, version);
                    var responseTask = reader.ReadLineAsync();
                    if (version != WatchdogProtocol.Version)
                    {
                        // Invalid protocol frames are rejected by closing the
                        // connection; no Attached response or authority may
                        // be produced.  The bounded read also detects an
                        // accidental response without hanging the suite.
                        if (responseTask.Wait(1500))
                        {
                            var line = responseTask.Result;
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                var response = WatchdogProtocol.Deserialize(line);
                                Assert(response == null || response.Type != WatchdogMessageType.Attached,
                                    "非法协议版本产生Attached，version=" + version);
                            }
                        }
                    }
                    else
                    {
                Assert(responseTask.Wait(5000), "真实Sidecar未返回v4 Attached");
                        var response = WatchdogProtocol.Deserialize(responseTask.Result);
                        Assert(response != null &&
                               response.Type == WatchdogMessageType.Attached &&
                               WatchdogProtocol.IsSupportedVersion(response.ProtocolVersion) &&
                               response.SidecarProcessId > 0 &&
                               response.SidecarProcessStartUtcTicks > 0 &&
                               response.SidecarInstanceNonce == launchNonce &&
                               response.InstanceNonce == launchNonce,
                    "真实Sidecar未按v4返回完整Attached身份");

                        writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                        {
                            ProtocolVersion = WatchdogProtocol.Version,
                            Type = WatchdogMessageType.ApplicationClosing,
                            SessionId = session,
                            Reason = "TransportIntegrationTestComplete"
                        }));
                    }
                }
                }
                finally
                {
                    try { pipe?.Dispose(); } catch { }
                    if (sidecar != null)
                    {
                        try
                        {
                            if (!sidecar.HasExited)
                            {
                                sidecar.WaitForExit(3000);
                                if (!sidecar.HasExited) sidecar.Kill();
                            }
                        }
                        catch { }
                        try { sidecar.Dispose(); } catch { }
                    }
                    try { Directory.Delete(projectDirectory, true); } catch { }
                }
            }
        }

        private static void RealSidecarRejectsMissingOrInvalidLaunchNonce()
        {
            var sidecarPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "MTTFTest.Watchdog.exe");
            Assert(File.Exists(sidecarPath), "测试输出目录缺少真实Sidecar：" + sidecarPath);
            foreach (var nonce in new[] { null, "not-a-128-bit-nonce" })
            {
                var projectDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "MTTFTest.NonceReject." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(projectDirectory);
                var session = Guid.NewGuid().ToString("N");
                var pipeName = "MTTFTest.NonceReject." + session;
                Process sidecar = null;
                try
                {
                    using (var current = Process.GetCurrentProcess())
                    {
                        var startTicks = current.StartTime.ToUniversalTime().Ticks;
                        var args = string.Join(" ",
                            "--parent-pid", current.Id.ToString(),
                            "--parent-start-ticks", startTicks.ToString(),
                            "--session", QuoteArg(session),
                            "--pipe", QuoteArg(pipeName),
                            "--executable", QuoteArg(current.MainModule?.FileName),
                            "--journal-directory", QuoteArg(projectDirectory));
                        if (nonce != null)
                            args += " --sidecar-instance-nonce " + QuoteArg(nonce);
                        sidecar = Process.Start(new ProcessStartInfo
                        {
                            FileName = sidecarPath,
                            Arguments = args,
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                    }
                    Assert(sidecar != null, "非法nonce场景Sidecar未启动进程");
                    Assert(sidecar.WaitForExit(5000),
                        "缺失/非法nonce的Sidecar未在有界时间内退出：" + nonce);
                    Assert(sidecar.ExitCode == 2,
                        "缺失/非法nonce的Sidecar退出码不是2：" + sidecar.ExitCode);
                }
                finally
                {
                    if (sidecar != null)
                    {
                        try
                        {
                            if (!sidecar.HasExited) sidecar.Kill();
                        }
                        catch { }
                        try { sidecar.Dispose(); } catch { }
                    }
                    try { Directory.Delete(projectDirectory, true); } catch { }
                }
            }
        }

        private static void RealSidecarAllowsSameAuthorityReconnect()
        {
            var sidecarPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "MTTFTest.Watchdog.exe");
            Assert(File.Exists(sidecarPath), "测试输出目录缺少真实Sidecar：" + sidecarPath);
            var projectDirectory = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.Reattach." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectDirectory);
            var session = Guid.NewGuid().ToString("N");
            var pipeName = "MTTFTest.Reattach." + session;
            var nonce = Guid.NewGuid().ToString("N");
            Process sidecar = null;
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    var startTicks = current.StartTime.ToUniversalTime().Ticks;
                    var bootstrap = DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                        projectDirectory,
                        session,
                        current.Id,
                        startTicks,
                        RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit);
                    Assert(bootstrap.Succeeded,
                        "同权威重连 strict authority初始化失败：" + bootstrap.Reason);
                    sidecar = Process.Start(new ProcessStartInfo
                    {
                        FileName = sidecarPath,
                        Arguments = string.Join(" ",
                            "--parent-pid", current.Id.ToString(),
                            "--parent-start-ticks", startTicks.ToString(),
                            "--session", QuoteArg(session),
                            "--pipe", QuoteArg(pipeName),
                            "--executable", QuoteArg(current.MainModule?.FileName),
                            "--journal-directory", QuoteArg(projectDirectory),
                            "--sidecar-instance-nonce", nonce),
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    Assert(sidecar != null, "同权威重连Sidecar启动失败");
                    using (var first = new NamedPipeClientStream(
                               ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        first.Connect(5000);
                        using (var reader = new StreamReader(first, new UTF8Encoding(false), false, 4096, true))
                        using (var writer = new StreamWriter(first, new UTF8Encoding(false), 4096, true)
                               { AutoFlush = true })
                        {
                            SendAttach(writer, session, pipeName, WatchdogProtocol.Version);
                            var attached = ReadMessage(reader, TimeSpan.FromSeconds(5));
                            Assert(attached?.Type == WatchdogMessageType.Attached &&
                                   attached.SidecarInstanceNonce == nonce,
                                "首次Attach未完成严格nonce promotion");
                            SendAttach(writer, session, pipeName, WatchdogProtocol.Version);
                            var duplicate = reader.ReadLineAsync();
                            Assert(!duplicate.Wait(500),
                                "同一命名管道连接重复Attach不应再次返回Attached");
                        }
                    }

                    // The first pipe is closed, but the exact parent PID/start
                    // authority remains live.  A second pipe must be accepted
                    // as a reconnect without a new authority epoch.
                    using (var second = new NamedPipeClientStream(
                               ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        second.Connect(5000);
                        using (var reader = new StreamReader(second, new UTF8Encoding(false), false, 4096, true))
                        using (var writer = new StreamWriter(second, new UTF8Encoding(false), 4096, true)
                               { AutoFlush = true })
                        {
                            SendAttach(writer, session, pipeName, WatchdogProtocol.Version);
                            var reattached = ReadMessage(reader, TimeSpan.FromSeconds(5));
                            Assert(reattached?.Type == WatchdogMessageType.Attached &&
                                   reattached.SidecarInstanceNonce == nonce,
                                "同权威新管道re-attach未被接受");
                            writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                            {
                                Type = WatchdogMessageType.ApplicationClosing,
                                SessionId = session,
                                Reason = "SameAuthorityReconnectComplete"
                            }));
                        }
                    }
                }
                Assert(sidecar.WaitForExit(5000),
                    "同权威重连测试Sidecar未按ApplicationClosing退出");
            }
            finally
            {
                if (sidecar != null)
                {
                    try
                    {
                        if (!sidecar.HasExited) sidecar.Kill();
                    }
                    catch { }
                    try { sidecar.Dispose(); } catch { }
                }
                try { Directory.Delete(projectDirectory, true); } catch { }
            }
        }

        private static void RealSidecarAllowsCommittedRecoveryReconnect()
        {
            var sidecarPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "MTTFTest.Watchdog.exe");
            Assert(File.Exists(sidecarPath), "测试输出目录缺少真实Sidecar：" + sidecarPath);
            var projectDirectory = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.RecoveryReattach." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectDirectory);
            var session = Guid.NewGuid().ToString("N");
            var pipeName = "MTTFTest.RecoveryReattach." + session;
            var nonce = Guid.NewGuid().ToString("N");
            Process sidecar = null;
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    var startTicks = current.StartTime.ToUniversalTime().Ticks;
                    var created = DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                        projectDirectory,
                        session,
                        current.Id,
                        startTicks,
                        RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit);
                    Assert(created.Succeeded,
                        "恢复重连strict authority初始化失败：" + created.Reason);
                    var failure = created.Authority.RegisterFailureAndDecide(
                        CreateRecoveryFailureOperation(session));
                    Assert(failure.ActionAllowed &&
                           failure.Record?.State == DurableRelaunchPermitState.Approved,
                        "恢复重连未生成Approved许可：" + failure.Reason);
                    var intent = created.Authority.PrepareLaunchIntent(
                        CreateLaunchIntent(failure.Record, session));
                    Assert(intent.Succeeded && intent.Capability != null,
                        "恢复重连未生成LaunchIntent：" + intent.Reason);
                    Assert(created.Authority.ConsumeLaunchIntent(intent.Capability).Succeeded,
                        "恢复重连LaunchIntent未耐久消费");
                    var started = created.Authority.CommitStarted(
                        intent.Capability,
                        current.Id,
                        startTicks);
                    Assert(started.Succeeded &&
                           started.Record?.State == DurableRelaunchPermitState.Started,
                        "恢复重连未提交Started：" + started.Reason);
                    var recoveryPermit = started.Record.Clone();

                    sidecar = Process.Start(new ProcessStartInfo
                    {
                        FileName = sidecarPath,
                        Arguments = string.Join(" ",
                            "--parent-pid", current.Id.ToString(),
                            "--parent-start-ticks", startTicks.ToString(),
                            "--session", QuoteArg(session),
                            "--pipe", QuoteArg(pipeName),
                            "--executable", QuoteArg(current.MainModule?.FileName),
                            "--journal-directory", QuoteArg(projectDirectory),
                            "--sidecar-instance-nonce", nonce),
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    Assert(sidecar != null, "恢复重连Sidecar启动失败");

                    using (var first = new NamedPipeClientStream(
                               ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        first.Connect(5000);
                        using (var reader = new StreamReader(first, new UTF8Encoding(false), false, 4096, true))
                        using (var writer = new StreamWriter(first, new UTF8Encoding(false), 4096, true)
                               { AutoFlush = true })
                        {
                            SendAttach(
                                writer,
                                session,
                                pipeName,
                                WatchdogProtocol.Version,
                                recoveryPermit);
                            var attached = ReadMessage(reader, TimeSpan.FromSeconds(5));
                            Assert(attached?.Type == WatchdogMessageType.Attached,
                                "恢复进程首次Attach未被接受");
                            writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                            {
                                Type = WatchdogMessageType.RecoveryBatchCommitted,
                                SessionId = session,
                                CorrelationId = Guid.NewGuid().ToString("N"),
                                RecoveryCommitGeneration = 1,
                                RecoveryProgressToken = "recovery-commit-1",
                                Reason = "RecoveryCommittedBeforeReconnect",
                                Heartbeat = CreateRecoveryHeartbeat(
                                    session,
                                    current.Id,
                                    startTicks,
                                    1)
                            }));
                            Assert(SpinWait.SpinUntil(
                                    () => DurableRelaunchAuthorityV4Factory
                                              .TryOpenExisting(projectDirectory, session)
                                              .Authority?.Snapshot?.State ==
                                          DurableRelaunchPermitState.Committed,
                                    5000),
                                "恢复批次未在断管前提交Committed");
                            writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                            {
                                Type = WatchdogMessageType.Heartbeat,
                                SessionId = session,
                                CorrelationId = Guid.NewGuid().ToString("N"),
                                Heartbeat = CreateRecoveryHeartbeat(
                                    session,
                                    current.Id,
                                    startTicks,
                                    1)
                            }));
                            var pendingPauseAck = ReadMessage(
                                reader,
                                TimeSpan.FromSeconds(5));
                            Assert(pendingPauseAck?.Type == WatchdogMessageType.HeartbeatAck &&
                                   pendingPauseAck.AckSequence == 1,
                                "ManualPausePending心跳未被确认");
                        }
                    }

                    using (var second = new NamedPipeClientStream(
                               ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        second.Connect(5000);
                        using (var reader = new StreamReader(second, new UTF8Encoding(false), false, 4096, true))
                        using (var writer = new StreamWriter(second, new UTF8Encoding(false), 4096, true)
                               { AutoFlush = true })
                        {
                            SendAttach(
                                writer,
                                session,
                                pipeName,
                                WatchdogProtocol.Version,
                                recoveryPermit);
                            var reattached = ReadMessage(reader, TimeSpan.FromSeconds(5));
                            Assert(reattached?.Type == WatchdogMessageType.Attached,
                                "Committed恢复进程同身份重连被误阻断");
                            writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                            {
                                Type = WatchdogMessageType.Heartbeat,
                                SessionId = session,
                                CorrelationId = Guid.NewGuid().ToString("N"),
                                Heartbeat = CreateRecoveryHeartbeat(
                                    session,
                                    current.Id,
                                    startTicks,
                                    2)
                            }));
                            var ack = ReadMessage(reader, TimeSpan.FromSeconds(5));
                            Assert(ack?.Type == WatchdogMessageType.HeartbeatAck &&
                                   ack.AckSequence == 2,
                                "Committed恢复重连后的首条Heartbeat未被确认");
                            writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                            {
                                Type = WatchdogMessageType.ApplicationClosing,
                                SessionId = session,
                                Reason = "CommittedRecoveryReconnectComplete"
                            }));
                        }
                    }
                }
                Assert(sidecar.WaitForExit(5000),
                    "Committed恢复重连测试Sidecar未正常退出");
                var events = string.Join(
                    Environment.NewLine,
                    Directory.GetFiles(projectDirectory, "*events.jsonl")
                        .Select(File.ReadAllText));
                Assert(events.Contains("AttachedReconnectValidated"),
                    "Committed恢复重连未记录AttachedReconnectValidated");
                Assert(!events.Contains("DurableRecoveryBlocked") &&
                       !events.Contains("RecoveryReconnectRejected"),
                    "Committed恢复重连错误进入恢复阻断");
            }
            finally
            {
                if (sidecar != null)
                {
                    try { if (!sidecar.HasExited) sidecar.Kill(); } catch { }
                    try { sidecar.Dispose(); } catch { }
                }
                try { Directory.Delete(projectDirectory, true); } catch { }
            }
        }

        private static void RealSidecarAttachReconnectAndStartupFailureReturnsToBaseline()
        {
            var sidecarPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "MTTFTest.Watchdog.exe");
            Assert(File.Exists(sidecarPath), "测试输出目录缺少真实Sidecar：" + sidecarPath);
            var baseline = SidecarProcessHandleOwner.LiveOwnedCount;
            var processHandleBaseline = CurrentProcessHandleCount();
            for (var iteration = 0; iteration < 50; iteration++)
            {
                var projectDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "MTTFTest.Transport50." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(projectDirectory);
                var session = Guid.NewGuid().ToString("N");
                var pipeName = "MTTFTest.Transport50." + session;
                var nonce = Guid.NewGuid().ToString("N");
                Process pendingSidecar = null;
                Process promotedSidecar = null;
                SidecarProcessHandleOwner pendingOwner = null;
                SidecarProcessHandleOwner promotedOwner = null;
                try
                {
                    using (var current = Process.GetCurrentProcess())
                    {
                        var startTicks = current.StartTime.ToUniversalTime().Ticks;
                        var bootstrap = DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                            projectDirectory,
                            session,
                            current.Id,
                            startTicks,
                            RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit);
                        Assert(bootstrap.Succeeded,
                            "50轮真实Helper strict authority初始化失败，iteration=" + iteration +
                            ";reason=" + bootstrap.Reason);
                        var common = string.Join(" ",
                            "--parent-pid", current.Id.ToString(),
                            "--parent-start-ticks", startTicks.ToString(),
                            "--session", QuoteArg(session),
                            "--pipe", QuoteArg(pipeName),
                            "--executable", QuoteArg(current.MainModule?.FileName),
                            "--journal-directory", QuoteArg(projectDirectory));

                        // A live helper with no Attach is a pending helper.  Its
                        // death must not retain a process handle or suppress the
                        // replacement launch in the next half of this cycle.
                        pendingSidecar = Process.Start(new ProcessStartInfo
                        {
                            FileName = sidecarPath,
                            Arguments = common + " --sidecar-instance-nonce " + nonce,
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                        Assert(pendingSidecar != null,
                            "pending helper启动失败，iteration=" + iteration);
                        pendingOwner = new SidecarProcessHandleOwner(
                            Process.GetProcessById(pendingSidecar.Id));
                        using (var pendingPipe = new NamedPipeClientStream(
                                   ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                        {
                            pendingPipe.Connect(5000);
                        }
                        if (!pendingSidecar.HasExited) pendingSidecar.Kill();
                        Assert(pendingSidecar.WaitForExit(5000),
                            "pending helper未在有界时间内死亡，iteration=" + iteration);
                        pendingOwner.Dispose();
                        pendingOwner = null;
                        Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                            "pending helper死亡后句柄未回基线，iteration=" + iteration);

                        var replacementNonce = Guid.NewGuid().ToString("N");
                        promotedSidecar = Process.Start(new ProcessStartInfo
                        {
                            FileName = sidecarPath,
                            Arguments = common + " --sidecar-instance-nonce " + replacementNonce,
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                        Assert(promotedSidecar != null,
                            "replacement helper启动失败，iteration=" + iteration);
                        promotedOwner = new SidecarProcessHandleOwner(
                            Process.GetProcessById(promotedSidecar.Id));
                        using (var pipe = new NamedPipeClientStream(
                                   ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                        {
                            pipe.Connect(5000);
                            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true)
                                   { AutoFlush = true })
                            {
                                SendAttach(writer, session, pipeName, WatchdogProtocol.Version);
                                var attached = ReadMessage(reader, TimeSpan.FromSeconds(5));
                                Assert(attached?.Type == WatchdogMessageType.Attached &&
                                       attached.SidecarInstanceNonce == replacementNonce,
                                    "replacement helper promotion失败，iteration=" + iteration);
                                writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                                {
                                    Type = WatchdogMessageType.ApplicationClosing,
                                    SessionId = session,
                                    Reason = "Transport50Complete"
                                }));
                            }
                        }
                        Assert(promotedSidecar.WaitForExit(5000),
                            "promoted helper未按有界关闭退出，iteration=" + iteration);
                        promotedOwner.Dispose();
                        promotedOwner = null;
                        Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                            "promotion/close后句柄未回基线，iteration=" + iteration);

                        // A startup failure is a real sidecar process, not a
                        // swallowed test exception: omit the required nonce and
                        // assert the executable returns its documented failure.
                        var failedSession = Guid.NewGuid().ToString("N");
                        var failedPipe = "MTTFTest.Transport50.Fail." + failedSession;
                        var failed = Process.Start(new ProcessStartInfo
                        {
                            FileName = sidecarPath,
                            Arguments = string.Join(" ",
                                "--parent-pid", current.Id.ToString(),
                                "--parent-start-ticks", startTicks.ToString(),
                                "--session", QuoteArg(failedSession),
                                "--pipe", QuoteArg(failedPipe),
                                "--executable", QuoteArg(current.MainModule?.FileName),
                                "--journal-directory", QuoteArg(projectDirectory)),
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                        Assert(failed != null && failed.WaitForExit(5000),
                            "startup failure helper未有界退出，iteration=" + iteration);
                        Assert(failed.ExitCode == 2,
                            "startup failure helper退出码错误，iteration=" + iteration +
                            ";Exit=" + failed.ExitCode);
                        failed.Dispose();
                    }
                }
                finally
                {
                    try { pendingOwner?.Dispose(); } catch { }
                    try { promotedOwner?.Dispose(); } catch { }
                    if (pendingSidecar != null)
                    {
                        try { if (!pendingSidecar.HasExited) pendingSidecar.Kill(); } catch { }
                        try { pendingSidecar.Dispose(); } catch { }
                    }
                    if (promotedSidecar != null)
                    {
                        try { if (!promotedSidecar.HasExited) promotedSidecar.Kill(); } catch { }
                        try { promotedSidecar.Dispose(); } catch { }
                    }
                    try { Directory.Delete(projectDirectory, true); } catch { }
                }
                Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                    "50次真实Sidecar循环结束后句柄未回基线，iteration=" + iteration);
                AssertProcessHandleCountBounded(
                    processHandleBaseline,
                    "50次真实Sidecar循环后OS handle超出有界基线，iteration=" + iteration);
            }
            AssertProcessHandleCountBounded(
                processHandleBaseline,
                "真实Sidecar循环结束后OS handle超出有界基线");
        }

        private static void SendAttach(
            StreamWriter writer,
            string session,
            string pipeName,
            int protocolVersion)
        {
            SendAttach(writer, session, pipeName, protocolVersion, null);
        }

        private static void SendAttach(
            StreamWriter writer,
            string session,
            string pipeName,
            int protocolVersion,
            DurableRelaunchAuthorityRecord recoveryPermit)
        {
            using (var current = Process.GetCurrentProcess())
            {
                writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                {
                    ProtocolVersion = protocolVersion,
                    Type = WatchdogMessageType.Attach,
                    SessionId = session,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Session = new WatchdogRunSession
                    {
                        SessionId = session,
                        PipeName = pipeName,
                        ExecutablePath = current.MainModule?.FileName,
                        ProcessId = current.Id,
                        ProcessStartUtcTicks = current.StartTime.ToUniversalTime().Ticks,
                        RecoveryProcess = recoveryPermit != null,
                        RelaunchGeneration = recoveryPermit?.Generation ?? 0,
                        RelaunchPermitId = recoveryPermit?.PermitId,
                        RelaunchPermitNonce = recoveryPermit?.PermitNonce
                    }
                }));
            }
        }

        private static WatchdogHeartbeat CreateRecoveryHeartbeat(
            string session,
            int processId,
            long processStartUtcTicks,
            long sequence)
        {
            return new WatchdogHeartbeat
            {
                Sequence = sequence,
                SessionId = session,
                ProcessId = processId,
                ProcessStartUtcTicks = processStartUtcTicks,
                RunId = "recovered-run",
                RunEpoch = 2,
                Phase = "Formal",
                RecoveryStage = "RecoveryCommitted",
                RecoveryProgressVersion = sequence,
                RecoveryBatchCommitGeneration = sequence >= 1 ? 1 : 0,
                ManualPauseActive = true,
                ManualPausePending = sequence == 1,
                ManualPauseStage = sequence == 1
                    ? WatchdogManualPauseStage.PersistenceDrain.ToString()
                    : WatchdogManualPauseStage.Completed.ToString(),
                ManualPauseProgressVersion = sequence,
                ManualPauseStageStartedUtc = DateTime.UtcNow.Ticks,
                ManualPauseHardDeadlineUtc = DateTime.UtcNow.AddSeconds(30).Ticks,
                OutputsConfirmedOff = true,
                EnabledChannels = Array.Empty<int>(),
                EligibleChannels = Array.Empty<int>()
            };
        }

        private static RecoveryFailureOperation CreateRecoveryFailureOperation(string session)
        {
            using (var current = Process.GetCurrentProcess())
            {
                return new RecoveryFailureOperation
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    SessionId = session,
                    SessionNonce = new string('a', 32),
                    SidecarProcessId = current.Id,
                    SidecarProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    ConnectionGeneration = 1,
                    RecoveryAttemptGeneration = 1,
                    RequestCorrelationId = Guid.NewGuid().ToString("N"),
                    RequestPayloadSha256 = new string('B', 64),
                    FailureCode = "CommittedReconnectFixture",
                    FailureFingerprint = "CommittedReconnectFixture",
                    DetailCode = "CommittedReconnectFixture",
                    RunId = "failed-run",
                    RunEpoch = 1,
                    RecoveryStage = "Recovery",
                    RecoveryProgressToken = "failed-progress-1",
                    RecoveryProcessSource = "TransportFixture",
                    DeviceOrChannelGroup = "Host",
                    MaximumProcessRelaunches =
                        RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit
                };
            }
        }

        private static DurableLaunchIntent CreateLaunchIntent(
            DurableRelaunchAuthorityRecord record,
            string session)
        {
            var executable = Path.GetFullPath(
                Process.GetCurrentProcess().MainModule.FileName);
            string executableSha256;
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(
                       executable,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
                executableSha256 = BitConverter.ToString(sha.ComputeHash(stream))
                    .Replace("-", string.Empty)
                    .ToUpperInvariant();
            var intent = new DurableLaunchIntent
            {
                SessionId = session,
                SessionNonce = new string('a', 32),
                Generation = record.Generation,
                PermitId = record.PermitId,
                PermitNonce = record.PermitNonce,
                IntentId = Guid.NewGuid().ToString("N"),
                ExecutablePath = executable,
                ExecutableSha256 = executableSha256,
                Arguments = "--committed-reconnect-fixture",
                WorkingDirectory = Path.GetDirectoryName(executable),
                LaunchOptionsCanonical = "UseShellExecute=false;CreateNoWindow=true"
            };
            intent.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(intent);
            return intent;
        }

        private static WatchdogMessage ReadMessage(
            StreamReader reader,
            TimeSpan timeout)
        {
            var read = reader.ReadLineAsync();
            Assert(read.Wait(timeout),
                "真实Sidecar在有界时间内未返回协议消息。");
            return WatchdogProtocol.Deserialize(read.Result);
        }

        private static string QuoteArg(string value)
        {
            // ProcessStartInfo.Arguments is parsed by the Windows command
            // line parser; ordinary path backslashes must not be doubled.
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static void ReconnectBackoffIsBounded()
        {
            var expected = new[] { 250, 500, 1000, 2000, 5000, 5000, 5000, 5000 };
            Assert(WatchdogTransportPolicy.ReconnectMaxAttempts == expected.Length,
                "重连最大次数不是8次");
            for (var index = 0; index < expected.Length; index++)
                Assert(WatchdogTransportPolicy.SelectReconnectBackoffMs(index) == expected[index],
                    "第" + index + "次重连退避不符合固定策略");
            Assert(WatchdogTransportPolicy.SelectReconnectBackoffMs(100) == 5000,
                "超过预算后的退避不能超过5秒");
        }

        private static void DeadAuthorityAllowsOneReplacement()
        {
            var state = new SidecarIdentityStateMachine();
            var authority = new SidecarIdentityStateMachine.AuthoritySnapshot(
                101, 1001, "session-dead", TestNonce(1));
            Assert(state.TrySeedValidatedAuthority(authority, "session-dead", out var seedError), seedError);
            Assert(state.TryClearAuthority(authority), "死亡权威未能原子清除");

            Assert(state.TryReservePending(
                       202, 2002, "session-dead", 7,
                       TestNonce(2),
                       out var pending, out var firstError), firstError);
            Assert(!state.TryReservePending(
                         303, 3003, "session-dead", 7,
                         TestNonce(3),
                         out _, out var secondError) &&
                   secondError.Contains("another pending"),
                "旧权威死亡后允许第二个pending helper");
            Assert(state.Pending == pending && state.Pending.ProcessId == 202,
                "替代helper reservation不是唯一且稳定的pending快照");
        }

        private static void SlowPendingHelperIsIdempotent()
        {
            var state = new SidecarIdentityStateMachine();
            SidecarIdentityStateMachine.PendingSnapshot first = null;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                Assert(state.TryReservePending(
                           404, 4004, "session-slow", 8,
                           TestNonce(4),
                           out var current, out var error), error);
                first = first ?? current;
                Assert(ReferenceEquals(first, current),
                    "同一慢启动helper重复连接没有复用同一reservation");
            }
            Assert(state.HasPending && !state.HasAuthority,
                "慢启动期间pending状态错误地变成/丢失了authority");
        }

        private static void InvalidAttachedDoesNotSuppressFallback()
        {
            var state = new SidecarIdentityStateMachine();
            Assert(state.TryReservePending(
                       505, 5005, "session-attached", 9,
                       TestNonce(5),
                       out _, out var reserveError), reserveError);
            var invalid = new SidecarIdentityStateMachine.AuthoritySnapshot(
                506, 5005, "session-attached", TestNonce(6));
            Assert(!state.CanAcceptAttached(
                        invalid, "session-attached", 9, out var invalidError) &&
                   invalidError.Contains("pending"),
                "非法Attached未被pending身份门禁拒绝");
            Assert(state.HasPending && !state.HasAuthority,
                "非法Attached改变了握手/authority状态");

            var valid = new SidecarIdentityStateMachine.AuthoritySnapshot(
                505, 5005, "session-attached", TestNonce(5));
            Assert(state.TryPromoteAttached(
                       valid, "session-attached", 9, out var promotionError), promotionError);
            Assert(!state.HasPending && state.HasAuthority,
                "后续合法Attached未能接管并清除pending");
        }

        private static void PendingChallengeNonceCannotBeReplaced()
        {
            var state = new SidecarIdentityStateMachine();
            const string session = "session-pending-nonce";
            const string expectedNonce = "0123456789abcdef0123456789abcdef";
            const string wrongNonce = "fedcba9876543210fedcba9876543210";
            Assert(state.TryReservePending(
                       515,
                       5151,
                       session,
                       15,
                       expectedNonce,
                       out var pending,
                       out var reserveError), reserveError);
            Assert(pending.InstanceNonce == expectedNonce,
                "pending snapshot未冻结challenge nonce");
            var wrong = new SidecarIdentityStateMachine.AuthoritySnapshot(
                515, 5151, session, wrongNonce);
            Assert(!state.CanAcceptAttached(
                        wrong, session, 15, out var wrongError) &&
                   wrongError.Contains("pending"),
                "错误challenge nonce覆盖了pending reservation");
            Assert(state.HasPending && !state.HasAuthority &&
                   state.Pending.InstanceNonce == expectedNonce,
                "错误challenge nonce改变了pending/authority状态");
            var valid = new SidecarIdentityStateMachine.AuthoritySnapshot(
                515, 5151, session, expectedNonce);
            Assert(state.TryPromoteAttached(
                       valid, session, 15, out var promotionError), promotionError);
            Assert(!state.HasPending && state.HasAuthority &&
                   state.Authority.InstanceNonce == expectedNonce,
                 "正确challenge nonce未能promotion");
        }

        private static void PendingNonceMustBePresentAndExact()
        {
            var state = new SidecarIdentityStateMachine();
            const string session = "session-exact-quad";
            var nonce = TestNonce(20);
            Assert(state.TryReservePending(
                       520,
                       5201,
                       session,
                       20,
                       nonce,
                       out var pending,
                       out var reserveError), reserveError);

            var missing = new SidecarIdentityStateMachine.AuthoritySnapshot(
                520, 5201, session, string.Empty);
            Assert(!state.CanAcceptAttached(
                        missing, session, 20, out _),
                "缺失nonce的Attached被接受");
            Assert(ReferenceEquals(state.Pending, pending) && !state.HasAuthority,
                "缺失nonce改变了pending状态");

            var wrongNonce = new SidecarIdentityStateMachine.AuthoritySnapshot(
                520, 5201, session, TestNonce(21));
            Assert(!state.CanAcceptAttached(
                        wrongNonce, session, 20, out _),
                "错误nonce的Attached被接受");
            Assert(ReferenceEquals(state.Pending, pending) && !state.HasAuthority,
                "错误nonce改变了pending状态");

            var staleNonce = new SidecarIdentityStateMachine.AuthoritySnapshot(
                520, 5201, session, TestNonce(22));
            Assert(!state.CanAcceptAttached(
                        staleNonce, session, 20, out _),
                "过期nonce的Attached被接受");
            Assert(ReferenceEquals(state.Pending, pending) && !state.HasAuthority,
                "过期nonce改变了pending状态");

            var wrongPid = new SidecarIdentityStateMachine.AuthoritySnapshot(
                521, 5201, session, nonce);
            Assert(!state.CanAcceptAttached(wrongPid, session, 20, out _),
                "错误PID的Attached被接受");
            var wrongStart = new SidecarIdentityStateMachine.AuthoritySnapshot(
                520, 5202, session, nonce);
            Assert(!state.CanAcceptAttached(wrongStart, session, 20, out _),
                "错误StartTicks的Attached被接受");
            var wrongSession = new SidecarIdentityStateMachine.AuthoritySnapshot(
                520, 5201, "session-other", nonce);
            Assert(!state.CanAcceptAttached(wrongSession, session, 20, out _),
                "错误Session的Attached被接受");
            Assert(ReferenceEquals(state.Pending, pending) && !state.HasAuthority,
                "四元组不匹配改变了pending状态");

            var exact = new SidecarIdentityStateMachine.AuthoritySnapshot(
                520, 5201, session, nonce);
            Assert(state.TryPromoteAttached(
                       exact, session, 20, out var promotionError), promotionError);
            Assert(!state.HasPending && state.HasAuthority &&
                   SidecarIdentityStateMachineIdentityMatches(state.Authority, exact),
                "四元组精确匹配未能唯一promotion");
        }

        private static bool SidecarIdentityStateMachineIdentityMatches(
            SidecarIdentityStateMachine.AuthoritySnapshot actual,
            SidecarIdentityStateMachine.AuthoritySnapshot expected)
        {
            return actual != null && expected != null &&
                   actual.ProcessId == expected.ProcessId &&
                   actual.ProcessStartUtcTicks == expected.ProcessStartUtcTicks &&
                   string.Equals(actual.SessionId, expected.SessionId, StringComparison.Ordinal) &&
                   string.Equals(actual.InstanceNonce, expected.InstanceNonce, StringComparison.Ordinal);
        }

        private static string TestNonce(int value)
        {
            return value.ToString("x32");
        }

        private static int CurrentProcessHandleCount()
        {
            using (var process = Process.GetCurrentProcess())
                return process.HandleCount;
        }

        private static void AssertProcessHandleCountBounded(
            int baseline,
            string message)
        {
            const int toleratedTransientHandles = 16;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (CurrentProcessHandleCount() <= baseline + toleratedTransientHandles)
                    return;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert(CurrentProcessHandleCount() <= baseline + toleratedTransientHandles, message);
        }

        private static void LateAttachedGenerationIsRejected()
        {
            var state = new SidecarIdentityStateMachine();
            Assert(state.TryReservePending(
                       606, 6006, "session-generation", 10,
                       TestNonce(7),
                       out _, out var reserveError), reserveError);
            var attached = new SidecarIdentityStateMachine.AuthoritySnapshot(
                606, 6006, "session-generation", TestNonce(7));
            Assert(!state.CanAcceptAttached(
                        attached, "session-generation", 11, out var staleError) &&
                   staleError.Contains("pending"),
                "旧generation Attached未被拒绝");
            Assert(state.HasPending && !state.HasAuthority,
                "旧generation Attached改变了当前状态");
            Assert(state.TryPromoteAttached(
                       attached, "session-generation", 10, out var currentError), currentError);
        }

        private static void ConcurrentPendingReservationIsSingle()
        {
            var state = new SidecarIdentityStateMachine();
            var accepted = 0;
            Parallel.For(0, 32, index =>
            {
                if (state.TryReservePending(
                        700 + index,
                        7000 + index,
                        "session-concurrent",
                        12,
                        TestNonce(8),
                        out _,
                        out _))
                    System.Threading.Interlocked.Increment(ref accepted);
            });
            Assert(accepted == 1,
                "并发不同helper reservation没有收敛为单一pending");
            Assert(state.HasPending && !state.HasAuthority,
                "并发reservation后的状态不是pending-only");
        }

        private static void AuthorityIdentityCannotBeReplacedByStaleAttached()
        {
            var state = new SidecarIdentityStateMachine();
            var authority = new SidecarIdentityStateMachine.AuthoritySnapshot(
                808, 8008, "session-authority", TestNonce(9));
            Assert(state.TrySeedValidatedAuthority(authority, "session-authority", out var seedError), seedError);

            var staleNonce = new SidecarIdentityStateMachine.AuthoritySnapshot(
                808, 8008, "session-authority", TestNonce(10));
            Assert(!state.CanAcceptAttached(
                        staleNonce, "session-authority", 13, out var nonceError) &&
                   nonceError.Contains("validated authority"),
                "旧nonce Attached覆盖了validated authority");

            var staleStart = new SidecarIdentityStateMachine.AuthoritySnapshot(
                808, 8009, "session-authority", TestNonce(9));
            Assert(!state.CanAcceptAttached(
                        staleStart, "session-authority", 13, out var startError) &&
                   startError.Contains("validated authority"),
                "旧StartTicks Attached覆盖了validated authority");
            Assert(state.Authority == authority,
                "拒绝旧Attached后当前authority快照发生变化");
        }

        private static void Run(string name, Action test, ref int passed)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
                throw;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
