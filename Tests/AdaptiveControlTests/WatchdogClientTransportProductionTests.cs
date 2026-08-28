using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Direct production-engine tests.  The launcher and pipe server are
    /// deliberately only boundary seams; admission, connection ownership,
    /// launch outcome and CAS logic are executed by WatchdogClientTransportEngine.
    /// </summary>
    internal static class WatchdogClientTransportProductionTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("生产Engine单Owner首次PipeUnavailable不产生未观察共享任务异常",
                ColdStartOwnerOnlyObservesReservationFailure, ref passed);
            Run("生产Engine冷启动64并发共享一次launch并传播成功/失败/取消结果",
                ColdStartAdmissionSharesOutcome, ref passed);
            Run("生产Engine已Attached时64次Start不创建新worker或pipe",
                HotStartAlreadyAttachedIsNoOp, ref passed);
            Run("生产Engine发送失败CAS拒绝旧writer并允许新writer复位",
                SendFailureUsesExactConnectionIdentity, ref passed);
            Run("生产Engine ACK拒绝旧lease/connection和倒退序列",
                AckUsesExactConnectionAndMonotonicSequence, ref passed);
            Run("生产Engine Launch closure合作/非合作取消与迟到资源收口",
                LaunchClosureIsBoundedAndReleasable, ref passed);
            Run("生产Engine fail-closed首个FailureKind/detail粘性与新Begin收口",
                StickyTransportFailureIsTerminalUntilNewBegin, ref passed);
            Run("生产Engine Attached完整身份字段门禁拒绝并保持identity不变",
                InvalidAttachedEvidenceDoesNotMutateIdentity, ref passed);
            Run("生产Engine public Attached握手超时进入HandshakeRejected",
                PublicHandshakeTimeoutIsStickyFailClosed, ref passed);
            Run("生产Engine public Host消息触发SessionRevoked",
                PublicHostMessageRevokesSession, ref passed);
            Run("生产Engine public callback/config入口触发InvalidConfiguration",
                PublicInvalidConfigurationIsStickyFailClosed, ref passed);
            Run("生产Engine StateChanged快照不可变/连续序列/观察者重入隔离",
                StateChangedSnapshotsAreImmutableAndReentrant, ref passed);
            Run("生产Engine真实Sidecar 50圈Attach/同权威重连/Shutdown资源回基线",
                RealEngineAttachReconnectShutdownFiftyCycles, ref passed);
            passed += RunPublicRecoveryPolicyOnly();
            passed += RunPublicReconnectLifecycleOnly();
            return passed;
        }

        internal static int RunRealEngineOnly()
        {
            var passed = 0;
            Run("生产Engine真实Sidecar 50圈Attach/同权威重连/Shutdown资源回基线",
                RealEngineAttachReconnectShutdownFiftyCycles, ref passed);
            return passed;
        }

        internal static int RunPublicSendAckOnly()
        {
            var passed = 0;
            Run("生产Engine public Send真实writer/NoWriter/新connection gate",
                SendFailureUsesExactConnectionIdentity, ref passed);
            Run("生产Engine public NamedPipe ACK旧帧/重复/倒退/递增",
                AckUsesExactConnectionAndMonotonicSequence, ref passed);
            return passed;
        }

        /// <summary>
        /// Exercises the real writer admission gate with a large legal
        /// protocol message.  The server-side read barrier is installed before
        /// attach, so a successful attach cannot be mistaken for a blocked
        /// writer.  A busy send must be observable without tearing down the
        /// healthy connection; the write-failure and heartbeat-capture cases
        /// below retain the destructive transport paths.
        /// </summary>
        internal static int RunPublicSendHardeningOnly()
        {
            var passed = 0;
            for (var i = 0; i < 20; i++)
            {
                var iteration = i + 1;
                Run("生产Engine SendBusy不破坏健康连接 #" + iteration,
                    () => SendBusyDoesNotTearHealthyConnection(iteration), ref passed);
            }
            Run("生产Engine写失败按精确connection单次断开/重连",
                WriteFailureBreaksExactConnectionAndReconnects, ref passed);
            Run("生产Engine CaptureHeartbeat一次失败不触发重连",
                HeartbeatCaptureFailureDoesNotReconnect, ref passed);
            return passed;
        }

        internal static int RunPublicFailClosedAttachedOnly()
        {
            var passed = 0;
            Run("生产Engine public v0/v2/v4 ProtocolRejected sticky",
                StickyTransportFailureIsTerminalUntilNewBegin, ref passed);
            Run("生产Engine public Host消息 SessionRevoked",
                PublicHostMessageRevokesSession, ref passed);
            Run("生产Engine public callback/config InvalidConfiguration",
                PublicInvalidConfigurationIsStickyFailClosed, ref passed);
            Run("生产Engine public Attached握手超时 HandshakeRejected",
                PublicHandshakeTimeoutIsStickyFailClosed, ref passed);
            Run("生产Engine public reconnect后迟到old Attached不污染authority",
                PublicDelayedOldAttachedDoesNotPolluteReconnect, ref passed);
            Run("生产Engine public v3 malformed/late Attached IdentityRejected",
                InvalidAttachedEvidenceDoesNotMutateIdentity, ref passed);
            return passed;
        }

        internal static int RunPublicReconnectLifecycleOnly()
        {
            var passed = 0;
            Run("生产Engine public同Session真实断链/单一reconnect/同authority",
                SameSessionTransportFailureUsesSingleReconnect, ref passed);
            Run("生产Engine public重连耗尽attempt上限/一次SafeDegraded",
                ReconnectExhaustionIsSingleSafeDegraded, ref passed);
            Run("生产Engine public ShutdownWithReceipt四阶段ABA/新Begin隔离",
                ShutdownBeginSessionAbaAcrossWorkers, ref passed);
            Run("生产Engine public reconnect迟到old Attached不污染authority",
                PublicDelayedOldAttachedDoesNotPolluteReconnect, ref passed);
            Run("生产Engine public heartbeat/monitor/reconnect cadence",
                HeartbeatMonitorAndReconnectCadence, ref passed);
            return passed;
        }

        internal static int RunPublicRecoveryPolicyOnly()
        {
            var passed = 0;
            Run("生产Engine recovery AttachOnly缺失/非法身份/断管道不启动",
                AttachOnlyRecoveryPolicyFailsClosedWithoutLaunch, ref passed);
            Run("生产Engine recovery精确权威接入与public session CAS门禁",
                AttachOnlyExactAuthorityAndSessionGuards, ref passed);
            Run("生产Engine recovery launch reservation跨Session lease失效不启动",
                AttachOnlyRejectsStaleLaunchReservation, ref passed);
            Run("生产Engine recovery reconnect跨Session lease只收口旧worker",
                AttachOnlyRejectsStaleReconnectWorker, ref passed);
            Run("生产Engine recovery 64 guarded send与真实重连不跨writer",
                GuardedSessionSendCrossesRealReconnect, ref passed);
            return passed;
        }

        private static void AttachOnlyRecoveryPolicyFailsClosedWithoutLaunch()
        {
            // Missing seed is a configuration failure before the first pipe
            // attempt.  The launcher must not even be entered.
            using (var missing = TestHarness.Create(LaunchMode.Success))
            {
                var options = CreateAttachOnlyOptions(missing, null, 1);
                missing.Engine.BeginSession(options, missing.CreateCallbacks(options, "attach-missing"));
                var failure = WaitTaskFailure(missing.Engine.StartAsync(), 5000);
                Assert(failure is WatchdogConnectException &&
                       ((WatchdogConnectException)failure).Kind == WatchdogConnectFailureKind.InvalidConfiguration,
                    "AttachOnly缺失seed没有返回typed InvalidConfiguration：" + failure);
                Assert(missing.Snapshot().TransportFailClosed &&
                       missing.Snapshot().TransportFailureKind == WatchdogConnectFailureKind.InvalidConfiguration &&
                       missing.Launcher.LaunchCount == 0,
                    "AttachOnly缺失seed未闭锁或错误启动Sidecar：" + missing.ServerStats);
            }

            // Structural, nonce, PID/start and session mismatches are all
            // rejected before launch.  Each case uses a fresh production
            // Engine and its public BeginSession/Start path.
            var cases = new[] { "nonce", "pid-start", "session" };
            foreach (var kind in cases)
            {
                using (var invalid = TestHarness.Create(LaunchMode.Success))
                {
                    using (var process = Process.GetCurrentProcess())
                    {
                        var startTicks = process.StartTime.ToUniversalTime().Ticks;
                        var session = invalid.Options.SessionId;
                        var seed = kind == "nonce"
                            ? new SidecarIdentityStateMachine.AuthoritySnapshot(
                                process.Id, startTicks, session, "not-a-challenge")
                            : kind == "pid-start"
                                ? new SidecarIdentityStateMachine.AuthoritySnapshot(
                                    process.Id, checked(startTicks + 1), session,
                                    "0123456789abcdef0123456789abcdef")
                                : new SidecarIdentityStateMachine.AuthoritySnapshot(
                                    process.Id, startTicks, session + "-wrong",
                                    "0123456789abcdef0123456789abcdef");
                        var options = CreateAttachOnlyOptionsFromAuthority(invalid, seed, 1);
                        invalid.Engine.BeginSession(options, invalid.CreateCallbacks(options, "attach-" + kind));
                        var failure = WaitTaskFailure(invalid.Engine.StartAsync(), 5000);
                        Assert(failure is WatchdogConnectException &&
                               ((WatchdogConnectException)failure).Kind == WatchdogConnectFailureKind.InvalidConfiguration,
                            "AttachOnly非法" + kind + "没有返回typed InvalidConfiguration：" + failure);
                        Assert(invalid.Launcher.LaunchCount == 0 &&
                               invalid.Snapshot().TransportFailClosed,
                            "AttachOnly非法" + kind + "发生Sidecar launch或未闭锁：" + invalid.ServerStats);
                    }
                }
            }

            // With a valid authority seed but no pipe, recovery remains
            // attached-only: a live authority is supervised by one
            // reconnect worker, never by a new launch.  Killing that exact
            // authority later must converge to the sticky IdentityRejected
            // terminal boundary.
            using (var unavailable = TestHarness.Create(LaunchMode.Success))
            {
                unavailable.Engine.BeginSession(unavailable.Options, unavailable.Callbacks);
                Assert(unavailable.Engine.StartAsync().Wait(15000), "AttachOnly pipe测试前置Attach失败");
                var source = unavailable.Snapshot();
                var launchCount = unavailable.Launcher.LaunchCount;
                unavailable.Engine.Shutdown();
                unavailable.Factory.Disable();
                var options = CreateAttachOnlyOptions(unavailable, source, checked(source.SessionGeneration + 1));
                unavailable.Engine.BeginSession(options, unavailable.CreateCallbacks(options, "attach-pipe"));
                Assert(unavailable.Engine.StartAsync().Wait(10000),
                    "AttachOnly live-authority断管道没有进入唯一重连监督。");
                Parallel.For(0, 64, _ => unavailable.Engine.ScheduleReconnect(0));
                Assert(WaitUntil(
                           () => unavailable.Snapshot().ReconnectActive ||
                                 unavailable.Snapshot().ReconnectAttempt > 0,
                           5000),
                    "AttachOnly live-authority断管道没有启动唯一reconnect worker。");
                var after = unavailable.Snapshot();
                Assert(!after.TransportFailClosed &&
                       unavailable.Launcher.LaunchCount == launchCount,
                    "AttachOnly live-authority断管道错误启动Sidecar或提前闭锁：" + unavailable.ServerStats);

                using (var authority = Process.GetProcessById(source.AuthorityProcessId))
                {
                    if (!authority.HasExited) authority.Kill();
                    Assert(authority.WaitForExit(5000),
                        "AttachOnly live-authority测试未能结束精确authority。");
                }
                Assert(WaitUntil(
                           () => unavailable.Snapshot().TransportFailClosed &&
                                 unavailable.Snapshot().TransportFailureKind ==
                                     WatchdogConnectFailureKind.IdentityRejected,
                           15000),
                    "AttachOnly authority死亡没有进入sticky IdentityRejected：" + unavailable.ServerStats);
                after = unavailable.Snapshot();
                Assert(!after.ReconnectActive &&
                       unavailable.Launcher.LaunchCount == launchCount,
                    "AttachOnly authority死亡后仍启动Sidecar/reconnect：" + unavailable.ServerStats);
            }
        }

        private static void AttachOnlyExactAuthorityAndSessionGuards()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000), "AttachOnly精确权威测试前置连接失败");
                var old = harness.Snapshot();
                var launchCount = harness.Launcher.LaunchCount;
                Assert(old.IsAttached && old.HasAuthority && old.AuthorityInstanceNonce.Length == 32,
                    "前置连接没有产生可复用的权威身份");

                // The old authority process remains alive across transport
                // session shutdown; the next recovery session must attach to
                // this exact PID/start/nonce without launching another helper.
                harness.Engine.Shutdown();
                var options = CreateAttachOnlyOptions(
                    harness,
                    old,
                    checked(old.SessionGeneration + 1));
                harness.Engine.BeginSession(options, harness.CreateCallbacks(options, "attach-exact"));
                Assert(harness.Engine.StartAsync().Wait(15000), "AttachOnly精确权威接入失败");
                var current = harness.Snapshot();
                Assert(harness.Launcher.LaunchCount == launchCount &&
                       current.IsAttached && current.HasAuthority &&
                       current.AuthorityProcessId == old.AuthorityProcessId &&
                       current.AuthorityProcessStartUtcTicks == old.AuthorityProcessStartUtcTicks &&
                       current.AuthoritySessionId == old.AuthoritySessionId &&
                       current.AuthorityInstanceNonce == old.AuthorityInstanceNonce &&
                       !current.TransportFailClosed,
                    "AttachOnly精确权威没有复用原有authority：" + harness.ServerStats);

                // Exercise the public CAS guards from 64 stale callbacks.
                // They must neither write to the new writer nor close the
                // current session.
                var staleSendResults = new bool[64];
                var staleCloseResults = new bool[64];
                Parallel.For(0, 64, index =>
                {
                    staleSendResults[index] = harness.Engine.TrySendForSession(
                        new WatchdogMessage
                        {
                            ProtocolVersion = WatchdogProtocol.Version,
                            Type = WatchdogMessageType.Ping,
                            SessionId = old.SessionId,
                            Reason = "stale-" + index
                        },
                        old.SessionId,
                        old.SessionGeneration,
                        old.ActiveSessionLease);
                    staleCloseResults[index] = harness.Engine.TryMarkSessionClosing(
                        old.SessionId,
                        old.SessionGeneration,
                        old.ActiveSessionLease);
                });
                Assert(staleSendResults.All(value => !value) &&
                       staleCloseResults.All(value => !value),
                    "旧Session callback越过了session generation CAS门禁");
                var afterStale = harness.Snapshot();
                Assert(!afterStale.IsClosing && afterStale.SessionGeneration == current.SessionGeneration,
                    "旧Session callback错误关闭或污染了新Session：" + harness.ServerStats);

                var wrongPayload = new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    SessionId = current.SessionId + "-wrong",
                    Reason = "wrong-payload-session"
                };
                Assert(!harness.Engine.TrySendForSession(
                           wrongPayload,
                           current.SessionId,
                           current.SessionGeneration,
                           current.ActiveSessionLease),
                    "payload SessionId不匹配时仍写入了当前writer");
                Assert(wrongPayload.SessionId == current.SessionId + "-wrong",
                    "错误payload SessionId被Engine意外改写");

                var emptyPayload = new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    Reason = "empty-payload-session"
                };
                Assert(harness.Engine.TrySendForSession(
                           emptyPayload,
                           current.SessionId,
                           current.SessionGeneration,
                           current.ActiveSessionLease),
                    "空payload SessionId没有由Engine副本补齐后发送");
                Assert(string.IsNullOrEmpty(emptyPayload.SessionId),
                    "空payload原对象被Engine写入SessionId，未保持调用者对象不变");

                var publicWrongPayload = new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    SessionId = current.SessionId + "-public-wrong",
                    Reason = "public-wrong-payload-session"
                };
                Assert(!harness.Engine.Send(publicWrongPayload),
                    "public Send没有拒绝错误SessionId payload");
                Assert(publicWrongPayload.SessionId == current.SessionId + "-public-wrong",
                    "public Send改写了错误SessionId payload原对象");
                var publicEmptyPayload = new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    Reason = "public-empty-payload-session"
                };
                Assert(harness.Engine.Send(publicEmptyPayload),
                    "public Send没有为SessionId为空的payload创建冻结副本");
                Assert(string.IsNullOrEmpty(publicEmptyPayload.SessionId),
                    "public Send改写了SessionId为空的payload原对象");

                Assert(harness.Engine.TrySendForSession(
                           new WatchdogMessage
                           {
                               ProtocolVersion = WatchdogProtocol.Version,
                               Type = WatchdogMessageType.Ping,
                               SessionId = current.SessionId,
                               Reason = "current-session"
                           },
                           current.SessionId,
                           current.SessionGeneration,
                           current.ActiveSessionLease),
                    "当前Session TrySendForSession没有复用精确connection");

                Assert(harness.Engine.TryMarkSessionClosing(
                           current.SessionId,
                           current.SessionGeneration,
                           current.ActiveSessionLease),
                    "当前Session TryMarkSessionClosing没有成功");
                harness.Engine.Shutdown();
                var reconnectOptions = CreateAttachOnlyOptions(
                    harness,
                    current,
                    checked(current.SessionGeneration + 1));
                harness.Engine.BeginSession(
                    reconnectOptions,
                    harness.CreateCallbacks(reconnectOptions, "attach-reconnect"));
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "AttachOnly重连门禁测试无法建立精确existing authority");
                // An attach-only session may reconnect to the same validated
                // authority after a real pipe break.  It must use only the
                // existing pipe/authority and may never launch a helper.
                var beforeReconnect = harness.Snapshot();
                var reconnectBoundary = harness.MarkStateBoundary();
                harness.ArmForNextServer();
                harness.CloseCurrentConnection();
                Assert(WaitUntil(
                           () => harness.Snapshot().ReconnectGeneration >
                                 beforeReconnect.ReconnectGeneration ||
                                 harness.Snapshot().ReconnectActive,
                           5000),
                    "AttachOnly真实断链没有启动唯一reconnect worker");
                Assert(WaitUntil(
                           () => harness.Snapshot().IsAttached &&
                                 !harness.Snapshot().ReconnectActive,
                           15000),
                    "AttachOnly真实断链后没有恢复同一authority");
                var afterReconnect = harness.Snapshot();
                Assert(!afterReconnect.TransportFailClosed &&
                       afterReconnect.HasAuthority &&
                       afterReconnect.AuthorityProcessId == beforeReconnect.AuthorityProcessId &&
                       afterReconnect.AuthorityProcessStartUtcTicks ==
                           beforeReconnect.AuthorityProcessStartUtcTicks &&
                       afterReconnect.AuthoritySessionId == beforeReconnect.AuthoritySessionId &&
                       afterReconnect.AuthorityInstanceNonce == beforeReconnect.AuthorityInstanceNonce &&
                       harness.Launcher.LaunchCount == launchCount &&
                       harness.CountStateEventsForLeaseAfter(
                           beforeReconnect.ActiveSessionLease,
                           reconnectBoundary,
                           "ReconnectStarted") == 1 &&
                       harness.CountStateEventsForLeaseAfter(
                           beforeReconnect.ActiveSessionLease,
                           reconnectBoundary,
                           "Reconnected") == 1,
                    "AttachOnly断链重连没有保持authority四元组/唯一worker：" +
                    harness.ServerStats);

                // A confirmed authority death is a typed terminal condition;
                // it must not kill/relaunch a process that the recovery
                // session does not own.
                var authorityPid = afterReconnect.AuthorityProcessId;
                using (var authority = Process.GetProcessById(authorityPid))
                {
                    if (!authority.HasExited) authority.Kill();
                    Assert(authority.WaitForExit(5000), "AttachOnly authority未在有界时间内退出");
                }
                harness.CloseCurrentConnection();
                Assert(WaitUntil(
                           () => harness.Snapshot().TransportFailClosed &&
                                 harness.Snapshot().TransportFailureKind ==
                                     WatchdogConnectFailureKind.IdentityRejected,
                           15000),
                    "AttachOnly authority死亡后没有进入typed IdentityRejected终态");
                var authorityDead = harness.Snapshot();
                Assert(authorityDead.TransportFailClosed &&
                       authorityDead.TransportFailureKind == WatchdogConnectFailureKind.IdentityRejected &&
                       harness.Launcher.LaunchCount == launchCount &&
                       !authorityDead.ReconnectActive,
                    "AttachOnly authority死亡后错误启动Sidecar/reconnect：" +
                    harness.ServerStats);
                harness.Engine.Shutdown();
            }
        }

        private static void AttachOnlyRejectsStaleLaunchReservation()
        {
            var baseline = SidecarProcessHandleOwner.LiveOwnedCount;
            using (var harness = TestHarness.Create(LaunchMode.NonCooperativeCancel))
            {
                // A is deliberately held after the production launch
                // reservation has been admitted.  B is then begun through
                // the public lifecycle boundary while A is still in-flight.
                // Releasing A must settle the captured old lease, never
                // promote its result into B, and never create a second
                // helper for B's AttachOnly policy.
                harness.Engine.BeginSession(
                    harness.Options,
                    harness.CreateCallbacks(harness.Options, "attach-launch-a"));
                var startA = harness.Engine.StartAsync();
                Assert(harness.Launcher.Entered.Task.Wait(5000),
                    "AttachOnly launch lease测试没有进入真实launch reservation");
                var oldLease = harness.Snapshot().ActiveSessionLease;
                Assert(oldLease > 0 && harness.Launcher.LaunchCount == 1,
                    "AttachOnly launch lease测试前置launch状态不完整");

                var attachOnlyB = CreateAttachOnlyOptions(
                    harness,
                    null,
                    checked(harness.Options.SessionGeneration + 1));
                var beginB = Task.Run(() => harness.Engine.BeginSession(
                    attachOnlyB,
                    harness.CreateCallbacks(attachOnlyB, "attach-launch-b")));
                Assert(WaitUntil(
                           () => harness.Engine.IsClosing &&
                                 !harness.Snapshot().SessionActive,
                           3000),
                    "AttachOnly launch lease测试没有进入A关闭/B接管边界");

                // This is the public release point for A's non-cooperative
                // launch.  The engine must observe the lease change at its
                // final reservation gate and clean the exact owned result.
                harness.Launcher.ReleaseNonCooperative();
                Assert(WaitTaskCompleted(startA, 10000),
                    "旧Session launch task在释放真实launcher后未结束");
                Assert(WaitTaskCompleted(beginB, 10000),
                    "AttachOnly B BeginSession未在有界时间内完成");

                var after = harness.Snapshot();
                Assert(after.SessionActive &&
                       after.TransportFailClosed &&
                       after.TransportFailureKind == WatchdogConnectFailureKind.InvalidConfiguration &&
                       !after.LaunchActive && !after.HasPending &&
                       !after.HasAuthority &&
                       harness.Launcher.LaunchCount == 1,
                    "A旧lease跨越launch边界或B错误启动Sidecar：" + harness.ServerStats);
                Assert(WaitUntil(
                           () => SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                           5000),
                    "A旧lease launch结果未在B接管后回收");
                harness.Engine.Shutdown();
            }
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                "AttachOnly launch reservation测试泄漏了Sidecar句柄");
        }

        private static void AttachOnlyRejectsStaleReconnectWorker()
        {
            var baseline = SidecarProcessHandleOwner.LiveOwnedCount;
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "AttachOnly reconnect lease测试前置Attach失败");
                var old = harness.Snapshot();
                var oldLease = old.ActiveSessionLease;
                var launchCount = harness.Launcher.LaunchCount;
                var createBaseline = harness.Factory.CreateCount;

                // Hold the real public Connect boundary after reconnect has
                // reserved its worker.  B will close A and then attach to
                // the same validated authority, without launching.
                harness.Factory.BlockCreate();
                harness.ArmForNextServer();
                harness.CloseCurrentConnection();
                harness.Engine.ScheduleReconnect(0);
                Assert(WaitUntil(
                           () => harness.Snapshot().ReconnectActive &&
                                 harness.Factory.CreateCount > createBaseline,
                           5000),
                    "AttachOnly reconnect lease测试没有进入真实Connect边界");

                var attachOnlyB = CreateAttachOnlyOptions(
                    harness,
                    old,
                    checked(old.SessionGeneration + 1));
                var beginB = Task.Run(() => harness.Engine.BeginSession(
                    attachOnlyB,
                    harness.CreateCallbacks(attachOnlyB, "attach-reconnect-b")));
                Assert(WaitUntil(
                           () => beginB.IsCompleted ||
                                 harness.Engine.IsClosing ||
                                 !harness.Snapshot().SessionActive,
                           3000),
                    "AttachOnly reconnect lease测试没有进入A关闭/B接管边界");
                harness.Factory.RejectNextCreate();
                harness.Factory.ReleaseCreate();
                Assert(WaitTaskCompleted(beginB, 10000),
                    "AttachOnly B BeginSession未在旧reconnect收口后完成");
                Assert(!beginB.IsFaulted,
                    "AttachOnly B BeginSession在旧reconnect收口时失败：" +
                    (beginB.Exception?.GetBaseException().Message ?? "unknown"));

                var stateBoundary = harness.MarkStateBoundary();
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "AttachOnly B无法复用精确existing authority");
                Assert(WaitUntil(
                           () => harness.Snapshot().IsAttached &&
                                 !harness.Snapshot().ReconnectActive,
                           5000),
                    "AttachOnly B启动后仍有旧reconnect worker活动");
                var after = harness.Snapshot();
                Assert(after.IsAttached && after.HasAuthority &&
                       after.AuthorityProcessId == old.AuthorityProcessId &&
                       after.AuthorityProcessStartUtcTicks == old.AuthorityProcessStartUtcTicks &&
                       after.AuthoritySessionId == old.AuthoritySessionId &&
                       after.AuthorityInstanceNonce == old.AuthorityInstanceNonce &&
                       !after.TransportFailClosed &&
                       harness.Launcher.LaunchCount == launchCount &&
                       harness.CountStateEventsForLeaseAfter(oldLease, stateBoundary) == 0,
                    "旧reconnect worker污染了AttachOnly B或触发第二次launch：" +
                    harness.ServerStats);
                Assert(harness.Engine.ShutdownWithReceipt().AllResourcesReleased,
                    "AttachOnly reconnect lease测试资源未收口");
            }
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                "AttachOnly reconnect lease测试泄漏了Sidecar句柄");
        }

        private static void GuardedSessionSendCrossesRealReconnect()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "guarded send/reconnect测试前置Attach失败");
                // StartAsync's Attached barrier is published by the client
                // reader.  Wait for the server-side reader to observe the
                // production heartbeat as well, so the first guarded sends
                // exercise an established bidirectional writer rather than
                // racing the server's post-Attached writer publication.
                Assert(WaitUntil(
                           () => harness.Snapshot().IsAttached &&
                                 harness.HeartbeatCount > 0,
                           5000),
                    "guarded send/reconnect测试前置心跳未穿过真实NamedPipe writer：" +
                    harness.ServerStats);
                var before = harness.Snapshot();
                var launchCount = harness.Launcher.LaunchCount;

                // First prove that all 64 lease-bound calls can complete on
                // the real attached writer without mutating the caller's
                // payload objects.
                var firstSends = new bool[64];
                Parallel.For(0, firstSends.Length, index =>
                {
                    firstSends[index] = harness.Engine.TrySendForSession(
                        new WatchdogMessage
                        {
                            ProtocolVersion = WatchdogProtocol.Version,
                            Type = WatchdogMessageType.Ping,
                            Reason = "guarded-before-reconnect-" + index
                        },
                        before.SessionId,
                        before.SessionGeneration,
                        before.ActiveSessionLease);
                });
                Assert(firstSends.Any(value => value),
                    "64次guarded send没有任何一次到达真实writer; before=" +
                    "session=" + before.SessionId + "/gen=" + before.SessionGeneration +
                    "/lease=" + before.ActiveSessionLease + "/conn=" +
                    before.ActiveConnectionGeneration + "/attached=" + before.IsAttached +
                    ";after=" + harness.Snapshot().ActiveConnectionGeneration +
                    "/attached=" + harness.Snapshot().IsAttached + ";stats=" +
                    harness.ServerStats);

                // Start the real reconnect boundary while another 64 calls
                // still use the frozen session identity.  The engine must
                // either complete on the captured old connection or reject
                // at its connection CAS; it must never write through a stale
                // writer after the new connection has been installed.
                harness.ArmForNextServer();
                harness.CloseCurrentConnection();
                var crossingSends = Enumerable.Range(0, 64)
                    .Select(index => Task.Run(() => harness.Engine.TrySendForSession(
                        new WatchdogMessage
                        {
                            ProtocolVersion = WatchdogProtocol.Version,
                            Type = WatchdogMessageType.Ping,
                            Reason = "guarded-cross-reconnect-" + index
                        },
                        before.SessionId,
                        before.SessionGeneration,
                        before.ActiveSessionLease)))
                    .ToArray();
                harness.Engine.ScheduleReconnect(0);
                Assert(WaitAll(crossingSends, 5000),
                    "跨真实reconnect的64次guarded send没有在有界时间内结束");
                Assert(WaitUntil(
                           () => harness.Snapshot().IsAttached &&
                                 !harness.Snapshot().ReconnectActive,
                           15000),
                    "64次guarded send交叉期间真实reconnect没有完成");
                var after = harness.Snapshot();
                Assert(!after.TransportFailClosed && after.HasAuthority &&
                       after.AuthorityProcessId == before.AuthorityProcessId &&
                       after.AuthorityProcessStartUtcTicks == before.AuthorityProcessStartUtcTicks &&
                       after.AuthorityInstanceNonce == before.AuthorityInstanceNonce &&
                       harness.Launcher.LaunchCount == launchCount,
                    "guarded send跨reconnect污染新writer/authority或触发第二次launch：" +
                    harness.ServerStats);
                harness.Engine.Shutdown();
            }
        }

        private static void ColdStartAdmissionSharesOutcome()
        {
            using (var success = TestHarness.Create(LaunchMode.Success))
            {
                success.Engine.BeginSession(success.Options, success.Callbacks);
                var callers = StartCallers(success.Engine, 64);
                Assert(WaitAll(callers, 20000), "冷启动成功调用未在有界时间内结束");
                Assert(callers.All(task => task.Status == TaskStatus.RanToCompletion),
                    "冷启动成功调用没有全部返回成功");
                Assert(success.Launcher.LaunchCount == 1,
                    "冷启动成功创建了多个Sidecar launch reservation：" + success.Launcher.LaunchCount);
                Assert(success.Snapshot().IsAttached,
                    "冷启动成功后生产Engine没有进入Attached");
                success.Engine.Shutdown();
            }

            using (var failed = TestHarness.Create(LaunchMode.Failure))
            {
                failed.Engine.BeginSession(failed.Options, failed.Callbacks);
                var callers = StartCallers(failed.Engine, 64);
                Assert(failed.Launcher.Entered.Task.Wait(5000),
                    "leader failure场景没有进入生产launch");
                failed.Launcher.ReleaseFailure();
                Assert(WaitAll(callers, 15000), "leader failure调用未在有界时间内结束");
                Assert(failed.Launcher.LaunchCount == 1,
                    "leader failure产生了多个launch：" + failed.Launcher.LaunchCount);
                var failures = callers.Select(GetFailure).ToArray();
                Assert(failures.All(error => error is WatchdogConnectException),
                    "leader failure存在非生产WatchdogConnectException结果");
                Assert(failures.Select(error => ((WatchdogConnectException)error).Kind)
                           .Distinct().Count() == 1 &&
                       ((WatchdogConnectException)failures[0]).Kind == WatchdogConnectFailureKind.LaunchFailure,
                    "leader failure没有向所有caller传播同一LaunchFailure outcome");
                failed.Engine.Shutdown();
            }

            using (var cancelled = TestHarness.Create(LaunchMode.Cancel))
            {
                cancelled.Engine.BeginSession(cancelled.Options, cancelled.Callbacks);
                var callers = StartCallers(cancelled.Engine, 64);
                Assert(cancelled.Launcher.Entered.Task.Wait(5000),
                    "cancel场景没有进入生产launch");
                // Let all callers leave the shared Connect reservation and
                // join the same launch transaction before cancellation.
                Thread.Sleep(150);
                cancelled.Engine.Shutdown();
                Assert(WaitAll(callers, 15000), "cancel调用未在有界时间内结束");
                Assert(cancelled.Launcher.LaunchCount == 1,
                    "cancel场景产生了多个launch：" + cancelled.Launcher.LaunchCount);
                var failures = callers.Select(GetFailure).ToArray();
                Assert(failures.All(error => error is WatchdogConnectException),
                    "cancel场景存在非WatchdogConnectException结果");
                Assert(failures.Select(error => ((WatchdogConnectException)error).Kind)
                           .Distinct().Count() == 1,
                    "cancel场景caller没有共享同一取消结果");
            }
        }

        private static void ColdStartOwnerOnlyObservesReservationFailure()
        {
            var unobservedPipeUnavailable = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
            {
                var flattened = args.Exception.Flatten();
                if (flattened.InnerExceptions.Any(error =>
                        error is WatchdogConnectException connect &&
                        connect.Kind == WatchdogConnectFailureKind.PipeUnavailable))
                    Interlocked.Increment(ref unobservedPipeUnavailable);
                args.SetObserved();
            };

            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                    RunOwnerOnlyColdStartOnce();

                // The reservation fault has no joiner by construction. Force
                // collection so the test proves the publisher observed its own
                // TCS failure instead of relying on a later concurrent caller.
                for (var i = 0; i < 4; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Thread.Sleep(50);
                }

                Assert(Volatile.Read(ref unobservedPipeUnavailable) == 0,
                    "单Owner冷启动仍产生未观察PipeUnavailable共享任务异常：" +
                    unobservedPipeUnavailable);
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= handler;
            }
        }

        private static void RunOwnerOnlyColdStartOnce()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                var start = harness.Engine.StartAsync();
                Assert(start.Wait(15000), "单Owner冷启动未在有界时间内成功");
                Assert(harness.Launcher.LaunchCount == 1 && harness.Snapshot().IsAttached,
                    "单Owner冷启动没有完成一次launch/Attached");
                harness.Engine.Shutdown();
            }
        }

        private static void HotStartAlreadyAttachedIsNoOp()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "hot-start测试首次Start失败");
                var launches = harness.Launcher.LaunchCount;
                var creates = harness.Factory.CreateCount;
                var workers = harness.Snapshot().ReconnectGeneration;
                var callers = StartCallers(harness.Engine, 64);
                Assert(WaitAll(callers, 15000), "Attached重复Start未在有界时间内结束");
                Assert(callers.All(task => task.Status == TaskStatus.RanToCompletion),
                    "Attached重复Start产生失败caller");
                var after = harness.Snapshot();
                Assert(harness.Launcher.LaunchCount == launches &&
                       harness.Factory.CreateCount == creates &&
                       after.ReconnectGeneration == workers &&
                       after.IsAttached,
                    "Attached重复Start创建了新的launch/pipe/worker或丢失Attached");
                harness.Engine.Shutdown();
            }
        }

        private static void LaunchClosureIsBoundedAndReleasable()
        {
            var baseline = SidecarProcessHandleOwner.LiveOwnedCount;
            using (var cooperative = TestHarness.Create(LaunchMode.Cancel))
            {
                cooperative.Engine.BeginSession(cooperative.Options, cooperative.Callbacks);
                var start = cooperative.Engine.StartAsync();
                Assert(cooperative.Launcher.Entered.Task.Wait(5000),
                    "合作取消场景没有进入生产launch");
                cooperative.Engine.Shutdown();
                Assert(WaitFaulted(start, 10000),
                    "合作取消launch调用没有在有界时间内结束");
                var snapshot = cooperative.Snapshot();
                Assert(!snapshot.LaunchActive && !snapshot.LaunchClosureBlocked,
                    "合作取消后Launch closure没有即时收口");
                Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                    "合作取消后Sidecar句柄未回到基线");
                cooperative.Engine.Shutdown();
            }

            using (var nonCooperative = TestHarness.Create(LaunchMode.NonCooperativeCancel))
            {
                nonCooperative.Engine.BeginSession(nonCooperative.Options, nonCooperative.Callbacks);
                var start = nonCooperative.Engine.StartAsync();
                Assert(nonCooperative.Launcher.Entered.Task.Wait(5000),
                    "非合作取消场景没有进入生产launch");

                nonCooperative.Engine.Shutdown();
                // A second shutdown must operate on the retained launch
                // transaction, never start a second close/launch worker.
                nonCooperative.Engine.Shutdown();
                var blocked = nonCooperative.Snapshot();
                Assert(blocked.LaunchActive && blocked.LaunchClosureBlocked,
                    "非合作launcher超过shutdown收口期限后没有sticky LaunchClosureBlocked");
                Assert(blocked.LaunchClosureDetail.IndexOf("Shutdown", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LaunchClosureBlocked没有保留真实Shutdown原因");
                AssertThrows<WatchdogConnectException>(
                    () => nonCooperative.Engine.StartAsync().Wait(5000),
                    "LaunchClosureBlocked期间二次Start未被拒绝");

                nonCooperative.Launcher.ReleaseNonCooperative();
                Assert(nonCooperative.Launcher.Returned.Task.Wait(5000),
                    "迟到launcher没有返回真实Process/owner结果");
                Assert(WaitFaulted(start, 10000),
                    "非合作取消的原始Start没有传播取消结果");
                Assert(WaitUntil(
                           () => !nonCooperative.Snapshot().LaunchActive &&
                                 SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                           5000),
                    "迟到launch完成后closure/owner没有最终收口");
                var released = nonCooperative.Snapshot();
                Assert(released.LaunchClosureBlocked && !released.LaunchActive,
                    "迟到launch收口后Launch closure历史/活动状态不一致");
                Assert(!IsProcessAlive(nonCooperative.Launcher.LastProcessId),
                    "迟到返回的真实Process未被kill并WaitForExit");

                // Historical LaunchClosureBlocked is retained for evidence,
                // but it must not block a fresh session once LaunchActive is
                // released.
                nonCooperative.Engine.BeginSession(
                    nonCooperative.Options,
                    nonCooperative.Callbacks);
                Assert(nonCooperative.Snapshot().SessionActive,
                    "Launch closure释放后无法Begin新session");
                nonCooperative.Engine.Shutdown();
                nonCooperative.Engine.Shutdown();
                Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                    "重复Shutdown后Sidecar句柄未回到基线");
            }
        }

        private static void StickyTransportFailureIsTerminalUntilNewBegin()
        {
            var protocolMutations = new[]
            {
                AttachedMutation.ProtocolV0,
                AttachedMutation.ProtocolV2,
                AttachedMutation.ProtocolV4
            };
            foreach (var mutation in protocolMutations)
            {
                using (var harness = TestHarness.Create(LaunchMode.Success, mutation))
                {
                    harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                    var start = harness.Engine.StartAsync();
                    Assert(WaitFaulted(start, 15000),
                        "public v" + GetProtocolVersion(mutation) + "没有在有界时间内失败");
                    AssertPublicStickyFailure(
                        harness,
                        start,
                        WatchdogConnectFailureKind.ProtocolRejected,
                        "public protocol v" + GetProtocolVersion(mutation),
                        expectPending: true,
                        expectAuthority: false);
                }
            }

            // Sticky state may only be cleared by the public shutdown
            // boundary; a direct second Begin without Shutdown is not used
            // as a reset path.
            using (var reset = TestHarness.Create(
                       LaunchMode.Success,
                       AttachedMutation.ProtocolV2))
            {
                reset.Engine.BeginSession(reset.Options, reset.Callbacks);
                var failed = reset.Engine.StartAsync();
                Assert(WaitFaulted(failed, 15000),
                    "Shutdown边界前置ProtocolRejected没有失败");
                Assert(reset.Snapshot().TransportFailClosed,
                    "Shutdown边界前置没有形成sticky状态");
                reset.Engine.Shutdown();
                reset.SetAttachedMutation(AttachedMutation.Valid);
                reset.Engine.BeginSession(reset.Options, reset.Callbacks);
                Assert(reset.Snapshot().SessionActive &&
                       !reset.Snapshot().TransportFailClosed,
                    "只有public Shutdown后新Begin才应清理sticky状态");
                reset.Engine.Shutdown();
            }
        }

        private static void InvalidAttachedEvidenceDoesNotMutateIdentity()
        {
            var mutations = new[]
            {
                AttachedMutation.MissingSessionId,
                AttachedMutation.MissingAuthoritySessionId,
                AttachedMutation.MissingSidecarSessionId,
                AttachedMutation.WrongSessionId,
                AttachedMutation.WrongAuthoritySessionId,
                AttachedMutation.WrongSidecarSessionId,
                AttachedMutation.MissingPid,
                AttachedMutation.WrongPid,
                AttachedMutation.MissingProcessStart,
                AttachedMutation.MissingSidecarStart,
                AttachedMutation.MissingStart,
                AttachedMutation.WrongStart,
                AttachedMutation.MissingSidecarNonce,
                AttachedMutation.MissingNonce,
                AttachedMutation.WrongNonce
            };
            foreach (var mutation in mutations)
            {
                using (var harness = TestHarness.Create(
                           LaunchMode.Success,
                           mutation))
                {
                    harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                    var start = harness.Engine.StartAsync();
                    Assert(WaitFaulted(start, 15000),
                        "非法Attached没有在有界时间内失败：" + mutation);
                    AssertPublicStickyFailure(
                        harness,
                        start,
                        WatchdogConnectFailureKind.IdentityRejected,
                        "public malformed Attached " + mutation,
                        expectPending: true,
                        expectAuthority: false);
                }
            }

            // A valid v3 response remains the only promotion path, and stale
            // identity evidence received through a later public reconnect is
            // rejected by the production Reader before authority mutation.
            RunPublicStaleAttachedReconnectCase(
                AttachedMutation.WrongNonce,
                "old connection identity");
            RunPublicStaleAttachedReconnectCase(
                AttachedMutation.WrongStart,
                "late generation identity");
        }

        private static void PublicHandshakeTimeoutIsStickyFailClosed()
        {
            using (var harness = TestHarness.Create(
                       LaunchMode.Success,
                       AttachedMutation.NoAttachedResponse))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                var start = harness.Engine.StartAsync();
                Assert(WaitFaulted(start, 15000),
                    "Attached不响应没有在有界时间内失败");
                AssertPublicStickyFailure(
                    harness,
                    start,
                    WatchdogConnectFailureKind.HandshakeRejected,
                    "public Attached timeout",
                    expectPending: true,
                    expectAuthority: false);
            }
        }

        private static void PublicHostMessageRevokesSession()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "SessionRevoked测试前置Attach失败");
                var before = harness.Snapshot();
                var processId = harness.Launcher.LastProcessId;
                var launchCount = harness.Launcher.LaunchCount;
                var reconnectGeneration = before.ReconnectGeneration;
                Assert(harness.SendSessionRevokedMessage(),
                    "受控Host没有通过真实NamedPipe发送SessionRevoked消息");
                Assert(WaitUntil(
                           () =>
                           {
                               var snapshot = harness.Snapshot();
                               return snapshot.TransportFailClosed &&
                                      snapshot.TransportFailureKind == WatchdogConnectFailureKind.SessionRevoked;
                           },
                           5000),
                    "真实Reader没有把Host消息收敛为SessionRevoked");
                var revoked = harness.Snapshot();
                Assert(revoked.HasAuthority && IsProcessAlive(processId),
                    "SessionRevoked错误清除了validated authority或kill了权威进程");
                Assert(harness.Launcher.LaunchCount == launchCount &&
                       revoked.ReconnectGeneration == reconnectGeneration,
                    "SessionRevoked触发了新launch/reconnect");
                var detail = revoked.TransportFailureDetail;
                Assert(!string.IsNullOrWhiteSpace(detail),
                    "SessionRevoked没有保留首个结构化detail");
                AssertThrows<WatchdogConnectException>(
                    () => harness.Engine.StartAsync().Wait(5000),
                    "SessionRevoked sticky期间仍允许Start");
                var sticky = harness.Snapshot();
                Assert(sticky.TransportFailureKind == WatchdogConnectFailureKind.SessionRevoked &&
                       sticky.TransportFailureDetail == detail,
                    "SessionRevoked后续状态覆盖了首个failure detail");
                harness.Engine.Shutdown();
            }
        }

        private static void PublicInvalidConfigurationIsStickyFailClosed()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                AssertThrows<ArgumentNullException>(
                    () => harness.Engine.BeginSession(harness.Options, null),
                    "public BeginSession(null callbacks)没有在配置入口拒绝");

                var callbacks = harness.CreateCallbacks(harness.Options, "invalid-config");
                callbacks.CreateRunSession = (recovery, attempt) => null;
                harness.Engine.BeginSession(harness.Options, callbacks);
                var start = harness.Engine.StartAsync();
                Assert(WaitFaulted(start, 15000),
                    "CreateRunSession返回null没有在有界时间内失败");
                AssertPublicStickyFailure(
                    harness,
                    start,
                    WatchdogConnectFailureKind.InvalidConfiguration,
                    "public CreateRunSession null",
                    expectPending: true,
                    expectAuthority: false);
            }
        }

        private static void RunPublicStaleAttachedReconnectCase(
            AttachedMutation mutation,
            string label)
        {
            using (var harness = TestHarness.Create(LaunchMode.Success, AttachedMutation.Valid))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    label + "前置v3 Attached失败");
                var before = harness.Snapshot();
                var processId = harness.Launcher.LastProcessId;
                Assert(before.IsAttached && before.HasAuthority &&
                       before.AuthorityProcessId == processId &&
                       before.AuthorityInstanceNonce.Length == 32 &&
                       !before.TransportFailClosed,
                    label + "没有完成唯一exact v3 authority promotion");
                var launchCount = harness.Launcher.LaunchCount;
                harness.SetAttachedMutation(mutation);
                harness.ArmForNextServer();
                harness.Engine.ScheduleReconnect(0);
                Assert(WaitUntil(
                           () =>
                           {
                               var snapshot = harness.Snapshot();
                               return snapshot.TransportFailClosed &&
                                      snapshot.TransportFailureKind == WatchdogConnectFailureKind.IdentityRejected;
                           },
                           15000),
                    label + "没有通过public reconnect触发IdentityRejected");
                var rejected = harness.Snapshot();
                Assert(rejected.HasAuthority && IsProcessAlive(processId),
                    label + "错误清除了旧validated authority或kill了权威进程");
                Assert(harness.Launcher.LaunchCount == launchCount,
                    label + "触发了第二次sidecar launch");
                var reconnectGeneration = rejected.ReconnectGeneration;
                var detail = rejected.TransportFailureDetail;
                Assert(!string.IsNullOrWhiteSpace(detail),
                    label + "没有保留IdentityRejected detail");
                Thread.Sleep(250);
                var sticky = harness.Snapshot();
                Assert(sticky.ReconnectGeneration == reconnectGeneration &&
                       sticky.TransportFailureKind == WatchdogConnectFailureKind.IdentityRejected &&
                       sticky.TransportFailureDetail == detail,
                    label + "发生了重复reconnect或sticky detail覆盖");
                harness.Engine.Shutdown();
                harness.SetAttachedMutation(AttachedMutation.Valid);
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(!harness.Snapshot().TransportFailClosed,
                    label + "Shutdown后新Begin仍保留sticky状态");
                harness.Engine.Shutdown();
            }
        }

        private static void PublicDelayedOldAttachedDoesNotPolluteReconnect()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success, AttachedMutation.Valid))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "迟到old Attached测试前置v3握手失败");
                var before = harness.Snapshot();
                var delayedOldAttached = harness.PrepareDelayedOldAttached();
                harness.ArmForNextServer();
                harness.Engine.ScheduleReconnect(0);
                Assert(WaitUntil(
                           () =>
                           {
                               var snapshot = harness.Snapshot();
                               return snapshot.IsAttached &&
                                      !snapshot.ReconnectActive &&
                                      snapshot.ActiveConnectionGeneration >
                                      before.ActiveConnectionGeneration;
                           },
                           15000),
                    "public reconnect没有完成新Attached连接");
                var current = harness.Snapshot();
                harness.ReleaseDelayedOldAttached();
                Assert(delayedOldAttached.Wait(5000),
                    "旧connection delayed Attached没有在有界时间内完成server写尝试");
                Thread.Sleep(150);
                var after = harness.Snapshot();
                Assert(after.IsAttached && after.HasAuthority &&
                       !after.TransportFailClosed &&
                       after.ActiveConnectionGeneration == current.ActiveConnectionGeneration &&
                       after.AuthorityProcessId == current.AuthorityProcessId &&
                       after.AuthorityProcessStartUtcTicks == current.AuthorityProcessStartUtcTicks &&
                       after.AuthorityInstanceNonce == current.AuthorityInstanceNonce,
                    "迟到old Attached污染了新authority或触发sticky fail-closed");
                harness.Engine.Shutdown();
            }
        }

        private static void AssertPublicStickyFailure(
            TestHarness harness,
            Task start,
            WatchdogConnectFailureKind expectedKind,
            string label,
            bool expectPending,
            bool expectAuthority)
        {
            var failure = GetFailure(start) as WatchdogConnectException;
            Assert(failure != null && failure.Kind == expectedKind,
                label + "没有返回预期typed failure：实际=" +
                (failure == null ? "<none>" : failure.Kind.ToString()) +
                ";期望=" + expectedKind);
            var first = harness.Snapshot();
            Assert(first.TransportFailClosed &&
                   first.TransportFailureKind == failure.Kind &&
                   first.TransportFailureDetail == failure.Message &&
                   !string.IsNullOrWhiteSpace(first.TransportFailureDetail),
                label + "没有形成sticky first kind/detail：task=" +
                failure.Kind + ";taskDetail=" + failure.Message +
                ";snapshot=" + first.TransportFailureKind +
                ";detail=" + first.TransportFailureDetail);
            Assert(first.HasAuthority == expectAuthority,
                label + "authority状态不符合fail-closed不变量");
            if (expectPending)
                Assert(first.PendingProcessId == harness.Launcher.LastProcessId,
                    label + "没有保留pending身份");
            Assert(IsProcessAlive(harness.Launcher.LastProcessId),
                label + "fail-closed错误kill了pending/authority进程");
            var launchCount = harness.Launcher.LaunchCount;
            var reconnectGeneration = first.ReconnectGeneration;
            var detail = first.TransportFailureDetail;
            AssertThrows<WatchdogConnectException>(
                () => harness.Engine.StartAsync().Wait(5000),
                label + "sticky期间仍允许第二次Start");
            var sticky = harness.Snapshot();
            Assert(sticky.TransportFailureKind == failure.Kind &&
                   sticky.TransportFailureDetail == failure.Message &&
                   sticky.TransportFailureKind == expectedKind &&
                   sticky.TransportFailureDetail == detail &&
                   sticky.ReconnectGeneration == reconnectGeneration &&
                   harness.Launcher.LaunchCount == launchCount,
                label + "后续Start覆盖了sticky failure或启动了新worker");
        }

        private static int GetProtocolVersion(AttachedMutation mutation)
        {
            switch (mutation)
            {
                case AttachedMutation.ProtocolV0: return 0;
                case AttachedMutation.ProtocolV2: return 2;
                case AttachedMutation.ProtocolV4: return 4;
                default: return WatchdogProtocol.Version;
            }
        }

        private static void SameSessionTransportFailureUsesSingleReconnect()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000), "断链测试前置Attach失败");
                var before = harness.Snapshot();
                var stateBoundary = harness.MarkStateBoundary();
                harness.ArmForNextServer();
                // Exercise the actual NamedPipe boundary.  The production
                // reader/monitor observes this close; no Engine writer or
                // connection identity is reached through reflection.
                harness.CloseCurrentConnection();
                var schedules = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => harness.Engine.ScheduleReconnect(0)))
                    .ToArray();
                Assert(WaitAll(schedules, 5000),
                    "同Session断链后的并发public ScheduleReconnect未在有界时间内返回");
                Assert(WaitUntil(
                           () =>
                           {
                               var snapshot = harness.Snapshot();
                               return snapshot.ReconnectGeneration > before.ReconnectGeneration ||
                                      snapshot.ReconnectActive;
                           },
                           5000),
                    "真实CloseCurrentConnection后没有观察到ReconnectStarted或generation/active变化");
                var worker = harness.Snapshot();
                var generation = worker.ReconnectGeneration;
                Assert(WaitUntil(
                           () => harness.Snapshot().IsAttached &&
                                 !harness.Snapshot().ReconnectActive,
                           15000),
                   "同Session断链后reconnect worker没有恢复Attached");
                var after = harness.Snapshot();
                Assert(after.ReconnectGeneration == before.ReconnectGeneration + 1 &&
                       after.ReconnectGeneration == generation &&
                       harness.Launcher.LaunchCount == 1 &&
                       after.HasAuthority && after.IsAttached &&
                       after.AuthorityProcessId == before.AuthorityProcessId &&
                       after.AuthorityProcessStartUtcTicks == before.AuthorityProcessStartUtcTicks &&
                       after.AuthoritySessionId == before.AuthoritySessionId &&
                       after.AuthorityInstanceNonce == before.AuthorityInstanceNonce,
                    "同Session重连重复创建worker/launch、generation不严格递增或丢失authority：before=" +
                    before.ReconnectGeneration + ",after=" + after.ReconnectGeneration);
                Assert(harness.CountStateEventsForLeaseAfter(
                           before.ActiveSessionLease,
                           stateBoundary,
                           "ReconnectStarted") == 1 &&
                       harness.CountStateEventsForLeaseAfter(
                           before.ActiveSessionLease,
                           stateBoundary,
                           "Reconnected") == 1,
                    "同Session重连没有对该lease各发布恰好一次ReconnectStarted/Reconnected：" +
                    string.Join(",", harness.StateEventsAfter(stateBoundary)
                        .Where(value => value?.Snapshot != null &&
                                        value.Snapshot.ActiveSessionLease == before.ActiveSessionLease)
                        .Select(value => value.EventType)));
                AssertShutdownReceipt(
                    harness.Engine.ShutdownWithReceipt(),
                    before.ActiveSessionLease,
                    "同Session真实断链");
            }
        }

        private static void ReconnectExhaustionIsSingleSafeDegraded()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000), "耗尽测试前置Attach失败");
                var before = harness.Snapshot();
                var stateBoundary = harness.MarkStateBoundary();
                var eventBoundary = harness.EventsSnapshot().Length;
                var createBaseline = harness.Factory.CreateCount;
                var launchBaseline = harness.Launcher.LaunchCount;
                harness.Factory.Disable();
                harness.StopListening();
                var schedules = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => harness.Engine.ScheduleReconnect(0)))
                    .ToArray();
                Assert(WaitAll(schedules, 5000),
                    "重连耗尽前并发public ScheduleReconnect未在有界时间内返回");
                Assert(WaitUntil(
                           () => harness.Snapshot().IsSafeDegraded &&
                                 !harness.Snapshot().ReconnectActive,
                           40000),
                   "pipe失联耗尽后没有进入SafeDegraded");
                Assert(WaitUntil(
                           () => harness.CountEvents("TransportSafeDegraded") == 1 &&
                                 harness.StateEventsAfter(stateBoundary).Any(value =>
                                     value?.Snapshot != null &&
                                     value.Snapshot.ActiveSessionLease == before.ActiveSessionLease &&
                                     string.Equals(value.EventType, "SafeDegraded", StringComparison.Ordinal)) &&
                                 harness.EventsSnapshot().Skip(eventBoundary).Any(value =>
                                     value.StartsWith(
                                         "TransportEvent:WatchdogReconnectExhaustedSafeIdle:",
                                         StringComparison.Ordinal)),
                           5000),
                    "SafeDegraded终态回调/状态事件未完成发布");
                var degraded = harness.Snapshot();
                var stateEvidence = harness.StateEventsAfter(stateBoundary);
                var safeDegradedEvent = stateEvidence.LastOrDefault(value =>
                    value?.Snapshot != null &&
                    value.Snapshot.ActiveSessionLease == before.ActiveSessionLease &&
                    string.Equals(value.EventType, "SafeDegraded", StringComparison.Ordinal));
                var transportLostEvents = harness.EventsSnapshot()
                    .Skip(eventBoundary)
                    .Where(value => value.StartsWith("TransportLost:", StringComparison.Ordinal))
                    .ToArray();
                var exhaustedEvents = harness.EventsSnapshot()
                    .Skip(eventBoundary)
                    .Where(value => value.StartsWith(
                        "TransportEvent:WatchdogReconnectExhaustedSafeIdle:",
                        StringComparison.Ordinal))
                    .ToArray();
                Assert(harness.Factory.CreateCount - createBaseline ==
                           WatchdogTransportPolicy.ReconnectMaxAttempts &&
                       harness.Launcher.LaunchCount == launchBaseline &&
                       degraded.HasAuthority &&
                       degraded.ReconnectGeneration == before.ReconnectGeneration + 1 &&
                       safeDegradedEvent != null &&
                       safeDegradedEvent.Snapshot.ReconnectAttempt ==
                           WatchdogTransportPolicy.ReconnectMaxAttempts &&
                       harness.CountStateEventsForLeaseAfter(
                           before.ActiveSessionLease,
                           stateBoundary,
                           "ReconnectStarted") == 1 &&
                       harness.CountStateEventsForLeaseAfter(
                           before.ActiveSessionLease,
                           stateBoundary,
                           "SafeDegraded") == 1 &&
                       harness.CountEvents("TransportSafeDegraded") == 1 &&
                       transportLostEvents.Length == 1 &&
                       exhaustedEvents.Length == 1,
                    "SafeDegraded耗尽没有严格停在attempt上限或触发重复launch/降级：" +
                    harness.Factory.CreateCount +
                    ";generation=" + degraded.ReconnectGeneration +
                    ";safeAttempt=" +
                    (safeDegradedEvent == null
                        ? -1
                        : safeDegradedEvent.Snapshot.ReconnectAttempt) +
                    ";transportLost=" + string.Join("|", transportLostEvents));
                Assert(degraded.AuthorityProcessId == before.AuthorityProcessId &&
                       degraded.AuthorityProcessStartUtcTicks == before.AuthorityProcessStartUtcTicks &&
                       degraded.AuthoritySessionId == before.AuthoritySessionId &&
                       degraded.AuthorityInstanceNonce == before.AuthorityInstanceNonce &&
                       IsProcessAlive(before.AuthorityProcessId),
                    "SafeDegraded耗尽改变了validated authority四元组或进程已退出");
                var generation = degraded.ReconnectGeneration;
                var stateCountBeforePost = stateEvidence.Length;
                var eventCountBeforePost = harness.EventsSnapshot().Length;
                var createCountBeforePost = harness.Factory.CreateCount;
                var launchCountBeforePost = harness.Launcher.LaunchCount;
                var attemptBeforePost = degraded.ReconnectAttempt;
                var postSchedules = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => harness.Engine.ScheduleReconnect(0)))
                    .ToArray();
                Assert(WaitAll(postSchedules, 5000),
                    "SafeDegraded后的并发public ScheduleReconnect未在有界时间内返回");
                Thread.Sleep(250);
                var post = harness.Snapshot();
                Assert(harness.CountEvents("TransportSafeDegraded") == 1 &&
                       harness.Launcher.LaunchCount == launchCountBeforePost &&
                       harness.Factory.CreateCount == createCountBeforePost &&
                       harness.StateEventsAfter(stateBoundary).Length == stateCountBeforePost &&
                       harness.EventsSnapshot().Length == eventCountBeforePost &&
                       post.ReconnectGeneration == generation &&
                       post.ReconnectAttempt == attemptBeforePost,
                    "SafeDegraded后仍重复启动reconnect/launch");
                Assert(before.AuthorityProcessId == harness.Launcher.LastProcessId &&
                       IsProcessAlive(before.AuthorityProcessId),
                    "pipe失联耗尽错误kill了validated authority");
                AssertShutdownReceipt(
                    harness.Engine.ShutdownWithReceipt(),
                    before.ActiveSessionLease,
                    "同Session重连耗尽");
            }
        }

        private static void ShutdownBeginSessionAbaAcrossWorkers()
        {
            RunConnectAdmissionAbaCase();
            RunAttachedSessionAbaCase();
            RunReconnectSessionAbaCase();
            RunLaunchAdmissionAbaCase();
        }

        private static void RunConnectAdmissionAbaCase()
        {
            var baseline = SidecarProcessHandleOwner.LiveOwnedCount;
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Factory.BlockCreate();
                var oldCallbacks = harness.CreateCallbacks(harness.Options, "aba-connect-old");
                harness.Engine.BeginSession(harness.Options, oldCallbacks);
                // Create is intentionally blocked synchronously by this
                // boundary seam, so execute the public Start on a worker and
                // later close the admitted session through the public receipt.
                var oldStart = Task.Run(() => harness.Engine.StartAsync());
                Assert(WaitUntil(() => harness.Factory.CreateCount > 0, 3000),
                    "connect未安装ABA没有进入生产Connect admission");
                var oldLease = harness.Snapshot().ActiveSessionLease;
                var callbackBoundary = harness.MarkCallbackBoundary("aba-connect-old");
                var stateBoundary = harness.MarkStateBoundary();
                var shutdownTask = Task.Run(() => harness.Engine.ShutdownWithReceipt());
                Assert(WaitUntil(
                           () => harness.Engine.IsClosing &&
                                 !harness.Snapshot().SessionActive,
                           3000),
                    "connect未安装ABA没有观察到public ShutdownWithReceipt进入closing/detached边界");
                // Keep the real ConnectOwner blocked until the production
                // shutdown has detached the old session.  Releasing the
                // public pipe factory then lets its captured reservation
                // settle without any private task/field access.
                harness.Factory.RejectNextCreate();
                harness.Factory.ReleaseCreate();
                Assert(WaitTaskSuccessful(shutdownTask, 7000, out var shutdownFailure),
                    "connect未安装ABA ShutdownWithReceipt未在有界时间内返回：" +
                    (shutdownFailure?.GetBaseException().Message ?? "unknown"));
                Assert(WaitTaskCompleted(oldStart, 7000),
                    "connect未安装ABA旧Start task未在释放真实pipe后结束");
                AssertShutdownReceipt(
                    shutdownTask.Result,
                    oldLease,
                    "connect未安装ABA");
                var postReceiptCallbackBoundary = harness.MarkCallbackBoundary("aba-connect-old");
                var postReceiptStateBoundary = harness.MarkStateBoundary();
                harness.Engine.BeginSession(
                    harness.Options,
                    harness.CreateCallbacks(harness.Options, "aba-connect-new"));
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "connect未安装ABA新session无法重新Attach");
                Assert(harness.CountCallbacksAfter("aba-connect-old", postReceiptCallbackBoundary) == 0,
                    "connect未安装旧task在新session后发布了callback");
                Assert(harness.CountStateEventsForLeaseAfter(oldLease, postReceiptStateBoundary) == 0,
                    "connect未安装旧task在新session后发布了旧lease StateChanged");
                var after = harness.Snapshot();
                Assert(after.SessionActive && after.ActiveSessionLease != oldLease &&
                       after.IsAttached && after.HasAuthority && !after.HasPending &&
                       !after.ConnectActive && !after.ReconnectActive,
                     "connect未安装旧task覆盖了新session状态：active=" + after.SessionActive +
                     ",lease=" + after.ActiveSessionLease + ",oldLease=" + oldLease +
                     ",attached=" + after.IsAttached + ",authority=" + after.HasAuthority +
                     ",pending=" + after.HasPending + ",connect=" + after.ConnectActive +
                     ",reconnect=" + after.ReconnectActive + ",monitor=" + after.MonitorActive);
                Assert(harness.Engine.ShutdownWithReceipt().AllResourcesReleased,
                    "connect未安装ABA新session关闭资源未收口");
            }
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                "connect未安装ABA泄漏了Sidecar句柄");
        }

        private static void RunAttachedSessionAbaCase()
        {
            var baseline = SidecarProcessHandleOwner.LiveOwnedCount;
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                const string phase = "Attached";
                var oldCallbacks = harness.CreateCallbacks(harness.Options, "aba-attached-old");
                harness.Engine.BeginSession(harness.Options, oldCallbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    phase + " ABA前置Attach失败");
                var old = harness.Snapshot();
                var callbackBoundary = harness.MarkCallbackBoundary("aba-attached-old");
                var next = CreateSeededOptions(harness, old);
                harness.ArmForNextServer();
                var receipt = harness.Engine.ShutdownWithReceipt();
                AssertShutdownReceipt(receipt, old.ActiveSessionLease, phase + " ABA");
                var callbackAfterShutdown = harness.MarkCallbackBoundary("aba-attached-old");
                var stateBoundary = harness.MarkStateBoundary();
                harness.Engine.BeginSession(
                    next,
                    harness.CreateCallbacks(next, "aba-attached-new"));
                var fresh = harness.Snapshot();
                Assert(fresh.SessionActive && fresh.ActiveSessionLease != old.ActiveSessionLease,
                    phase + " ABA没有建立新的session lease");
                Assert(harness.Engine.StartAsync().Wait(15000),
                    phase + " ABA新session无法重新Attach");
                Assert(harness.CountCallbacksAfter("aba-attached-old", callbackAfterShutdown) == 0,
                    phase + " ABA旧worker在新session后发布了callback");
                Assert(harness.CountStateEventsForLeaseAfter(old.ActiveSessionLease, stateBoundary) == 0,
                    phase + " ABA旧worker在新session后发布了旧lease StateChanged");
                Assert(WaitUntil(() => harness.Snapshot().HeartbeatAckSequence > 0, 5000),
                    phase + " ABA新session没有取得Heartbeat ACK");
                var beforeSend = harness.Snapshot();
                Assert(harness.Engine.Send(new WatchdogMessage
                {
                    Type = WatchdogMessageType.Ping,
                    SessionId = next.SessionId,
                    Reason = "AbaFreshWriter"
                }),
                    phase + " ABA新session writer/send gate不可用");
                var after = harness.Snapshot();
                Assert(after.ActiveSessionLease == fresh.ActiveSessionLease &&
                       after.IsAttached && after.HasAuthority && !after.HasPending &&
                       after.AuthorityProcessId == beforeSend.AuthorityProcessId &&
                       after.HeartbeatAckSequence >= beforeSend.HeartbeatAckSequence,
                    phase + " ABA旧worker清除了新session authority/writer/ACK状态");
                var activeWorkers = (after.ConnectActive ? 1 : 0) +
                                    (after.ReconnectActive ? 1 : 0) +
                                    (after.MonitorActive ? 1 : 0);
                Assert(activeWorkers <= 1,
                    phase + " ABA新旧worker同时活动：" + activeWorkers);
                Assert(harness.Engine.ShutdownWithReceipt().AllResourcesReleased,
                    phase + " ABA新session资源未收口");
            }
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                "Attached ABA泄漏了Sidecar句柄");
        }

        private static void RunReconnectSessionAbaCase()
        {
            var baseline = SidecarProcessHandleOwner.LiveOwnedCount;
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                var oldCallbacks = harness.CreateCallbacks(harness.Options, "aba-reconnect-old");
                harness.Engine.BeginSession(harness.Options, oldCallbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "reconnect ABA前置Attach失败");
                var old = harness.Snapshot();
                var createBaseline = harness.Factory.CreateCount;
                harness.Factory.BlockCreate();
                harness.ArmForNextServer();
                harness.CloseCurrentConnection();
                harness.Engine.ScheduleReconnect(0);
                Assert(WaitUntil(
                           () => harness.Snapshot().ReconnectActive &&
                                 harness.Factory.CreateCount > createBaseline,
                           5000),
                    "reconnect ABA没有观察到被真实Factory.BlockCreate卡住的public Connect boundary");
                var callbackBoundary = harness.MarkCallbackBoundary("aba-reconnect-old");
                var stateBoundary = harness.MarkStateBoundary();
                var next = CreateSeededOptions(harness, old);
                var shutdowns = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => harness.Engine.ShutdownWithReceipt()))
                    .ToArray();
                Assert(WaitUntil(
                           () => harness.Engine.IsClosing &&
                                 !harness.Snapshot().SessionActive,
                           3000),
                    "reconnect ABA并发ShutdownWithReceipt没有进入closing/detached边界");
                harness.Factory.RejectNextCreate();
                harness.Factory.ReleaseCreate();
                Assert(WaitAll(shutdowns, 10000),
                    "reconnect ABA并发ShutdownWithReceipt未在有界时间内完成");
                Assert(shutdowns.All(task => task.IsCompleted &&
                                             task.Status == TaskStatus.RanToCompletion),
                    "reconnect ABA并发ShutdownWithReceipt存在未成功收口的no-op caller");
                var receipts = shutdowns.Select(task => task.Result).ToArray();
                var authoritativeReceipts = receipts
                    .Where(value => value != null &&
                                    value.SessionLease == old.ActiveSessionLease &&
                                    value.ShutdownEventPublished)
                    .ToArray();
                Assert(authoritativeReceipts.Length == 1,
                    "reconnect ABA必须只有一个携带旧lease ShutdownEventPublished的权威receipt，实际=" +
                    authoritativeReceipts.Length);
                AssertShutdownReceipt(authoritativeReceipts[0], old.ActiveSessionLease, "reconnect ABA");
                var oldShutdownStateEvents = harness.StateEventsAfter(stateBoundary)
                    .Where(value => value != null && value.Snapshot != null &&
                                    value.Snapshot.SessionLease == old.ActiveSessionLease &&
                                    string.Equals(value.EventType, "Shutdown", StringComparison.Ordinal))
                    .ToArray();
                Assert(oldShutdownStateEvents.Length == 1,
                    "reconnect ABA旧lease Shutdown StateChanged必须恰好一次，实际=" +
                    oldShutdownStateEvents.Length + ";state=" +
                    string.Join("|", harness.StateEventsAfter(stateBoundary)
                        .Where(value => value != null && value.Snapshot != null &&
                                        value.Snapshot.SessionLease == old.ActiveSessionLease)
                        .Select(value => value.EventType + ":" + value.Detail)));
                var closed = harness.Snapshot();
                Assert(!closed.SessionActive &&
                       !closed.IsAttached &&
                       !closed.HasPending &&
                       !closed.HasAuthority &&
                       !closed.ConnectActive &&
                       !closed.ReconnectActive &&
                       !closed.MonitorActive &&
                       !closed.LaunchActive &&
                       SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                    "reconnect ABA权威receipt后旧session没有完整收口：session=" +
                    closed.SessionActive + ";attached=" + closed.IsAttached +
                    ";pending=" + closed.HasPending + ";authority=" + closed.HasAuthority +
                    ";connect=" + closed.ConnectActive + ";reconnect=" +
                    closed.ReconnectActive + ";monitor=" + closed.MonitorActive +
                    ";launch=" + closed.LaunchActive + ";handles=" +
                    SidecarProcessHandleOwner.LiveOwnedCount + "/" + baseline);
                var postReceiptCallbackBoundary = harness.MarkCallbackBoundary("aba-reconnect-old");
                var postReceiptStateBoundary = harness.MarkStateBoundary();
                harness.Engine.BeginSession(
                    next,
                    harness.CreateCallbacks(next, "aba-reconnect-new"));
                var fresh = harness.Snapshot();
                Assert(fresh.SessionActive && fresh.ActiveSessionLease != old.ActiveSessionLease,
                    "reconnect ABA没有建立新session lease");
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "reconnect ABA新session无法重新Attach");
                Assert(harness.CountCallbacksAfter("aba-reconnect-old", postReceiptCallbackBoundary) == 0,
                    "reconnect ABA旧worker在新session后发布了callback");
                Assert(harness.CountStateEventsForLeaseAfter(old.ActiveSessionLease, postReceiptStateBoundary) == 0,
                    "reconnect ABA旧worker在新session后发布了旧lease StateChanged");
                var after = harness.Snapshot();
                Assert(after.ActiveSessionLease == fresh.ActiveSessionLease &&
                       after.IsAttached && after.HasAuthority &&
                       harness.Launcher.LaunchCount == 1,
                    "reconnect ABA旧worker清除了新authority或重复launch");
                var activeWorkers = (after.ConnectActive ? 1 : 0) +
                                    (after.ReconnectActive ? 1 : 0) +
                                    (after.MonitorActive ? 1 : 0);
                Assert(activeWorkers <= 1, "reconnect ABA存在多个活动worker");
                Assert(harness.Engine.ShutdownWithReceipt().AllResourcesReleased,
                    "reconnect ABA新session资源未收口");
            }
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                "reconnect ABA泄漏了Sidecar句柄");
        }

        private static void RunLaunchAdmissionAbaCase()
        {
            var baseline = SidecarProcessHandleOwner.LiveOwnedCount;
            using (var harness = TestHarness.Create(LaunchMode.Cancel))
            {
                var oldCallbacks = harness.CreateCallbacks(harness.Options, "aba-launch-old");
                harness.Engine.BeginSession(harness.Options, oldCallbacks);
                var oldStart = harness.Engine.StartAsync();
                Assert(harness.Launcher.Entered.Task.Wait(5000),
                    "launch ABA没有进入生产launch in-flight");
                var old = harness.Snapshot();
                var callbackBoundary = harness.MarkCallbackBoundary("aba-launch-old");
                var stateBoundary = harness.MarkStateBoundary();
                var receipt = harness.Engine.ShutdownWithReceipt();
                AssertShutdownReceipt(receipt, old.ActiveSessionLease, "launch ABA");
                Assert(WaitTaskCompleted(oldStart, 7000),
                    "launch ABA旧Start task未在有界时间内结束");
                Assert(harness.CountCallbacksAfter("aba-launch-old", callbackBoundary) == 0,
                    "launch ABA旧task在新session后发布了callback");
                Assert(harness.CountStateEventsForLeaseAfter(old.ActiveSessionLease, stateBoundary) == 0,
                    "launch ABA旧task在新session后发布了旧lease StateChanged");
                harness.Engine.BeginSession(
                    harness.Options,
                    harness.CreateCallbacks(harness.Options, "aba-launch-new"));
                var after = harness.Snapshot();
                Assert(after.SessionActive &&
                       after.ActiveSessionLease != old.ActiveSessionLease &&
                       !after.LaunchActive && !after.ConnectActive &&
                       !after.ReconnectActive && !after.MonitorActive &&
                       !after.HasPending && !after.HasAuthority &&
                       harness.Launcher.LaunchCount == 1,
                    "launch ABA旧task覆盖了新session或触发了第二次launch");
                Assert(harness.Engine.ShutdownWithReceipt().AllResourcesReleased,
                    "launch ABA新session资源未收口");
            }
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baseline,
                "launch ABA泄漏了Sidecar句柄");
        }

        private static void AssertShutdownReceipt(
            ShutdownReceipt receipt,
            long expectedLease,
            string label)
        {
            Assert(receipt != null, label + "缺少ShutdownReceipt");
            Assert(receipt.SessionLease == expectedLease &&
                   receipt.WorkerTermination != null &&
                   receipt.WorkerTermination.SessionLease == expectedLease,
                label + "receipt lease不匹配");
            var workers = receipt.WorkerTermination;
            Assert(receipt.ShutdownEventPublished && receipt.SessionDetached,
                label + "没有发布Shutdown或完成Session detach");
            Assert(workers.ReaderTerminal && workers.HeartbeatTerminal &&
                   workers.MonitorTerminal && workers.ReconnectTerminal &&
                   workers.ConnectTerminal && workers.LaunchTerminal &&
                   workers.LaunchReservationTerminal && workers.RetainedLaunchTerminal &&
                   workers.PendingOwnerReleased && workers.TransportDetached &&
                   workers.CapturedOwnerReleased && receipt.AllWorkersTerminal &&
                   receipt.AllResourcesReleased,
                label + "receipt没有严格收口六worker/资源：" +
                "reader=" + workers.ReaderTerminal +
                ",heartbeat=" + workers.HeartbeatTerminal +
                ",monitor=" + workers.MonitorTerminal +
                ",reconnect=" + workers.ReconnectTerminal +
                ",connect=" + workers.ConnectTerminal +
                ",launch=" + workers.LaunchTerminal +
                ",resources=" + receipt.AllResourcesReleased);
        }

        private static void StateChangedSnapshotsAreImmutableAndReentrant()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                var eventGate = new object();
                var events = new List<WatchdogClientTransportEvent>();
                var callbackSnapshots = new List<Tuple<WatchdogClientTransportSnapshot, string>>();
                var observerThrowAttempts = 0;
                var reentrantCaptureAndSend = 0;
                var reentrantShutdown = 0;

                // This observer deliberately throws once.  The production
                // dispatch loop must isolate it and continue delivering the
                // remaining immutable notifications.
                harness.Engine.StateChanged += notification =>
                {
                    if (notification == null) return;
                    lock (eventGate)
                    {
                        events.Add(notification);
                        if (notification.Snapshot != null)
                        {
                            // Capture the exact immutable object and its
                            // callback-time signature before any later
                            // transition can run.  The object itself must
                            // remain byte-for-byte stable thereafter.
                            callbackSnapshots.Add(Tuple.Create(
                                notification.Snapshot,
                                SnapshotSignature(notification.Snapshot)));
                        }
                    }
                    if (Interlocked.Exchange(ref observerThrowAttempts, 1) == 0)
                        throw new InvalidOperationException("state observer test fault");

                    if (string.Equals(notification.EventType, "Attached", StringComparison.Ordinal))
                    {
                        // StateChanged is delivered outside the engine gate;
                        // all three reentrant operations must complete.
                        var snapshot = harness.Engine.CaptureSnapshot();
                        var sent = harness.Engine.Send(new WatchdogMessage
                        {
                            ProtocolVersion = WatchdogProtocol.Version,
                            Type = WatchdogMessageType.Ping,
                            SessionId = snapshot.SessionId,
                            Reason = "StateChangedReentrantCaptureSend"
                        });
                        if (snapshot != null && sent)
                            Interlocked.Exchange(ref reentrantCaptureAndSend, 1);
                    }
                    else if (string.Equals(notification.EventType, "Shutdown", StringComparison.Ordinal))
                    {
                        // Re-enter Shutdown from the observer itself.  The
                        // outer call has already detached all mutable state,
                        // so the inner call must be an idempotent no-op.
                        harness.Engine.CaptureSnapshot();
                        harness.Engine.Send(new WatchdogMessage
                        {
                            ProtocolVersion = WatchdogProtocol.Version,
                            Type = WatchdogMessageType.ApplicationClosing,
                            SessionId = notification.Snapshot?.SessionId,
                            Reason = "StateChangedReentrantShutdown"
                        });
                        harness.Engine.Shutdown();
                        Interlocked.Exchange(ref reentrantShutdown, 1);
                    }
                };

                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "StateChanged快照测试前置Attach失败");

                var attached = harness.Snapshot();
                var scheduleCallers = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => harness.Engine.ScheduleReconnect(0)))
                    .ToArray();
                Assert(WaitAll(scheduleCallers, 5000),
                    "并发StateChanged变更调用未在有界时间内返回");
                Assert(WaitUntil(
                           () =>
                           {
                               var snapshot = harness.Snapshot();
                               return snapshot.ReconnectGeneration > attached.ReconnectGeneration &&
                                      snapshot.IsAttached && !snapshot.ReconnectActive &&
                                      snapshot.ActiveConnectionGeneration > attached.ActiveConnectionGeneration;
                           },
                           15000),
                    "并发ScheduleReconnect没有由唯一生产worker完成重连");

                // The observer receives the terminal Shutdown event and
                // re-enters Shutdown/Capture/Send without blocking the caller.
                harness.Engine.Shutdown();

                WatchdogClientTransportEvent[] observed;
                lock (eventGate) observed = events.ToArray();
                Assert(observed.Length >= 4,
                    "StateChanged observer没有收到足够的后续事件，observer异常可能阻断了dispatch");
                for (var index = 0; index < observed.Length; index++)
                {
                    Assert(observed[index] != null && observed[index].Snapshot != null,
                        "StateChanged事件缺少immutable snapshot，index=" + index);
                    Assert(observed[index].Sequence == index + 1,
                        "StateChanged sequence存在gap/乱序：index=" + index +
                        ";sequence=" + observed[index].Sequence);
                    var signature = SnapshotSignature(observed[index].Snapshot);
                    Assert(signature == SnapshotSignature(observed[index].Snapshot),
                        "StateChanged snapshot自身不可稳定读取，sequence=" + observed[index].Sequence);
                }

                // Verify snapshots captured by earlier notifications remained
                // byte-for-byte equivalent after all later transitions.
                foreach (var notification in observed)
                {
                    var before = SnapshotSignature(notification.Snapshot);
                    Thread.Yield();
                    var after = SnapshotSignature(notification.Snapshot);
                    Assert(before == after,
                        "历史StateChanged snapshot被后续状态变更修改，sequence=" + notification.Sequence);
                }
                Tuple<WatchdogClientTransportSnapshot, string>[] callbackObserved;
                lock (eventGate) callbackObserved = callbackSnapshots.ToArray();
                Assert(callbackObserved.Length == observed.Length,
                    "StateChanged回调没有逐事件保存immutable snapshot：callbacks=" +
                    callbackObserved.Length + ";events=" + observed.Length);
                foreach (var callbackSnapshot in callbackObserved)
                {
                    Assert(callbackSnapshot.Item1 != null &&
                           callbackSnapshot.Item2 == SnapshotSignature(callbackSnapshot.Item1),
                        "StateChanged回调瞬间保存的snapshot签名发生变化");
                }
                Assert(observerThrowAttempts == 1,
                    "StateChanged异常隔离测试没有实际触发一次observer异常");
                Assert(reentrantCaptureAndSend == 1,
                    "StateChanged observer无法安全重入Capture/Send");
                Assert(reentrantShutdown == 1,
                    "StateChanged observer无法安全重入Shutdown");
            }
        }

        private static void HeartbeatMonitorAndReconnectCadence()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                var lostAtTicks = 0L;
                var lostAckAgeMs = -1L;
                var lostCount = 0;
                var callbacks = harness.CreateCallbacks(harness.Options, "cadence");
                callbacks.TransportLost = (reason, detail) =>
                {
                    Interlocked.Increment(ref lostCount);
                    Interlocked.Exchange(ref lostAckAgeMs,
                        harness.Snapshot().HeartbeatAckAgeMilliseconds);
                    Interlocked.Exchange(ref lostAtTicks, DateTime.UtcNow.Ticks);
                    // Stop the cadence probe at the first real transport-loss
                    // decision so reconnect backoff cannot extend this probe.
                    harness.Engine.Shutdown();
                };

                var startAt = DateTime.UtcNow;
                harness.Engine.BeginSession(harness.Options, callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "heartbeat cadence测试前置Attach失败");
                Assert(WaitUntil(() => harness.HeartbeatCount >= 1, 2000),
                    "Attached后没有立即发送首个heartbeat");
                var firstHeartbeat = harness.HeartbeatTimesSnapshot().First();
                var firstDelayMs = (firstHeartbeat - startAt).TotalMilliseconds;
                Assert(firstDelayMs >= 0 && firstDelayMs < 1000,
                    "Attached首heartbeat延迟超出即时发送容差：" + firstDelayMs.ToString("F0"));
                Assert(WaitUntil(() => harness.HeartbeatCount >= 2, 2500),
                    "heartbeat 1s周期没有发送第二个heartbeat");
                var heartbeatTimes = harness.HeartbeatTimesSnapshot();
                var secondDelayMs = (heartbeatTimes[1] - heartbeatTimes[0]).TotalMilliseconds;
                Assert(secondDelayMs >= 700 && secondDelayMs <= 1500,
                    "heartbeat周期不在1s合理容差内：" + secondDelayMs.ToString("F0"));
                Assert(WaitUntil(() => harness.Snapshot().HeartbeatAckSequence > 0, 2000),
                    "首heartbeat没有取得ACK，无法验证3s age门禁");

                harness.SetHeartbeatAckEnabled(false);
                var ackDisabledAt = DateTime.UtcNow;
                Assert(WaitUntil(() => Volatile.Read(ref lostCount) == 1, 7000),
                    "ACK停止后monitor没有发布TransportLost");
                var lostAt = new DateTime(
                    Interlocked.Read(ref lostAtTicks),
                    DateTimeKind.Utc);
                var lostElapsedMs = (lostAt - ackDisabledAt).TotalMilliseconds;
                Assert(lostElapsedMs >= 2750 && lostElapsedMs <= 4500,
                    "TransportLost没有按3s ACK age与250ms monitor cadence触发：" +
                    lostElapsedMs.ToString("F0"));
                Assert(Interlocked.Read(ref lostAckAgeMs) >= 3000,
                    "TransportLost在ACK age未跨3s时提前触发：age=" +
                    Interlocked.Read(ref lostAckAgeMs));
                Assert(Volatile.Read(ref lostCount) == 1,
                    "同一ACK age故障重复发布TransportLost");
            }

            var expectedBackoff = new[] { 250, 500, 1000, 2000, 5000, 5000, 5000, 5000 };
            for (var index = 0; index < expectedBackoff.Length; index++)
                Assert(WatchdogTransportPolicy.SelectReconnectBackoffMs(index) == expectedBackoff[index],
                    "reconnect backoff边界错误，attempt=" + index);
            Assert(WatchdogTransportPolicy.SelectReconnectBackoffMs(-1) == 250,
                "负attempt没有钳制到250ms首退避");

            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "reconnect cadence测试前置Attach失败");
                var before = harness.Snapshot();
                harness.Factory.Disable();
                harness.StopListening();
                var createBaseline = harness.Factory.CreateCount;
                harness.Engine.ScheduleReconnect(0);
                var snapshotSamples = new List<WatchdogClientTransportSnapshot>();
                var snapshotGeneration = 0L;
                var snapshotAttempt = before.ReconnectAttempt;
                var snapshotRegressed = false;
                Assert(WaitUntil(
                           () =>
                           {
                               var snapshot = harness.Snapshot();
                               snapshotSamples.Add(snapshot);
                               if (snapshot.ReconnectGeneration < before.ReconnectGeneration)
                                   snapshotRegressed = true;
                               if (snapshotGeneration == 0 &&
                                   snapshot.ReconnectGeneration > before.ReconnectGeneration)
                                   snapshotGeneration = snapshot.ReconnectGeneration;
                               if (snapshotGeneration > 0 &&
                                   snapshot.ReconnectGeneration != snapshotGeneration)
                                   snapshotRegressed = true;
                               if (snapshot.ReconnectAttempt < snapshotAttempt)
                                   snapshotRegressed = true;
                               snapshotAttempt = Math.Max(
                                   snapshotAttempt,
                                   snapshot.ReconnectAttempt);
                               return harness.Factory.CreateCount >= createBaseline + 4;
                           },
                           5000),
                    "reconnect worker没有在有界时间内完成4次真实Factory.Create");
                var reconnectCreateTimes = harness.Factory.CreateTimesSnapshot()
                    .Skip(createBaseline)
                    .Take(4)
                    .ToArray();
                Assert(reconnectCreateTimes.Length == 4,
                    "reconnect真实Connect尝试记录不足4次：" + reconnectCreateTimes.Length);
                var firstToSecondMs =
                    (reconnectCreateTimes[1] - reconnectCreateTimes[0]).TotalMilliseconds;
                var secondToThirdMs =
                    (reconnectCreateTimes[2] - reconnectCreateTimes[1]).TotalMilliseconds;
                var thirdToFourthMs =
                    (reconnectCreateTimes[3] - reconnectCreateTimes[2]).TotalMilliseconds;
                Assert(firstToSecondMs >= 200 && firstToSecondMs <= 1500,
                    "attempt1->2没有遵守250ms退避边界：" + firstToSecondMs.ToString("F0"));
                Assert(secondToThirdMs >= 400 && secondToThirdMs <= 2000,
                    "attempt2->3没有遵守500ms退避边界：" + secondToThirdMs.ToString("F0"));
                Assert(thirdToFourthMs >= 800 && thirdToFourthMs <= 3000,
                    "attempt3->4没有遵守1000ms退避边界：" + thirdToFourthMs.ToString("F0"));
                Assert(!snapshotRegressed &&
                       snapshotSamples.Count >= 4 &&
                       snapshotGeneration > before.ReconnectGeneration &&
                       snapshotAttempt >= 4,
                    "reconnect snapshot发生代次/attempt回退或样本不足：samples=" +
                    snapshotSamples.Count + ";generation=" + snapshotGeneration +
                    ";before=" + before.ReconnectGeneration +
                    ";attempt=" + snapshotAttempt);
                var generation = harness.Snapshot().ReconnectGeneration;
                var concurrentSchedules = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => harness.Engine.ScheduleReconnect(0)))
                    .ToArray();
                Assert(WaitAll(concurrentSchedules, 5000),
                    "并发ScheduleReconnect调用未在有界时间内完成");
                var stillSingle = harness.Snapshot();
                Assert(stillSingle.ReconnectGeneration == generation &&
                       stillSingle.ReconnectActive &&
                       harness.Launcher.LaunchCount == 1 &&
                       stillSingle.ReconnectAttempt >= snapshotAttempt,
                    "并发ScheduleReconnect创建了第二个worker或重置attempt");
                harness.Engine.Shutdown();
                Assert(WaitUntil(() => !harness.Snapshot().ReconnectActive, 5000),
                    "reconnect cadence测试Shutdown后worker未收口");
            }
        }

        private static string SnapshotSignature(WatchdogClientTransportSnapshot snapshot)
        {
            if (snapshot == null) return "<null>";
            return string.Join(
                "|",
                new[]
                {
                    "CapturedUtcTicks=" + snapshot.CapturedUtcTicks,
                    "SessionId=" + snapshot.SessionId,
                    "SessionGeneration=" + snapshot.SessionGeneration,
                    "SessionLease=" + snapshot.SessionLease,
                    "ActiveSessionLease=" + snapshot.ActiveSessionLease,
                    "SessionActive=" + snapshot.SessionActive,
                    "ConnectionGeneration=" + snapshot.ConnectionGeneration,
                    "ActiveConnectionGeneration=" + snapshot.ActiveConnectionGeneration,
                    "AttachedConnectionGeneration=" + snapshot.AttachedConnectionGeneration,
                    "MonitorGeneration=" + snapshot.MonitorGeneration,
                    "MonitorActive=" + snapshot.MonitorActive,
                    "ReconnectGeneration=" + snapshot.ReconnectGeneration,
                    "ReconnectActive=" + snapshot.ReconnectActive,
                    "IsAttached=" + snapshot.IsAttached,
                    "HasPending=" + snapshot.HasPending,
                    "HasAuthority=" + snapshot.HasAuthority,
                    "IsClosing=" + snapshot.IsClosing,
                    "IsSafeDegraded=" + snapshot.IsSafeDegraded,
                    "TransportFailClosed=" + snapshot.TransportFailClosed,
                    "TransportFailureKind=" + snapshot.TransportFailureKind,
                    "TransportFailureDetail=" + snapshot.TransportFailureDetail,
                    "LaunchActive=" + snapshot.LaunchActive,
                    "LaunchClosureBlocked=" + snapshot.LaunchClosureBlocked,
                    "LaunchClosureDetail=" + snapshot.LaunchClosureDetail,
                    "ConnectActive=" + snapshot.ConnectActive,
                    "ConnectGeneration=" + snapshot.ConnectGeneration,
                    "ReconnectAttempt=" + snapshot.ReconnectAttempt,
                    "OwnedHandleCount=" + snapshot.OwnedHandleCount,
                    "HeartbeatAckSequence=" + snapshot.HeartbeatAckSequence,
                    "LastHeartbeatAckUtcTicks=" + snapshot.LastHeartbeatAckUtcTicks,
                    "HeartbeatAckAgeMilliseconds=" + snapshot.HeartbeatAckAgeMilliseconds,
                    "PendingProcessId=" + snapshot.PendingProcessId,
                    "PendingProcessStartUtcTicks=" + snapshot.PendingProcessStartUtcTicks,
                    "PendingSessionId=" + snapshot.PendingSessionId,
                    "PendingSessionGeneration=" + snapshot.PendingSessionGeneration,
                    "PendingInstanceNonce=" + snapshot.PendingInstanceNonce,
                    "AuthorityProcessId=" + snapshot.AuthorityProcessId,
                    "AuthorityProcessStartUtcTicks=" + snapshot.AuthorityProcessStartUtcTicks,
                    "AuthoritySessionId=" + snapshot.AuthoritySessionId,
                    "AuthorityInstanceNonce=" + snapshot.AuthorityInstanceNonce
                });
        }

        /// <summary>
        /// Exercise the production Engine with the real sidecar executable.
        /// The only boundary seams in this acceptance are the operating-system
        /// launcher and NamedPipe factory; all admission, identity promotion,
        /// reconnect and shutdown logic is the production implementation.
        /// </summary>
        private static void RealEngineAttachReconnectShutdownFiftyCycles()
        {
            var sidecarPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "MTTFTest.Watchdog.exe");
            Assert(File.Exists(sidecarPath), "测试输出目录缺少真实Sidecar：" + sidecarPath);

            var processName = Path.GetFileNameWithoutExtension(sidecarPath);
            var baselineOwned = SidecarProcessHandleOwner.LiveOwnedCount;
            var baselineSidecars = GetLiveProcessIds(processName);
            // Warm the real Process/NamedPipe/Task runtime once before taking
            // the OS handle baseline.  This removes one-time CLR/Win32 handle
            // creation from the leak budget while leaving every counted cycle
            // subject to the same stable baseline.
            WarmRealEngineRuntime(sidecarPath, processName, baselineSidecars);
            var processHandleBaseline = CurrentProcessHandleCount();
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baselineOwned,
                "真实Engine warm-up后owned handle没有回基线");
            var observedNonces = new HashSet<string>(StringComparer.Ordinal);

            for (var index = 0; index < 50; index++)
            {
                // Keep the failure paths in this same real-engine acceptance
                // run.  Neither failure path is counted as a normal cycle.
                if (index == 0)
                    RunRealEnginePendingDeathScenario(
                        sidecarPath,
                        processName,
                        baselineOwned,
                        processHandleBaseline,
                        baselineSidecars,
                        "pending-death");
                if (index == 25)
                    RunRealEngineStartupFailureScenario(
                        sidecarPath,
                        processName,
                        baselineOwned,
                        processHandleBaseline,
                        baselineSidecars,
                        "startup-failure");

                var journalDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "MTTFTest.ClientEngine.Real50." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(journalDirectory);
                var options = CreateRealEngineOptions(sidecarPath, journalDirectory, index + 1);
                BootstrapStrictAuthorityForRealEngine(options);
                var callbacks = CreateRealEngineCallbacks(options);
                Process authorityProcess = null;
                try
                {
                   using (var engine = new WatchdogClientTransportEngine(
                               new SystemSidecarProcessLauncher(),
                               new SystemNamedPipeClientFactory()))
                   {
                        var lifecycleGate = new object();
                        var lifecycleEvents = new List<WatchdogClientTransportEvent>();
                        engine.StateChanged += notification =>
                        {
                            if (notification == null) return;
                            lock (lifecycleGate) lifecycleEvents.Add(notification);
                        };
                        engine.BeginSession(options, callbacks);
                        var start = engine.StartAsync();
                        Assert(WaitTaskSuccessful(start, 15000, out var startFailure),
                            "真实Engine首连失败，iteration=" + index +
                            ";error=" + (startFailure?.GetBaseException().Message ?? "unknown"));

                        var first = engine.CaptureSnapshot();
                        Assert(first.IsAttached && first.HasAuthority && !first.HasPending &&
                               !first.LaunchActive && !first.ConnectActive &&
                               first.SessionId == options.SessionId &&
                               first.SessionGeneration == options.SessionGeneration &&
                               first.AuthorityProcessId > 0 &&
                               first.AuthorityProcessStartUtcTicks > 0 &&
                               first.AuthoritySessionId == options.SessionId &&
                               first.AuthorityInstanceNonce.Length == 32 &&
                               WatchdogProcessIdentityPolicy.IsValidChallengeNonce(
                                   first.AuthorityInstanceNonce),
                            "真实Engine首连未形成完整Attached authority，iteration=" + index);
                        Assert(observedNonces.Add(first.AuthorityInstanceNonce),
                            "50圈中出现重复128-bit challenge nonce，iteration=" + index);

                        var sessionLease = first.ActiveSessionLease;
                        var firstConnectionGeneration = first.ActiveConnectionGeneration;
                        var firstSessionGeneration = first.SessionGeneration;
                        var firstSessionId = first.SessionId;
                        var firstPid = first.AuthorityProcessId;
                        var firstStartTicks = first.AuthorityProcessStartUtcTicks;
                        var firstNonce = first.AuthorityInstanceNonce;
                        authorityProcess = Process.GetProcessById(firstPid);
                        var liveBeforeReconnect = GetLiveProcessIds(processName);
                        Assert(liveBeforeReconnect.Contains(firstPid),
                            "Attached authority不在真实Sidecar进程集合中，iteration=" + index);

                        var reconnectGenerationBefore = first.ReconnectGeneration;
                        // Exercise the production reconnect supervisor.  It
                        // closes the exact active connection, reconnects to
                        // the same helper, and owns the reconnect lifecycle;
                        // the acceptance test must not close the pipe or call
                        // ConnectFirstOrLaunch directly.
                        engine.ScheduleReconnect(0);
                        long reconnectStartedGeneration = 0;
                        Assert(WaitUntil(
                                   () =>
                                   {
                                       lock (lifecycleGate)
                                       {
                                           var started = lifecycleEvents.FirstOrDefault(notification =>
                                               notification.Snapshot != null &&
                                               notification.Snapshot.SessionLease == sessionLease &&
                                               notification.EventType == "ReconnectStarted" &&
                                               notification.Snapshot.ReconnectGeneration > reconnectGenerationBefore);
                                           if (started == null) return false;
                                           reconnectStartedGeneration = started.Snapshot.ReconnectGeneration;
                                           return true;
                                       }
                                   },
                                   15000),
                            "真实Engine ScheduleReconnect未发布ReconnectStarted事件栅栏，iteration=" + index);
                        Assert(reconnectStartedGeneration > reconnectGenerationBefore,
                            "真实Engine ReconnectStarted generation未冻结到新generation，iteration=" + index);
                        Assert(WaitUntil(
                                   () =>
                                   {
                                       lock (lifecycleGate)
                                       {
                                           return lifecycleEvents.Any(notification =>
                                               notification.Snapshot != null &&
                                               notification.Snapshot.SessionLease == sessionLease &&
                                               notification.EventType == "Reconnected" &&
                                               notification.Snapshot.ReconnectGeneration == reconnectStartedGeneration);
                                       }
                                   },
                                   15000),
                            "真实Engine Reconnected未按ReconnectStarted同一generation发布，iteration=" + index);
                        Assert(WaitUntil(() => !engine.CaptureSnapshot().ReconnectActive, 5000),
                            "真实Engine Reconnected事件后ReconnectActive未在有界时间内清除，iteration=" + index);
                        var second = engine.CaptureSnapshot();
                        var liveAfterReconnect = GetLiveProcessIds(processName);
                        Assert(second.IsAttached && second.HasAuthority && !second.HasPending &&
                               !second.LaunchActive && !second.ConnectActive &&
                               second.ActiveConnectionGeneration > firstConnectionGeneration &&
                               second.SessionId == firstSessionId &&
                               second.SessionGeneration == firstSessionGeneration &&
                               second.AuthorityProcessId == firstPid &&
                               second.AuthorityProcessStartUtcTicks == firstStartTicks &&
                               second.AuthoritySessionId == firstSessionId &&
                               second.AuthorityInstanceNonce == firstNonce,
                            "同权威重连改变了Session/PID/Start/Nonce或未提升connection generation，iteration=" + index +
                            ";first=" + firstSessionId + "/" + firstSessionGeneration + "/" +
                            firstConnectionGeneration + "/" + firstPid + "/" + firstStartTicks + "/" + firstNonce +
                            ";second=" + second.SessionId + "/" + second.SessionGeneration + "/" +
                            second.ActiveConnectionGeneration + "/" + second.AuthorityProcessId + "/" +
                            second.AuthorityProcessStartUtcTicks + "/" + second.AuthorityInstanceNonce +
                            ";attached=" + second.IsAttached + ";pending=" + second.HasPending +
                            ";launch=" + second.LaunchActive + ";connect=" + second.ConnectActive);
                        Assert(!liveAfterReconnect.Except(liveBeforeReconnect).Any(),
                            "同权威重连产生了新的Sidecar进程集合，iteration=" + index +
                            ";before=" + string.Join(",", liveBeforeReconnect) +
                            ";after=" + string.Join(",", liveAfterReconnect));
                        Assert(!second.HasPending && !second.LaunchActive &&
                               SidecarProcessHandleOwner.LiveOwnedCount == baselineOwned,
                            "同权威重连后遗留pending/launch/owned handle，iteration=" + index);

                        // The close marker is sent after MarkSessionClosing so
                        // no late worker can publish into a new session while
                        // the exact helper is exiting.  ShutdownWithReceipt
                        // is the production lifecycle boundary; the test does
                        // not inspect private Task fields or transient flags.
                        engine.MarkSessionClosing();
                        var sentClosing = engine.Send(new WatchdogMessage
                        {
                            ProtocolVersion = WatchdogProtocol.Version,
                            Type = WatchdogMessageType.ApplicationClosing,
                            SessionId = options.SessionId,
                            Reason = "RealEngine50CycleComplete"
                        });
                        Assert(sentClosing,
                            "真实Engine无法发送ApplicationClosing，iteration=" + index);
                        Assert(WaitUntil(() => !IsProcessAlive(firstPid), 5000),
                            "真实Sidecar未按ApplicationClosing退出，iteration=" + index +
                            ";pid=" + firstPid);
                        var receipt = engine.ShutdownWithReceipt();
                        Assert(receipt != null &&
                               receipt.SessionLease == sessionLease &&
                               receipt.ShutdownEventPublished &&
                               receipt.AllWorkersTerminal &&
                               receipt.AllResourcesReleased &&
                               receipt.TransportDetached &&
                               receipt.PendingOwnerReleased &&
                               receipt.LaunchReservationTerminal &&
                               receipt.RetainedLaunchTerminal &&
                               receipt.LiveOwnedHandleCount == 0,
                            "真实Engine Shutdown receipt未证明六worker/资源收口，iteration=" + index);
                        Assert(WaitUntil(
                                   () =>
                                   {
                                       lock (lifecycleGate)
                                       {
                                           return lifecycleEvents.Any(notification =>
                                               notification.EventType == "Shutdown" &&
                                               notification.Snapshot != null &&
                                               notification.Snapshot.SessionLease == sessionLease);
                                       }
                                   },
                                   5000),
                            "真实Engine Shutdown事件未按同一session lease发布，iteration=" + index);
                        var closed = engine.CaptureSnapshot();
                        Assert(!closed.SessionActive && !closed.IsAttached &&
                               !closed.HasPending && !closed.HasAuthority &&
                               !closed.LaunchActive && !closed.ConnectActive &&
                               !closed.MonitorActive && !closed.ReconnectActive,
                            "真实Engine Shutdown receipt后snapshot仍有活动worker，iteration=" + index);
                    }
                }
                finally
                {
                    KillAndDisposeProcess(authorityProcess);
                    try { if (Directory.Exists(journalDirectory)) Directory.Delete(journalDirectory, true); }
                    catch { }
                }

                Assert(SidecarProcessHandleOwner.LiveOwnedCount == baselineOwned,
                    "真实Engine 50圈中owned handle未回基线，iteration=" + index);
                AssertRealProcessHandleCountBounded(
                    processHandleBaseline,
                    "真实Engine 50圈中OS handle超出稳定基线，iteration=" + index);
            }

            Assert(observedNonces.Count == 50,
                "真实Engine 50圈没有获得50个互异challenge nonce：" + observedNonces.Count);
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baselineOwned,
                "真实Engine 50圈结束owned handle未回基线");
            AssertRealProcessHandleCountBounded(
                processHandleBaseline,
                "真实Engine 50圈结束OS handle超出稳定基线");
            var remainingSidecars = GetLiveProcessIds(processName);
            Assert(!remainingSidecars.Except(baselineSidecars).Any(),
                "真实Engine 50圈结束仍有Sidecar进程存活：" +
                string.Join(",", remainingSidecars));
        }

        private static void WarmRealEngineRuntime(
            string sidecarPath,
            string processName,
            HashSet<int> baselineSidecars)
        {
            var journalDirectory = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.ClientEngine.Warm." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journalDirectory);
            var options = CreateRealEngineOptions(sidecarPath, journalDirectory, 7000);
            BootstrapStrictAuthorityForRealEngine(options);
            var callbacks = CreateRealEngineCallbacks(options);
            Process authorityProcess = null;
            try
            {
                using (var engine = new WatchdogClientTransportEngine(
                           new SystemSidecarProcessLauncher(),
                           new SystemNamedPipeClientFactory()))
                {
                    engine.BeginSession(options, callbacks);
                    var start = engine.StartAsync();
                    Assert(WaitTaskSuccessful(start, 15000, out var failure),
                        "真实Engine warm-up首连失败：" +
                        (failure?.GetBaseException().Message ?? "unknown"));
                    var snapshot = engine.CaptureSnapshot();
                    Assert(snapshot.IsAttached && snapshot.HasAuthority && !snapshot.HasPending,
                        "真实Engine warm-up未形成Attached authority");
                    authorityProcess = Process.GetProcessById(snapshot.AuthorityProcessId);
                    Assert(engine.Send(new WatchdogMessage
                    {
                        ProtocolVersion = WatchdogProtocol.Version,
                        Type = WatchdogMessageType.ApplicationClosing,
                        SessionId = options.SessionId,
                        Reason = "RealEngineWarmupComplete"
                    }), "真实Engine warm-up无法发送ApplicationClosing");
                    Assert(WaitUntil(() => !IsProcessAlive(snapshot.AuthorityProcessId), 5000),
                        "真实Engine warm-up Sidecar未退出");
                    engine.Shutdown();
                }
            }
            finally
            {
                KillAndDisposeProcess(authorityProcess);
                try { if (Directory.Exists(journalDirectory)) Directory.Delete(journalDirectory, true); }
                catch { }
            }
            Assert(!GetLiveProcessIds(processName).Except(baselineSidecars).Any(),
                "真实Engine warm-up产生了未收口的Sidecar");
        }

        private static void RunRealEnginePendingDeathScenario(
            string sidecarPath,
            string processName,
            int baselineOwned,
            int processHandleBaseline,
            HashSet<int> baselineSidecars,
            string label)
        {
            var journalDirectory = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.ClientEngine." + label + "." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journalDirectory);
            var options = CreateRealEngineOptions(sidecarPath, journalDirectory, 9001);
            BootstrapStrictAuthorityForRealEngine(options);
            var callbacks = CreateRealEngineCallbacks(options);
            var observedPid = 0;
            var killed = 0;
            var callbackError = string.Empty;
            Process pendingProcess = null;
            try
            {
                using (var engine = new WatchdogClientTransportEngine(
                           new SystemSidecarProcessLauncher(),
                           new SystemNamedPipeClientFactory()))
                {
                    engine.StateChanged += notification =>
                    {
                        if (notification == null ||
                            !string.Equals(notification.EventType, "PendingPublished", StringComparison.Ordinal))
                            return;
                        var pid = notification.Snapshot?.PendingProcessId ?? 0;
                        Interlocked.Exchange(ref observedPid, pid);
                        if (pid <= 0)
                        {
                            callbackError = "PendingPublished没有真实PID";
                            return;
                        }
                        try
                        {
                            pendingProcess = Process.GetProcessById(pid);
                            if (!pendingProcess.HasExited) pendingProcess.Kill();
                            if (!pendingProcess.WaitForExit(5000))
                                throw new TimeoutException("pending process kill后未退出");
                            Interlocked.Exchange(ref killed, 1);
                        }
                        catch (Exception ex)
                        {
                            callbackError = ex.GetBaseException().Message;
                        }
                    };
                    engine.BeginSession(options, callbacks);
                    var start = Task.Run(() => engine.StartAsync());
                    var failure = WaitTaskFailure(start, 15000);
                    Assert(failure != null,
                        "真实Engine pending death没有返回失败结果，label=" + label);
                    Assert(Volatile.Read(ref killed) == 1,
                        "真实Engine pending death没有杀掉StateChanged报告的精确进程，label=" + label +
                        ";pid=" + observedPid + ";error=" + callbackError);
                    Assert(observedPid > 0 && !IsProcessAlive(observedPid),
                        "真实Engine pending death后进程仍存活，label=" + label +
                        ";pid=" + observedPid);
                    Assert(WaitUntil(
                               () =>
                               {
                                   var snapshot = engine.CaptureSnapshot();
                                   return !snapshot.SessionActive && !snapshot.HasPending &&
                                          !snapshot.HasAuthority && !snapshot.LaunchActive &&
                                          !snapshot.ConnectActive && !snapshot.MonitorActive &&
                                          !snapshot.ReconnectActive;
                               },
                               5000),
                        "真实Engine pending death没有收口全部worker，label=" + label +
                        ";failure=" + failure.GetBaseException().Message);
                    engine.Shutdown();
                }
            }
            finally
            {
                KillAndDisposeProcess(pendingProcess);
                try { if (Directory.Exists(journalDirectory)) Directory.Delete(journalDirectory, true); }
                catch { }
            }
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baselineOwned,
                "真实Engine pending death泄漏owned handle，label=" + label);
            AssertRealProcessHandleCountBounded(
                processHandleBaseline,
                "真实Engine pending death泄漏OS handle，label=" + label);
            Assert(!GetLiveProcessIds(processName).Except(baselineSidecars).Any(),
                "真实Engine pending death后仍有Sidecar存活，label=" + label);
        }

        private static void RunRealEngineStartupFailureScenario(
            string sidecarPath,
            string processName,
            int baselineOwned,
            int processHandleBaseline,
            HashSet<int> baselineSidecars,
            string label)
        {
            var rootDirectory = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.ClientEngine." + label + "." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDirectory);
            var invalidJournalPath = Path.Combine(rootDirectory, "journal-file-not-directory");
            File.WriteAllText(invalidJournalPath, "invalid journal directory marker");
            var options = CreateRealEngineOptions(sidecarPath, invalidJournalPath, 9002);
            // Keep the real executable path and default launcher/factory, but
            // force the actual Process.Start argument validation to fail
            // before a helper can be published.  This is the production
            // launch/startup-failure transaction (the pending-death scenario
            // above covers a helper that has already been published).
            options.MainExecutablePath = "\0";
            var callbacks = CreateRealEngineCallbacks(options);
            var observedPid = 0;
            try
            {
                using (var engine = new WatchdogClientTransportEngine(
                           new SystemSidecarProcessLauncher(),
                           new SystemNamedPipeClientFactory()))
                {
                    engine.StateChanged += notification =>
                    {
                        if (notification == null ||
                            !string.Equals(notification.EventType, "PendingPublished", StringComparison.Ordinal))
                            return;
                        Interlocked.Exchange(
                            ref observedPid,
                            notification.Snapshot?.PendingProcessId ?? 0);
                    };
                    engine.BeginSession(options, callbacks);
                    var start = Task.Run(() => engine.StartAsync());
                    var failure = WaitTaskFailure(start, 15000);
                    Assert(failure != null,
                        "真实Engine startup failure没有返回失败结果，label=" + label);
                    // Launch failure is reported to the caller first.  The
                    // production owner then closes the failed startup
                    // transaction explicitly; this is the same cleanup
                    // boundary used by the host and ensures no launch/connect
                    // reservation is left in the snapshot.
                    engine.CloseFailedStartup();
                    if (observedPid > 0)
                        Assert(WaitUntil(() => !IsProcessAlive(observedPid), 5000),
                            "真实Engine startup failure Sidecar未退出，label=" + label +
                            ";pid=" + observedPid);
                    Assert(WaitUntil(
                               () =>
                               {
                                   var snapshot = engine.CaptureSnapshot();
                                   return !snapshot.SessionActive && !snapshot.HasPending &&
                                          !snapshot.HasAuthority && !snapshot.LaunchActive &&
                                          !snapshot.ConnectActive && !snapshot.MonitorActive &&
                                          !snapshot.ReconnectActive;
                               },
                               5000),
                        "真实Engine startup failure没有收口全部worker，label=" + label +
                        ";failure=" + failure.GetBaseException().Message);
                    engine.Shutdown();
                }
            }
            finally
            {
                try { if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, true); }
                catch { }
            }
            Assert(SidecarProcessHandleOwner.LiveOwnedCount == baselineOwned,
                "真实Engine startup failure泄漏owned handle，label=" + label);
            AssertRealProcessHandleCountBounded(
                processHandleBaseline,
                "真实Engine startup failure泄漏OS handle，label=" + label);
            Assert(!GetLiveProcessIds(processName).Except(baselineSidecars).Any(),
                "真实Engine startup failure后仍有Sidecar存活，label=" + label);
        }

        private static WatchdogClientTransportOptions CreateRealEngineOptions(
            string sidecarPath,
            string journalDirectory,
            int generation)
        {
            var session = Guid.NewGuid().ToString("N");
            return new WatchdogClientTransportOptions
            {
                SessionId = session,
                PipeName = "MTTFTest.ClientEngine.Real." + session,
                MainExecutablePath = CurrentExecutablePath(),
                SidecarExecutablePath = sidecarPath,
                JournalDirectory = journalDirectory,
                JournalPolicy = new WatchdogJournalPolicy(),
                SessionGeneration = generation,
                RecoveryAttempt = 0,
                SelectedChannels = new[] { 4 }
            };
        }

        private static void BootstrapStrictAuthorityForRealEngine(
            WatchdogClientTransportOptions options)
        {
            Assert(options != null, "real Engine options missing");
            using (var process = Process.GetCurrentProcess())
            {
                var created = DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                    options.JournalDirectory,
                    options.SessionId,
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks,
                    RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit);
                Assert(created != null && created.Succeeded &&
                       created.Authority != null &&
                       created.Authority.Snapshot.RecordFormatRevision ==
                           DurableRelaunchAuthorityV4Validator.RequiredFormatRevision,
                    "real Engine strict authority bootstrap failed: " +
                    (created?.Reason ?? "null"));
            }
        }

        private static WatchdogClientTransportCallbacks CreateRealEngineCallbacks(
            WatchdogClientTransportOptions options)
        {
            return new WatchdogClientTransportCallbacks
            {
                CreateRunSession = (recovery, attempt) =>
                {
                    using (var process = Process.GetCurrentProcess())
                    {
                        return new WatchdogRunSession
                        {
                            SessionId = options.SessionId,
                            PipeName = options.PipeName,
                            ExecutablePath = options.MainExecutablePath,
                            ProcessId = process.Id,
                            ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                            RecoveryProcess = recovery,
                            RecoveryAttempt = attempt,
                            SelectedChannels = options.SelectedChannels.ToArray()
                        };
                    }
                },
                CaptureHeartbeat = () => new WatchdogHeartbeat
                {
                    SessionId = options.SessionId,
                    Phase = "RealEngineTransport50"
                },
                RecordEvent = (eventType, detail) => { },
                TransportError = (reason, detail) => { },
                TransportLost = (reason, detail) => { },
                StopAllRequested = (reason, correlation) => { },
                ObserveDurableStopMarker = () => false
            };
        }

        private static HashSet<int> GetLiveProcessIds(string processName)
        {
            var ids = new HashSet<int>();
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!process.HasExited) ids.Add(process.Id);
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }
            return ids;
        }

        private static string CurrentExecutablePath()
        {
            using (var process = Process.GetCurrentProcess())
                return process.MainModule?.FileName ??
                       Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AdaptiveControlTests.exe");
        }

        private static int CurrentProcessHandleCount()
        {
            using (var process = Process.GetCurrentProcess())
                return process.HandleCount;
        }

        private static bool WaitTaskSuccessful(
            Task task,
            int timeoutMs,
            out Exception failure)
        {
            failure = null;
            if (task == null)
            {
                failure = new InvalidOperationException("task为null");
                return false;
            }
            try
            {
                if (!task.Wait(timeoutMs))
                {
                    failure = new TimeoutException("task在" + timeoutMs + "ms内未完成");
                    return false;
                }
            }
            catch (AggregateException ex)
            {
                failure = ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
                return false;
            }
            if (task.IsCanceled)
            {
                failure = new TaskCanceledException("task已取消");
                return false;
            }
            if (task.IsFaulted)
            {
                failure = GetFailure(task) ?? new InvalidOperationException("task失败但没有异常");
                return false;
            }
            return task.Status == TaskStatus.RanToCompletion;
        }

        private static Exception WaitTaskFailure(Task task, int timeoutMs)
        {
            if (task == null) return new InvalidOperationException("task为null");
            try
            {
                if (!task.Wait(timeoutMs))
                    return new TimeoutException("预期失败task在" + timeoutMs + "ms内未完成");
            }
            catch (AggregateException ex)
            {
                return ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
            }
            if (task.IsCanceled) return new TaskCanceledException("预期失败task已取消");
            if (task.IsFaulted) return GetFailure(task) ?? new InvalidOperationException("task失败但没有异常");
            return null;
        }

        private static void KillAndDisposeProcess(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch { }
            try { process.Dispose(); } catch { }
        }

        private static void AssertRealProcessHandleCountBounded(
            int baseline,
            string message)
        {
            const int toleratedTransientHandles = 16;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var observed = CurrentProcessHandleCount();
                if (observed <= baseline + toleratedTransientHandles)
                    return;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(250);
            }
            var current = CurrentProcessHandleCount();
            Assert(current <= baseline + toleratedTransientHandles,
                message + ";baseline=" + baseline + ";current=" + current +
                ";allowed=" + (baseline + toleratedTransientHandles));
        }

        private static WatchdogClientTransportOptions CreateSeededOptions(
            TestHarness harness,
            WatchdogClientTransportSnapshot source)
        {
            var options = harness.Options;
            return new WatchdogClientTransportOptions
            {
                SessionId = options.SessionId,
                PipeName = options.PipeName,
                MainExecutablePath = options.MainExecutablePath,
                SidecarExecutablePath = options.SidecarExecutablePath,
                JournalDirectory = options.JournalDirectory,
                JournalPolicy = options.JournalPolicy,
                SelectedChannels = options.SelectedChannels == null
                    ? new int[0]
                    : options.SelectedChannels.ToArray(),
                RecoveryProcess = options.RecoveryProcess,
                RecoveryAttempt = options.RecoveryAttempt,
                SessionGeneration = checked(source.SessionGeneration + 1),
                AuthoritySeed = new SidecarIdentityStateMachine.AuthoritySnapshot(
                    source.AuthorityProcessId,
                    source.AuthorityProcessStartUtcTicks,
                    source.AuthoritySessionId,
                    source.AuthorityInstanceNonce)
            };
        }

        private static WatchdogClientTransportOptions CreateAttachOnlyOptions(
            TestHarness harness,
            WatchdogClientTransportSnapshot source,
            long generation)
        {
            return CreateAttachOnlyOptionsFromAuthority(
                harness,
                source == null
                    ? null
                    : new SidecarIdentityStateMachine.AuthoritySnapshot(
                        source.AuthorityProcessId,
                        source.AuthorityProcessStartUtcTicks,
                        source.AuthoritySessionId,
                        source.AuthorityInstanceNonce),
                generation);
        }

        private static WatchdogClientTransportOptions CreateAttachOnlyOptionsFromAuthority(
            TestHarness harness,
            SidecarIdentityStateMachine.AuthoritySnapshot authority,
            long generation)
        {
            var options = harness.Options;
            return new WatchdogClientTransportOptions
            {
                SessionId = options.SessionId,
                PipeName = options.PipeName,
                MainExecutablePath = options.MainExecutablePath,
                SidecarExecutablePath = options.SidecarExecutablePath,
                JournalDirectory = options.JournalDirectory,
                JournalPolicy = options.JournalPolicy,
                SelectedChannels = options.SelectedChannels == null
                    ? new int[0]
                    : options.SelectedChannels.ToArray(),
                RecoveryProcess = true,
                RecoveryAttempt = options.RecoveryAttempt,
                SessionGeneration = generation,
                LaunchPolicy = WatchdogLaunchPolicy.AttachExistingAuthorityOnly,
                AuthoritySeed = authority
            };
        }

        private static bool WaitTaskCompleted(Task task, int timeoutMs)
        {
            if (task == null) return true;
            try
            {
                task.Wait(timeoutMs);
            }
            catch (AggregateException) { }
            return task.IsCompleted;
        }

        private static void SendFailureUsesExactConnectionIdentity()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                const string label = "send-public";
                var callbacks = harness.CreateCallbacks(harness.Options, label);
                harness.Engine.BeginSession(harness.Options, callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000), "首次连接失败");
                var first = harness.Snapshot();
                Assert(first.IsAttached && first.ActiveConnectionGeneration > 0,
                    "首次连接没有形成可发送的生产connection");
                Assert(harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    SessionId = first.SessionId,
                    Reason = "PublicSendNormal"
                }), "当前connection public Send正常路径失败");

                // Hold the server read side so the production writer really
                // crosses its timeout/close path.  BeginSession then races the
                // old public Send; its late failure must not reach new
                // callbacks or the new send-failure gate.
                harness.BlockIncomingReads();
                var largeReason = new string('x', 8 * 1024 * 1024);
                var oldSend = Task.Run(() => harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    SessionId = first.SessionId,
                    Reason = largeReason
                }));
                Thread.Sleep(75);
                var callbackBoundary = harness.MarkCallbackBoundary(label);
                harness.ArmForNextServer();
                harness.CloseCurrentConnection();
                Assert(harness.WaitForNextServer(5000),
                    "旧connection关闭后server没有准备下一管道");
                var next = CreateSeededOptions(harness, first);
                var nextCallbacks = harness.CreateCallbacks(next, label);
                harness.Engine.BeginSession(next, nextCallbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "BeginSession切换后新connection建立失败");
                Assert(WaitTaskCompleted(oldSend, 5000),
                    "旧connection真实writer Send没有在有界时间内结束");
                Assert(!oldSend.Result,
                    "旧connection关闭后的真实writer Send意外报告成功");
                Assert(harness.CountCallbacksAfterContaining(
                           label,
                           callbackBoundary,
                           "TransportError:Send") == 0,
                    "旧lease真实writer失败污染了新Session callback：" +
                    string.Join("|", harness.CallbackEntriesAfter(callbackBoundary)));
                Assert(harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    SessionId = next.SessionId,
                    Reason = "PublicSendAfterBegin"
                }), "新connection public Send失败");

                // Drive the exact public no-writer path twice.  Only the
                // controlled NamedPipe endpoint is manipulated; no Engine
                // field or private failure method is inspected.
                harness.Factory.Disable();
                var noWriterSeed = harness.Snapshot();
                harness.ArmForNextServer();
                harness.CloseCurrentConnection();
                Assert(harness.WaitForNextServer(5000),
                    "当前connection断开后没有生成下一管道实例");
                // BeginSession is public and is the lifecycle boundary that
                // guarantees the previous transport writer is detached.
                var noWriterSession = CreateSeededOptions(harness, noWriterSeed);
                harness.Engine.BeginSession(
                    noWriterSession,
                    harness.CreateCallbacks(noWriterSession, label));
                Assert(!harness.Snapshot().IsAttached,
                    "BeginSession后的新Session错误宣称Attached");
                var noWriterBefore = harness.CountCallbacksAfterContaining(
                    label,
                    0,
                    "TransportError:SendNoWriter");
                Assert(!harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    SessionId = noWriterSession.SessionId,
                    Reason = "PublicNoWriterFirst"
                }), "当前断链public Send NoWriter意外成功");
                Assert(!harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    SessionId = noWriterSession.SessionId,
                    Reason = "PublicNoWriterDuplicate"
                }), "重复public Send NoWriter意外成功");
                Assert(WaitUntil(
                           () => harness.CountCallbacksAfterContaining(
                               label, 0, "TransportError:SendNoWriter") == noWriterBefore + 1,
                           3000),
                    "当前断链NoWriter没有恰好通知一次callback：" +
                    string.Join("|", harness.CallbackEntriesAfter(0)));

                // A new connection's successful public Send resets the gate.
                // A later disconnect must permit a new single NoWriter event.
                harness.Factory.Enable();
                var nextState = harness.Snapshot();
                var fresh = CreateSeededOptions(harness, nextState);
                harness.Engine.BeginSession(fresh, harness.CreateCallbacks(fresh, label));
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "NoWriter后新connection建立失败");
                Assert(harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    SessionId = fresh.SessionId,
                    Reason = "PublicSendReset"
                }), "新connection正常Send没有复位failure gate");
                harness.Factory.Disable();
                var freshSeed = harness.Snapshot();
                harness.ArmForNextServer();
                harness.CloseCurrentConnection();
                Assert(harness.WaitForNextServer(5000),
                    "复位后connection断开没有生成下一管道实例");
                var resetSession = CreateSeededOptions(harness, freshSeed);
                harness.Engine.BeginSession(
                    resetSession,
                    harness.CreateCallbacks(resetSession, label));
                Assert(!harness.Snapshot().IsAttached,
                    "复位后新Session错误宣称Attached");
                Assert(!harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Ping,
                    SessionId = resetSession.SessionId,
                    Reason = "PublicNoWriterAfterReset"
                }), "复位后的public NoWriter意外成功");
                Assert(WaitUntil(
                           () => harness.CountCallbacksAfterContaining(
                               label, 0, "TransportError:SendNoWriter") == noWriterBefore + 2,
                           3000),
                    "新connection正常Send后没有允许下一次独立NoWriter");
                harness.Engine.Shutdown();
            }
        }

        private static void SendBusyDoesNotTearHealthyConnection(int iteration)
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                var entered = harness.ArmReadBlockAfterAttach();
                harness.Engine.BeginSession(
                    harness.Options,
                    harness.CreateCallbacks(harness.Options, "send-busy-" + iteration));
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "SendBusy前置Attach失败；iteration=" + iteration +
                    ";stats=" + harness.ServerStats +
                    ";events=" + string.Join("|", harness.EventsSnapshot()));
                Assert(entered.Wait(5000),
                    "服务端未在Attach后进入真实读取屏障；iteration=" + iteration);

                var before = harness.Snapshot();
                var large = new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.RunStopped,
                    SessionId = before.SessionId,
                    // Large enough to hold the production writer on the
                    // blocked named-pipe read, but small enough to drain well
                    // inside the 500 ms production write deadline once the
                    // barrier is released.
                    Reason = new string('x', 1024 * 1024)
                };
                var largeSend = Task.Run(() => harness.Engine.Send(large));
                Assert(WaitUntil(() => !largeSend.IsCompleted, 1000),
                    "大消息没有进入真实发送路径；iteration=" + iteration);
                Task<WatchdogSendDisposition>[] busySends;
                using (var ready = new CountdownEvent(32))
                using (var start = new ManualResetEventSlim(false))
                {
                    busySends = Enumerable.Range(0, 32)
                        .Select(_ => Task.Factory.StartNew(
                            () =>
                            {
                                ready.Signal();
                                start.Wait();
                                return harness.Engine.TrySendForSessionWithDisposition(
                                    new WatchdogMessage
                                    {
                                        ProtocolVersion = WatchdogProtocol.Version,
                                        Type = WatchdogMessageType.RunStopped,
                                        SessionId = before.SessionId,
                                        Reason = "busy-probe"
                                    },
                                    before.SessionId,
                                    before.SessionGeneration,
                                    before.ActiveSessionLease);
                            },
                            CancellationToken.None,
                            TaskCreationOptions.LongRunning,
                            TaskScheduler.Default))
                        .ToArray();
                    Assert(ready.Wait(5000),
                        "SendBusy并发调用未全部到达启动屏障；iteration=" + iteration);
                    start.Set();
                }
                // The production writer deadline is 500ms.  Release the
                // server barrier independently so the test never converts a
                // scheduling delay into a transport timeout.
                Task.Run(async () =>
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    harness.ReleaseReadBlock();
                });
                Assert(busySends.All(task => task.Wait(3000)),
                    "SendBusy并发调用没有在有界时间内返回；iteration=" + iteration);
                Assert(busySends.Any(task =>
                           task.Result == WatchdogSendDisposition.AdmissionBusy),
                    "生命周期保留队列饱和后没有返回AdmissionBusy；iteration=" + iteration);

                // Release before the production 500ms write deadline.  The
                // held send may then complete; it must not make the healthy
                // authority look like a transport loss.
                harness.ReleaseReadBlock();
                Assert(largeSend.Wait(5000),
                    "大消息释放后仍未完成；iteration=" + iteration);
                var successfulBusySends = busySends.Count(task =>
                    task.Result == WatchdogSendDisposition.Sent);
                Assert(WaitUntil(
                           () => harness.CountReceivedReason("busy-probe") ==
                                 successfulBusySends,
                           3000),
                    "已返回AdmissionBusy的请求仍在队列中迟到写出；" +
                    "iteration=" + iteration +
                    ";successful=" + successfulBusySends +
                    ";received=" + harness.CountReceivedReason("busy-probe"));
                Assert(WaitUntil(() =>
                {
                    var current = harness.Snapshot();
                    return current.IsAttached &&
                           current.ActiveSessionLease == before.ActiveSessionLease &&
                           current.ActiveConnectionGeneration == before.ActiveConnectionGeneration;
                }, 3000),
                    "SendBusy后连接身份未保持不变；iteration=" + iteration +
                    ";stats=" + harness.ServerStats);
                var after = harness.Snapshot();
                Assert(!after.TransportFailClosed && !after.IsSafeDegraded &&
                       harness.CountCallbacksAfterContaining(
                           "send-busy-" + iteration, 0, "TransportLost") == 0,
                    "SendBusy错误触发了TransportLost/SafeDegraded；iteration=" + iteration +
                    ";events=" + string.Join("|", harness.EventsSnapshot()));
                Assert(harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.RunStopped,
                    SessionId = before.SessionId,
                    Reason = "post-busy"
                }), "SendBusy后正常发送未恢复；iteration=" + iteration);
            }
        }

        private static void WriteFailureBreaksExactConnectionAndReconnects()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                const string label = "send-write-failure";
                harness.Engine.BeginSession(
                    harness.Options,
                    harness.CreateCallbacks(harness.Options, label));
                Assert(harness.Engine.StartAsync().Wait(15000), "写失败前置Attach失败");
                var before = harness.Snapshot();
                var callbackBoundary = harness.MarkCallbackBoundary(label);
                harness.ArmForNextServer();
                var largeSend = Task.Run(() => harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.RunStopped,
                    SessionId = before.SessionId,
                    Reason = new string('y', 16 * 1024 * 1024)
                }));
                Thread.Sleep(25);
                harness.CloseCurrentConnection();
                Assert(largeSend.Wait(5000), "写失败任务未在有界时间内结束");
                Assert(WaitUntil(() =>
                {
                    var current = harness.Snapshot();
                    return current.ReconnectGeneration > before.ReconnectGeneration ||
                           current.ActiveConnectionGeneration > before.ActiveConnectionGeneration;
                }, 10000), "写失败没有进入唯一重连监督：" + harness.ServerStats);
                Assert(WaitUntil(() => harness.Snapshot().IsAttached, 10000),
                    "写失败后未重新Attach：" + harness.ServerStats);
                var after = harness.Snapshot();
                Assert(after.AuthorityProcessId == before.AuthorityProcessId &&
                       after.AuthorityProcessStartUtcTicks == before.AuthorityProcessStartUtcTicks &&
                       harness.TransportLostCallbackCount == 1,
                    "写失败未按精确connection单次报告TransportLost：" + harness.ServerStats +
                    ";callbacks=" + string.Join("|", harness.CallbackEntriesAfter(callbackBoundary)));
            }
        }

        private static void HeartbeatCaptureFailureDoesNotReconnect()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                const string label = "heartbeat-capture-failure";
                var throwNext = 0;
                var callbacks = harness.CreateCallbacks(harness.Options, label);
                var originalCapture = callbacks.CaptureHeartbeat;
                callbacks.CaptureHeartbeat = () =>
                {
                    if (Interlocked.Exchange(ref throwNext, 0) != 0)
                        throw new InvalidOperationException("test CaptureHeartbeat failure");
                    return originalCapture();
                };
                harness.Engine.BeginSession(harness.Options, callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000),
                    "CaptureHeartbeat失败前置Attach失败");
                var before = harness.Snapshot();
                var callbackBoundary = harness.MarkCallbackBoundary(label);
                Interlocked.Exchange(ref throwNext, 1);
                Assert(WaitUntil(() => harness.TransportErrorCount > 0, 4000),
                    "CaptureHeartbeat异常未进入诊断回调");
                Thread.Sleep(1200);
                var after = harness.Snapshot();
                Assert(after.IsAttached && !after.TransportFailClosed &&
                       after.ReconnectGeneration == before.ReconnectGeneration &&
                       harness.TransportLostCallbackCount == 0,
                    "CaptureHeartbeat一次失败错误触发了重连/TransportLost：" + harness.ServerStats);
                Assert(harness.Engine.Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.RunStopped,
                    SessionId = before.SessionId,
                    Reason = "capture-recovered"
                }), "CaptureHeartbeat失败后普通发送未恢复");
            }
        }

        private static void AckUsesExactConnectionAndMonotonicSequence()
        {
            using (var harness = TestHarness.Create(LaunchMode.Success))
            {
                harness.Engine.BeginSession(harness.Options, harness.Callbacks);
                Assert(harness.Engine.StartAsync().Wait(15000), "ACK测试首次连接失败");
                Assert(WaitUntil(() => harness.Snapshot().HeartbeatAckSequence > 0, 5000),
                    "ACK测试首次连接没有取得基础heartbeat ACK");
                harness.SetHeartbeatAckEnabled(false);
                Assert(harness.SendHeartbeatAck(100),
                    "ACK测试server没有通过真实NamedPipe发送当前ACK=100");
                Assert(WaitUntil(() => harness.Snapshot().HeartbeatAckSequence == 100, 3000),
                    "当前connection真实ACK=100没有更新public snapshot");
                var beforeReconnect = harness.Snapshot();

                // Keep a frame on the old server-side writer and release it
                // after the production reconnect has detached that connection.
                // The delayed write may fail at the pipe boundary, but it
                // must never change the new connection's public ACK snapshot.
                var delayedOldAck = harness.PrepareDelayedHeartbeatAck(200);
                harness.ArmForNextServer();
                harness.Engine.ScheduleReconnect(0);
                Assert(WaitUntil(
                           () =>
                           {
                               var snapshot = harness.Snapshot();
                               return snapshot.IsAttached && !snapshot.ReconnectActive &&
                                      snapshot.ActiveConnectionGeneration >
                                      beforeReconnect.ActiveConnectionGeneration;
                           },
                           15000),
                    "ACK测试public ScheduleReconnect没有建立新connection");
                var afterReconnect = harness.Snapshot();
                var newConnectionAckSequence = afterReconnect.HeartbeatAckSequence;
                var newConnectionAckTicks = afterReconnect.LastHeartbeatAckUtcTicks;
                // Release the old writer only after the public reconnect has
                // crossed its Attached barrier.  This makes the stale-frame
                // assertion independent of reconnect timing while still
                // exercising the real NamedPipe reader identity gate.
                harness.ReleaseDelayedHeartbeatAck();
                Assert(delayedOldAck.Wait(10000),
                    "旧connection delayed ACK没有在有界时间内完成server写尝试");
                var afterOldFrame = harness.Snapshot();
                Assert(afterOldFrame.HeartbeatAckSequence == newConnectionAckSequence &&
                       afterOldFrame.LastHeartbeatAckUtcTicks == newConnectionAckTicks,
                    "旧connection delayed ACK意外改变了新connection ACK快照：seq=" +
                    afterOldFrame.HeartbeatAckSequence +
                    ";connection=" + harness.Snapshot().ActiveConnectionGeneration +
                    ";delayedWrite=" + delayedOldAck.Result);
                Assert(harness.SendHeartbeatAck(99),
                    "当前connection没有通过真实NamedPipe发送倒退ACK=99");
                Assert(WaitUntil(() => harness.Snapshot().HeartbeatAckSequence == 99, 3000),
                    "当前connection首个ACK=99没有更新public snapshot");
                Assert(harness.SendHeartbeatAck(101),
                    "当前connection没有通过真实NamedPipe发送递增ACK=101");
                Assert(WaitUntil(() => harness.Snapshot().HeartbeatAckSequence == 101, 3000),
                    "当前connection递增ACK=101没有更新public snapshot");
                var after101 = harness.Snapshot();
                Assert(harness.SendHeartbeatAck(101),
                    "当前connection重复ACK=101没有通过真实NamedPipe发送");
                Assert(harness.SendHeartbeatAck(100),
                    "当前connection重复/倒退ACK=100没有通过真实NamedPipe发送");
                Thread.Sleep(100);
                var afterDuplicate = harness.Snapshot();
                Assert(afterDuplicate.HeartbeatAckSequence == after101.HeartbeatAckSequence &&
                       afterDuplicate.LastHeartbeatAckUtcTicks == after101.LastHeartbeatAckUtcTicks,
                    "重复/倒退真实ACK改变了当前ACK快照");
                harness.Engine.Shutdown();
            }
        }

        private static Task[] StartCallers(
            WatchdogClientTransportEngine engine,
            int count)
        {
            var tasks = new Task[count];
            for (var index = 0; index < count; index++)
                tasks[index] = engine.StartAsync();
            return tasks;
        }

        private static bool WaitAll(Task[] tasks, int timeoutMs)
        {
            try { return Task.WaitAll(tasks, timeoutMs); }
            catch (AggregateException) { return true; }
        }

        private static bool WaitFaulted(Task task, int timeoutMs)
        {
            if (task == null) return false;
            try
            {
                if (!task.Wait(timeoutMs)) return false;
                return task.IsFaulted || task.IsCanceled;
            }
            catch (AggregateException)
            {
                return true;
            }
        }

        private static bool WaitUntil(Func<bool> predicate, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (predicate()) return true;
                Thread.Sleep(25);
            }
            return predicate();
        }

        private static bool IsProcessAlive(int processId)
        {
            if (processId <= 0) return false;
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                var current = ex;
                while (current != null)
                {
                    if (current is TException) return;
                    current = current.InnerException;
                }
            }
            throw new InvalidOperationException(message);
        }

        private static Exception GetFailure(Task task)
        {
            if (task == null || task.Exception == null)
                return null;
            return task.Exception.Flatten().InnerExceptions.FirstOrDefault() ?? task.Exception;
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
                Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message);
                throw;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private enum LaunchMode
        {
            Success,
            Failure,
            Cancel,
            NonCooperativeCancel
        }

        private enum AttachedMutation
        {
            Valid,
            ProtocolV0,
            ProtocolV2,
            ProtocolV4,
            NoAttachedResponse,
            MissingSessionId,
            MissingAuthoritySessionId,
            MissingSidecarSessionId,
            WrongSessionId,
            WrongAuthoritySessionId,
            WrongSidecarSessionId,
            MissingPid,
            WrongPid,
            MissingProcessStart,
            MissingSidecarStart,
            MissingStart,
            WrongStart,
            MissingSidecarNonce,
            MissingNonce,
            WrongNonce
        }

        private sealed class TestHarness : IDisposable
        {
            internal readonly GatePipeFactory Factory;
            internal readonly ControlledLauncher Launcher;
            internal readonly WatchdogClientTransportEngine Engine;
            internal readonly WatchdogClientTransportOptions Options;
            internal readonly WatchdogClientTransportCallbacks Callbacks;
            private readonly PipeServer _server;
            private readonly string _journalDirectory;
            private readonly object _eventGate = new object();
            private readonly List<string> _events = new List<string>();
            private readonly List<string> _callbackEvents = new List<string>();
            private readonly List<WatchdogClientTransportEvent> _stateEvents =
                new List<WatchdogClientTransportEvent>();
            private int _transportErrorCount;
            private int _transportLostCallbackCount;

            private TestHarness(
                LaunchMode mode,
                AttachedMutation mutation = AttachedMutation.Valid)
            {
                _journalDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "MTTFTest.ClientEngine." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_journalDirectory);
                Factory = new GatePipeFactory();
                _server = new PipeServer(Factory, mutation);
                Launcher = new ControlledLauncher(mode, _server);
                Engine = new WatchdogClientTransportEngine(Launcher, Factory);
                Engine.StateChanged += notification =>
                {
                    lock (_eventGate) _stateEvents.Add(notification);
                };
                var session = Guid.NewGuid().ToString("N");
                Options = new WatchdogClientTransportOptions
                {
                    SessionId = session,
                    PipeName = "MTTFTest.ClientEngine." + session,
                    MainExecutablePath = CurrentExecutablePath(),
                    SidecarExecutablePath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    JournalDirectory = _journalDirectory,
                    SessionGeneration = 1,
                    RecoveryAttempt = 0,
                    SelectedChannels = new[] { 4 }
                };
                Callbacks = new WatchdogClientTransportCallbacks
                {
                    CreateRunSession = (recovery, attempt) => CreateRunSession(Options, recovery, attempt),
                    CaptureHeartbeat = () => new WatchdogHeartbeat
                    {
                        SessionId = Options.SessionId,
                        Phase = "Test"
                    },
                    RecordEvent = (eventType, detail) =>
                    {
                        lock (_eventGate) _events.Add(eventType + ":" + detail);
                    },
                    TransportError = (_, __) => Interlocked.Increment(ref _transportErrorCount),
                    TransportLost = (_, __) => { },
                    StopAllRequested = (_, __) => { },
                    ObserveDurableStopMarker = () => false
                };
            }

            internal static TestHarness Create(LaunchMode mode)
            {
                return new TestHarness(mode);
            }

            internal static TestHarness Create(
                LaunchMode mode,
                AttachedMutation mutation)
            {
                return new TestHarness(mode, mutation);
            }

            internal WatchdogClientTransportSnapshot Snapshot() => Engine.CaptureSnapshot();

            internal void ArmForNextServer() => _server.ArmForNextServer();

            internal void StopListening() => _server.StopListening();

            internal bool WaitForNextServer(int timeoutMs) => _server.WaitForNextServer(timeoutMs);

            internal int HeartbeatCount => _server.HeartbeatCount;

            internal DateTime[] HeartbeatTimesSnapshot() => _server.HeartbeatTimesSnapshot();

            internal void SetHeartbeatAckEnabled(bool enabled) => _server.SetHeartbeatAckEnabled(enabled);

            internal bool SendHeartbeatAck(long ackSequence) =>
                _server.SendHeartbeatAck(ackSequence);

            internal bool SendSessionRevokedMessage() =>
                _server.SendSessionRevokedMessage();

            internal void SetAttachedMutation(AttachedMutation mutation) =>
                _server.SetAttachedMutation(mutation);

            internal Task<bool> PrepareDelayedHeartbeatAck(long ackSequence) =>
                _server.PrepareDelayedHeartbeatAck(ackSequence);

            internal void ReleaseDelayedHeartbeatAck() =>
                _server.ReleaseDelayedHeartbeatAck();

            internal Task<bool> PrepareDelayedOldAttached() =>
                _server.PrepareDelayedOldAttached();

            internal void ReleaseDelayedOldAttached() =>
                _server.ReleaseDelayedOldAttached();

            internal void CloseCurrentConnection() => _server.CloseCurrentConnection();

            internal void BlockIncomingReads() => _server.BlockIncomingReads();

            internal Task ArmReadBlockAfterAttach() => _server.ArmReadBlockAfterAttach();

            internal void ReleaseReadBlock() => _server.ReleaseReadBlock();

            internal int CountReceivedReason(string reason) =>
                _server.CountReceivedReason(reason);

            internal string ServerStats
            {
                get
                {
                    lock (_eventGate)
                        return _server.Stats + ";events=" + string.Join("|", _events.ToArray());
                }
            }

            internal int TransportErrorCount => Volatile.Read(ref _transportErrorCount);

            internal int TransportLostCallbackCount =>
                Volatile.Read(ref _transportLostCallbackCount);

            internal WatchdogClientTransportCallbacks CreateCallbacks(
                WatchdogClientTransportOptions options,
                string label)
            {
                if (options == null) throw new ArgumentNullException(nameof(options));
                if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("callback label不能为空", nameof(label));
                return new WatchdogClientTransportCallbacks
                {
                    CreateRunSession = (recovery, attempt) => CreateRunSession(options, recovery, attempt),
                    CaptureHeartbeat = () => new WatchdogHeartbeat
                    {
                        SessionId = options.SessionId,
                        Phase = "Test"
                    },
                    RecordEvent = (eventType, detail) => RecordCallback(label, eventType, detail),
                    TransportError = (reason, detail) =>
                    {
                        Interlocked.Increment(ref _transportErrorCount);
                        RecordCallback(label, "TransportError", reason + ":" + detail);
                    },
                    TransportLost = (reason, detail) =>
                    {
                        Interlocked.Increment(ref _transportLostCallbackCount);
                        RecordCallback(label, "TransportLost", reason + ":" + detail);
                    },
                    StopAllRequested = (reason, correlation) =>
                        RecordCallback(label, "RequestStopAll", reason + ":" + correlation),
                    ObserveDurableStopMarker = () => false
                };
            }

            internal int MarkCallbackBoundary(string label)
            {
                lock (_eventGate)
                    return _callbackEvents.Count(value => value.StartsWith(label + "|", StringComparison.Ordinal));
            }

            internal int CountCallbacksAfter(string label, int boundary)
            {
                lock (_eventGate)
                {
                    return _callbackEvents
                        .Where(value => value.StartsWith(label + "|", StringComparison.Ordinal))
                        .Skip(boundary)
                        .Count();
                }
            }

            internal int CountCallbacksAfterContaining(
                string label,
                int boundary,
                string fragment)
            {
                lock (_eventGate)
                {
                    return _callbackEvents
                        .Skip(boundary)
                        .Count(value => value.StartsWith(label + "|", StringComparison.Ordinal) &&
                                        value.IndexOf(fragment ?? string.Empty,
                                            StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }

            internal string[] CallbackEntriesAfter(int boundary)
            {
                lock (_eventGate) return _callbackEvents.Skip(boundary).ToArray();
            }

            internal int MarkStateBoundary()
            {
                lock (_eventGate) return _stateEvents.Count;
            }

            internal int CountStateEventsForLeaseAfter(long sessionLease, int boundary)
            {
                lock (_eventGate)
                {
                    return _stateEvents
                        .Skip(boundary)
                        .Count(value => value?.Snapshot != null &&
                                        value.Snapshot.ActiveSessionLease == sessionLease);
                }
            }

            internal int CountStateEventsForLeaseAfter(
                long sessionLease,
                int boundary,
                string eventType)
            {
                lock (_eventGate)
                {
                    return _stateEvents
                        .Skip(boundary)
                        .Count(value => value?.Snapshot != null &&
                                        value.Snapshot.ActiveSessionLease == sessionLease &&
                                        string.Equals(
                                            value.EventType,
                                            eventType,
                                            StringComparison.Ordinal));
                }
            }

            internal WatchdogClientTransportEvent[] StateEventsAfter(int boundary)
            {
                lock (_eventGate)
                    return _stateEvents.Skip(boundary).ToArray();
            }

            private void RecordCallback(string label, string eventType, string detail)
            {
                lock (_eventGate)
                {
                    _callbackEvents.Add(
                        label + "|" + (eventType ?? string.Empty) + ":" + (detail ?? string.Empty));
                }
            }

            internal int CountEvents(string eventType)
            {
                lock (_eventGate)
                    return _events.Count(value => value.StartsWith(eventType + ":", StringComparison.Ordinal));
            }

            internal string[] EventsSnapshot()
            {
                lock (_eventGate) return _events.ToArray();
            }

            private static WatchdogRunSession CreateRunSession(
                WatchdogClientTransportOptions options,
                bool recovery,
                int attempt)
            {
                using (var process = Process.GetCurrentProcess())
                {
                    return new WatchdogRunSession
                    {
                        SessionId = options.SessionId,
                        PipeName = options.PipeName,
                        ExecutablePath = options.MainExecutablePath,
                        ProcessId = process.Id,
                        ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                        RecoveryProcess = recovery,
                        RecoveryAttempt = attempt,
                        SelectedChannels = options.SelectedChannels.ToArray()
                    };
                }
            }

            private static string CurrentExecutablePath()
            {
                using (var process = Process.GetCurrentProcess())
                    return process.MainModule?.FileName ?? AppDomain.CurrentDomain.BaseDirectory;
            }

            public void Dispose()
            {
                var failures = new List<Exception>();
                try { Engine.Shutdown(); }
                catch (Exception ex) { failures.Add(ex); }
                try { _server.Dispose(); }
                catch (Exception ex) { failures.Add(ex); }
                // Launcher.Dispose performs exact PID/start-time cleanup and
                // throws an AggregateException for any cleanup failure.  Do
                // not swallow that evidence at the harness boundary.
                try { Launcher.Dispose(); }
                catch (Exception ex) { failures.Add(ex); }
                try
                {
                    if (Directory.Exists(_journalDirectory))
                        Directory.Delete(_journalDirectory, true);
                }
                catch (Exception ex) { failures.Add(ex); }
                if (failures.Count > 0)
                    throw new AggregateException("TestHarness cleanup failed", failures);
            }
        }

        private sealed class GatePipeFactory : INamedPipeClientFactory
        {
            private int _enabled;
            private int _createCount;
            private int _rejectNextCreate;
            private TaskCompletionSource<bool> _createGate;
            private NamedPipeClientStream _blockedClient;
            private NamedPipeServerStream _blockedServer;
            private Task _blockedServerTask;
            private TaskCompletionSource<bool> _blockedServerRelease;
            private readonly List<DateTime> _createTimes = new List<DateTime>();

            internal int CreateCount => Volatile.Read(ref _createCount);

            internal DateTime[] CreateTimesSnapshot()
            {
                lock (_createTimes) return _createTimes.ToArray();
            }

            internal void Enable() => Volatile.Write(ref _enabled, 1);

            internal void Disable() => Volatile.Write(ref _enabled, 0);

            internal void BlockCreate()
            {
                Interlocked.Exchange(
                    ref _createGate,
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            internal void ReleaseCreate()
            {
                var gate = Interlocked.Exchange(ref _createGate, null);
                gate?.TrySetResult(true);
                var blockedClient = Interlocked.Exchange(ref _blockedClient, null);
                var blockedServer = Interlocked.Exchange(ref _blockedServer, null);
                var blockedServerRelease = Interlocked.Exchange(
                    ref _blockedServerRelease,
                    null);
                blockedServerRelease?.TrySetResult(true);
                try { blockedClient?.Dispose(); } catch { }
                try { blockedServer?.Dispose(); } catch { }
                var blockedServerTask = Interlocked.Exchange(ref _blockedServerTask, null);
                try { blockedServerTask?.Wait(1000); } catch { }
                // RejectNextCreate is a release instruction for the already
                // admitted stream, not a fault to carry into the next
                // session.  Consume it at the same boundary as the stream
                // close so a fresh BeginSession is unaffected.
                Interlocked.Exchange(ref _rejectNextCreate, 0);
            }

            internal void RejectNextCreate()
            {
                Volatile.Write(ref _rejectNextCreate, 1);
            }

            public NamedPipeClientStream Create(string pipeName)
            {
                Interlocked.Increment(ref _createCount);
                lock (_createTimes) _createTimes.Add(DateTime.UtcNow);
                var gate = Volatile.Read(ref _createGate);
                if (gate != null)
                {
                    // Keep the named test seam called BlockCreate, but let
                    // the production ConnectAsync reach its ConnectOwner
                    // reservation.  The returned stream is connected to a
                    // private fixture server which deliberately withholds
                    // Attached; ReleaseCreate closes that exact stream and
                    // server after public shutdown has detached the old
                    // session.  This makes the captured ConnectTask and
                    // reservation observable to ShutdownWithReceipt without
                    // blocking the synchronous factory call itself.
                    lock (this)
                    {
                        if (_blockedClient != null || _blockedServer != null)
                            throw new IOException("controlled concurrent pipe admission");
                    }
                    var blockedPipeName = pipeName + ".testblock." + Guid.NewGuid().ToString("N");
                    var blockedServer = new NamedPipeServerStream(
                        blockedPipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    var blockedRelease = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    var blockedClient = new NamedPipeClientStream(
                        ".",
                        blockedPipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous);
                    lock (this)
                    {
                        _blockedServer = blockedServer;
                        _blockedServerRelease = blockedRelease;
                        _blockedClient = blockedClient;
                        _blockedServerTask = Task.Run(async () =>
                        {
                            try
                            {
                                await blockedServer.WaitForConnectionAsync().ConfigureAwait(false);
                                await blockedRelease.Task.ConfigureAwait(false);
                            }
                            catch { }
                        });
                    }
                    return blockedClient;
                }
                if (Interlocked.Exchange(ref _rejectNextCreate, 0) != 0)
                    throw new IOException("controlled pipe unavailable after public shutdown boundary");
                if (Volatile.Read(ref _enabled) == 0)
                    throw new IOException("controlled pipe unavailable before launch");
                return new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
            }
        }

        private sealed class ControlledLauncher : ISidecarProcessLauncher, IDisposable
        {
            private sealed class ProcessIdentity
            {
                internal int ProcessId { get; }
                internal long StartUtcTicks { get; }

                internal ProcessIdentity(int processId, long startUtcTicks)
                {
                    ProcessId = processId;
                    StartUtcTicks = startUtcTicks;
                }

                internal string Display => ProcessId + "/" + StartUtcTicks;
            }

            private readonly LaunchMode _mode;
            private readonly PipeServer _server;
            private readonly TaskCompletionSource<bool> _failureRelease =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _nonCooperativeRelease =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Returned =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Keep only immutable identity data.  The Process object returned
            // by the production launcher remains owned by the engine/result;
            // this fixture never retains a shared Process reference.
            private readonly List<ProcessIdentity> _identities =
                new List<ProcessIdentity>();
            private readonly object _gate = new object();
            private int _launchCount;
            private int _lastProcessId;
            private long _lastProcessStartUtcTicks;

            internal readonly TaskCompletionSource<bool> Entered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal ControlledLauncher(LaunchMode mode, PipeServer server)
            {
                _mode = mode;
                _server = server;
            }

            internal int LaunchCount => Volatile.Read(ref _launchCount);

            internal int LastProcessId => Volatile.Read(ref _lastProcessId);

            internal long LastProcessStartUtcTicks =>
                Volatile.Read(ref _lastProcessStartUtcTicks);

            internal void ReleaseFailure() => _failureRelease.TrySetResult(true);

            internal void ReleaseNonCooperative() => _nonCooperativeRelease.TrySetResult(true);

            public async Task<SidecarProcessLaunchResult> LaunchAsync(
                SidecarProcessLaunchRequest request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _launchCount);
                Entered.TrySetResult(true);
                if (_mode == LaunchMode.Failure)
                {
                    await _failureRelease.Task.ConfigureAwait(false);
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.LaunchFailure,
                        "controlled leader launch failure");
                }
                if (_mode == LaunchMode.Cancel)
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                if (_mode == LaunchMode.NonCooperativeCancel)
                    await _nonCooperativeRelease.Task.ConfigureAwait(false);

                if (_mode != LaunchMode.NonCooperativeCancel)
                    cancellationToken.ThrowIfCancellationRequested();
                // Reuse the production launcher to obtain a fully-owned
                // SidecarProcessLaunchResult.  The test only controls the
                // pipe boundary; it does not construct or mutate production
                // launch-result internals.
                var launchCancellationToken = _mode == LaunchMode.NonCooperativeCancel
                    ? CancellationToken.None
                    : cancellationToken;
                var result = await new SystemSidecarProcessLauncher().LaunchAsync(
                    new SidecarProcessLaunchRequest
                    {
                        ExecutablePath = PingExecutablePath(),
                        Arguments = "127.0.0.1 -t",
                        WorkingDirectory = Environment.CurrentDirectory
                    },
                    launchCancellationToken).ConfigureAwait(false);
                var process = result?.Process;
                var owner = result?.Owner;
                if (result == null || process == null || owner == null)
                    throw new InvalidOperationException(
                        "controlled helper启动未返回完整Process/Owner结果");
                var identity = new ProcessIdentity(
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks);
                Volatile.Write(ref _lastProcessId, process.Id);
                Volatile.Write(ref _lastProcessStartUtcTicks, identity.StartUtcTicks);
                lock (_gate) _identities.Add(identity);
                var nonce = ParseNonce(request?.Arguments);
                var sidecarPid = process.Id;
                _server.Start(ParseArgument(request?.Arguments, "--pipe"), sidecarPid, identity.StartUtcTicks, nonce);
                Returned.TrySetResult(true);
                return result;
            }

            private static string PingExecutablePath()
            {
                var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
                if (string.IsNullOrWhiteSpace(systemRoot))
                    systemRoot = Environment.GetEnvironmentVariable("WINDIR");
                if (string.IsNullOrWhiteSpace(systemRoot))
                    throw new InvalidOperationException("SystemRoot/WINDIR未设置，无法定位PING.EXE");
                var path = Path.Combine(systemRoot, "System32", "PING.EXE");
                if (!File.Exists(path))
                    throw new FileNotFoundException("未找到系统PING.EXE", path);
                return path;
            }

            private static string ParseArgument(string arguments, string marker)
            {
                var index = (arguments ?? string.Empty).IndexOf(marker, StringComparison.Ordinal);
                if (index < 0) throw new InvalidOperationException("controlled launch缺少" + marker);
                var value = (arguments ?? string.Empty).Substring(index + marker.Length).TrimStart();
                if (value.Length == 0) throw new InvalidOperationException("controlled launch参数为空：" + marker);
                if (value[0] == '"')
                {
                    var end = value.IndexOf('"', 1);
                    if (end <= 1) throw new InvalidOperationException("controlled launch参数无效：" + marker);
                    return value.Substring(1, end - 1);
                }
                var space = value.IndexOf(' ');
                return space > 0 ? value.Substring(0, space) : value;
            }

            private static string ParseNonce(string arguments)
            {
                const string marker = "--sidecar-instance-nonce";
                var index = (arguments ?? string.Empty).IndexOf(marker, StringComparison.Ordinal);
                if (index < 0) throw new InvalidOperationException("controlled launch缺少nonce");
                var value = (arguments ?? string.Empty).Substring(index + marker.Length).Trim();
                if (value.Length >= 2 && value[0] == '"')
                {
                    var end = value.IndexOf('"', 1);
                    if (end > 1) value = value.Substring(1, end - 1);
                }
                return value;
            }

            public void Dispose()
            {
                ProcessIdentity[] identities;
                lock (_gate)
                {
                    identities = _identities.ToArray();
                    _identities.Clear();
                }

                var failures = new List<Exception>();
                var released = new List<string>();
                foreach (var identity in identities)
                {
                    try
                    {
                        using (var process = Process.GetProcessById(identity.ProcessId))
                        {
                            // Never kill a reused PID.  A different start
                            // time means the original exact identity has
                            // already exited and the current process is out
                            // of scope for this fixture.
                            if (!MatchesIdentity(process, identity))
                            {
                                released.Add(identity.Display + ":pid-reused-or-exited");
                                continue;
                            }
                            if (!process.HasExited)
                            {
                                process.Kill();
                                if (!process.WaitForExit(3000))
                                    throw new TimeoutException(
                                        "PING helper未在3s内退出：" + identity.Display);
                            }
                        }
                        released.Add(identity.Display);
                    }
                    catch (ArgumentException)
                    {
                        // GetProcessById reports an exited process this way.
                        released.Add(identity.Display + ":already-exited");
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new InvalidOperationException(
                            "精确回收PING helper失败：" + identity.Display,
                            ex));
                    }
                    if (IsExactProcessAlive(identity))
                    {
                        failures.Add(new InvalidOperationException(
                            "精确回收后PING helper仍存活：" + identity.Display));
                    }
                }
                Console.WriteLine(
                    "CONTROLLED_HELPER_RETIRED " +
                    (released.Count == 0 ? "<none>" : string.Join(",", released)));
                if (failures.Count > 0)
                    throw new AggregateException("ControlledLauncher exact cleanup failed", failures);
            }

            private static bool MatchesIdentity(Process process, ProcessIdentity identity)
            {
                if (process == null || process.Id != identity.ProcessId || process.HasExited)
                    return false;
                try
                {
                    return process.StartTime.ToUniversalTime().Ticks == identity.StartUtcTicks;
                }
                catch
                {
                    return false;
                }
            }

            private static bool IsExactProcessAlive(ProcessIdentity identity)
            {
                try
                {
                    using (var process = Process.GetProcessById(identity.ProcessId))
                        return MatchesIdentity(process, identity);
                }
                catch
                {
                    return false;
                }
            }
        }

        private sealed class PipeServer : IDisposable
        {
            private readonly GatePipeFactory _factory;
            private AttachedMutation _mutation;
            private readonly object _gate = new object();
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly ManualResetEventSlim _nextServer = new ManualResetEventSlim(false);
            private NamedPipeServerStream _current;
            private Task _loop;
            private int _serverGeneration;
            private int _armedGeneration;
            private string _pipeName;
            private string _sessionId;
            private int _sidecarProcessId;
            private long _sidecarStartUtcTicks;
            private string _nonce;
            private int _connectionCount;
            private int _attachCount;
            private int _attachedWriteCount;
            private readonly List<DateTime> _heartbeatTimes = new List<DateTime>();
            private readonly Dictionary<string, int> _receivedReasons =
                new Dictionary<string, int>(StringComparer.Ordinal);
            private int _heartbeatAckEnabled = 1;
            private int _serveErrorCount;
            private string _serveErrorDetail;
            private StreamWriter _activeWriter;
            private TaskCompletionSource<bool> _readGate;
            private int _blockAfterAttach;
            private TaskCompletionSource<bool> _readBlockEntered;
            private TaskCompletionSource<bool> _readBlockRelease;
            private TaskCompletionSource<bool> _delayedAckRelease;
            private TaskCompletionSource<bool> _delayedAttachedRelease;
            private readonly object _writerGate = new object();

            internal string Stats
            {
                get
                {
                    lock (_gate)
                    {
                        return "gen=" + _serverGeneration +
                               ";connections=" + _connectionCount +
                               ";attaches=" + _attachCount +
                               ";attachedWrites=" + _attachedWriteCount +
                               ";serveErrors=" + _serveErrorCount +
                               ";serveErrorDetail=" + (_serveErrorDetail ?? string.Empty) +
                               ";current=" + (_current != null);
                    }
                }
            }

            internal int HeartbeatCount
            {
                get
                {
                    lock (_gate) return _heartbeatTimes.Count;
                }
            }

            internal DateTime[] HeartbeatTimesSnapshot()
            {
                lock (_gate) return _heartbeatTimes.ToArray();
            }

            internal int CountReceivedReason(string reason)
            {
                lock (_gate)
                    return _receivedReasons.TryGetValue(reason ?? string.Empty, out var count)
                        ? count
                        : 0;
            }

            internal void SetHeartbeatAckEnabled(bool enabled)
            {
                Volatile.Write(ref _heartbeatAckEnabled, enabled ? 1 : 0);
            }

            internal bool SendHeartbeatAck(long ackSequence)
            {
                StreamWriter writer;
                string sessionId;
                lock (_gate)
                {
                    writer = _activeWriter;
                    sessionId = _sessionId;
                }
                return WriteMessage(writer, new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.HeartbeatAck,
                    SessionId = sessionId,
                    AckSequence = ackSequence
                });
            }

            internal bool SendSessionRevokedMessage()
            {
                StreamWriter writer;
                string sessionId;
                lock (_gate)
                {
                    writer = _activeWriter;
                    sessionId = _sessionId;
                }
                return WriteMessage(writer, new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Heartbeat,
                    SessionId = sessionId + "-revoked",
                    Heartbeat = new WatchdogHeartbeat
                    {
                        SessionId = sessionId + "-revoked",
                        Phase = "SessionRevokedFixture"
                    }
                });
            }

            internal void SetAttachedMutation(AttachedMutation mutation)
            {
                lock (_gate) _mutation = mutation;
            }

            internal Task<bool> PrepareDelayedHeartbeatAck(long ackSequence)
            {
                StreamWriter writer;
                string sessionId;
                var release = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    writer = _activeWriter;
                    sessionId = _sessionId;
                    _delayedAckRelease = release;
                }
                return Task.Run(async () =>
                {
                    try
                    {
                        await release.Task.ConfigureAwait(false);
                        return WriteMessage(writer, new WatchdogMessage
                        {
                            ProtocolVersion = WatchdogProtocol.Version,
                            Type = WatchdogMessageType.HeartbeatAck,
                            SessionId = sessionId,
                            AckSequence = ackSequence
                        });
                    }
                    catch { return false; }
                });
            }

            internal void ReleaseDelayedHeartbeatAck()
            {
                TaskCompletionSource<bool> release;
                lock (_gate)
                {
                    release = _delayedAckRelease;
                    _delayedAckRelease = null;
                }
                release?.TrySetResult(true);
            }

            internal Task<bool> PrepareDelayedOldAttached()
            {
                StreamWriter writer;
                string sessionId;
                TaskCompletionSource<bool> release;
                WatchdogMessage response;
                lock (_gate)
                {
                    writer = _activeWriter;
                    sessionId = _sessionId;
                    release = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _delayedAttachedRelease = release;
                    response = BuildAttachedResponse(new WatchdogMessage
                    {
                        ProtocolVersion = WatchdogProtocol.Version,
                        Type = WatchdogMessageType.Attach,
                        SessionId = sessionId
                    });
                }
                return Task.Run(async () =>
                {
                    try
                    {
                        await release.Task.ConfigureAwait(false);
                        return WriteMessage(writer, response);
                    }
                    catch { return false; }
                });
            }

            internal void ReleaseDelayedOldAttached()
            {
                TaskCompletionSource<bool> release;
                lock (_gate)
                {
                    release = _delayedAttachedRelease;
                    _delayedAttachedRelease = null;
                }
                release?.TrySetResult(true);
            }

            internal void BlockIncomingReads()
            {
                lock (_gate)
                {
                    _readGate = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            internal Task ArmReadBlockAfterAttach()
            {
                lock (_gate)
                {
                    _blockAfterAttach = 1;
                    _readBlockEntered = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _readBlockRelease = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    return _readBlockEntered.Task;
                }
            }

            internal void ReleaseReadBlock()
            {
                TaskCompletionSource<bool> release;
                lock (_gate)
                {
                    _blockAfterAttach = 0;
                    release = _readBlockRelease;
                    _readBlockRelease = null;
                    _readBlockEntered = null;
                }
                release?.TrySetResult(true);
            }

            internal void CloseCurrentConnection()
            {
                NamedPipeServerStream server;
                TaskCompletionSource<bool> readGate;
                TaskCompletionSource<bool> readBlockRelease;
                lock (_gate)
                {
                    server = _current;
                    readGate = _readGate;
                    _readGate = null;
                    readBlockRelease = _readBlockRelease;
                    _readBlockRelease = null;
                    _readBlockEntered = null;
                    _blockAfterAttach = 0;
                }
                readGate?.TrySetResult(true);
                readBlockRelease?.TrySetResult(true);
                try { server?.Dispose(); } catch { }
            }

            internal PipeServer(
                GatePipeFactory factory,
                AttachedMutation mutation)
            {
                _factory = factory;
                _mutation = mutation;
            }

            internal void Start(
                string pipeName,
                int sidecarProcessId,
                long sidecarStartUtcTicks,
                string nonce)
            {
                lock (_gate)
                {
                    if (_loop != null) return;
                    _pipeName = pipeName;
                    _sessionId = ExtractSession(pipeName);
                    _sidecarProcessId = sidecarProcessId;
                    _sidecarStartUtcTicks = sidecarStartUtcTicks;
                    _nonce = nonce;
                    _factory.Enable();
                    _current = CreateServer();
                    _serverGeneration = 1;
                    _nextServer.Set();
                    _loop = Task.Run(RunLoopAsync);
                }
            }

            internal bool WaitForNextServer(int timeoutMs)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                int previousGeneration;
                lock (_gate)
                {
                    previousGeneration = _armedGeneration > 0
                        ? _armedGeneration
                        : _serverGeneration;
                    _armedGeneration = 0;
                }
                while (true)
                {
                    lock (_gate)
                    {
                        if (_serverGeneration > previousGeneration) return true;
                        // Reset while holding the same gate used by RunLoopAsync
                        // to publish the next generation.  This avoids losing
                        // the signal when a closed connection is processed
                        // between the caller's close and this wait.
                        _nextServer.Reset();
                    }
                    var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remaining <= 0 || !_nextServer.Wait(remaining)) return false;
                    lock (_gate)
                    {
                        if (_serverGeneration > previousGeneration) return true;
                    }
                }
            }

            internal void ArmForNextServer()
            {
                lock (_gate)
                {
                    _armedGeneration = _serverGeneration;
                    _nextServer.Reset();
                }
            }

            internal void StopListening()
            {
                _stop.Cancel();
                NamedPipeServerStream server;
                TaskCompletionSource<bool> readGate;
                TaskCompletionSource<bool> readBlockRelease;
                lock (_gate)
                {
                    server = _current;
                    readGate = _readGate;
                    _readGate = null;
                    readBlockRelease = _readBlockRelease;
                    _readBlockRelease = null;
                    _readBlockEntered = null;
                    _blockAfterAttach = 0;
                    _activeWriter = null;
                    _delayedAckRelease?.TrySetResult(false);
                    _delayedAckRelease = null;
                    _delayedAttachedRelease?.TrySetResult(false);
                    _delayedAttachedRelease = null;
                }
                readGate?.TrySetResult(true);
                readBlockRelease?.TrySetResult(true);
                try { server?.Dispose(); } catch { }
                try { _loop?.Wait(3000); } catch { }
                lock (_gate)
                {
                    if (ReferenceEquals(_current, server)) _current = null;
                }
            }

            private async Task RunLoopAsync()
            {
                while (!_stop.IsCancellationRequested)
                {
                    NamedPipeServerStream server;
                    lock (_gate) server = _current;
                    if (server == null) return;
                    try
                    {
                        await server.WaitForConnectionAsync().ConfigureAwait(false);
                        await ServeConnectionAsync(server).ConfigureAwait(false);
                    }
                    catch { }
                    finally
                    {
                        try { server.Dispose(); } catch { }
                        lock (_gate)
                        {
                            if (ReferenceEquals(_current, server)) _current = null;
                        }
                    }
                    if (_stop.IsCancellationRequested) return;
                    lock (_gate)
                    {
                        if (_current == null)
                        {
                            _current = CreateServer();
                            _serverGeneration++;
                            _nextServer.Set();
                        }
                    }
                }
            }

            private async Task ServeConnectionAsync(NamedPipeServerStream server)
            {
                lock (_gate) _connectionCount++;
                StreamWriter activeWriter = null;
                var firstPostAttachRead = true;
                try
                {
                    using (var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, true))
                    using (var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true)
                    {
                        AutoFlush = true
                    })
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        var attach = WatchdogProtocol.Deserialize(line);
                        if (attach == null || attach.Type != WatchdogMessageType.Attach)
                            return;
                        lock (_gate) _attachCount++;
                        try
                        {
                            var attachedResponse = BuildAttachedResponse(attach);
                            if (attachedResponse != null)
                            {
                                if (!WriteMessage(writer, attachedResponse))
                                    throw new IOException("Attached响应写入失败。");
                                lock (_gate) _attachedWriteCount++;
                        }
                        lock (_gate) _activeWriter = writer;
                        activeWriter = writer;
                    }
                        catch (Exception ex)
                        {
                            lock (_gate)
                            {
                                _serveErrorCount++;
                                _serveErrorDetail = ex.GetBaseException().Message;
                            }
                            throw;
                        }
                        while (!_stop.IsCancellationRequested)
                        {
                            Task readGateTask;
                            lock (_gate) readGateTask = _readGate?.Task;
                            if (readGateTask != null)
                                await readGateTask.ConfigureAwait(false);
                            line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (line == null) return;
                            var message = WatchdogProtocol.Deserialize(line);
                            if (message != null && !string.IsNullOrEmpty(message.Reason))
                            {
                                lock (_gate)
                                {
                                    _receivedReasons.TryGetValue(
                                        message.Reason,
                                        out var receivedCount);
                                    _receivedReasons[message.Reason] = receivedCount + 1;
                                }
                            }
                            if (message?.Type == WatchdogMessageType.Heartbeat)
                            {
                                lock (_gate) _heartbeatTimes.Add(DateTime.UtcNow);
                                if (Volatile.Read(ref _heartbeatAckEnabled) != 0)
                                {
                                    WriteMessage(writer, new WatchdogMessage
                                    {
                                        ProtocolVersion = WatchdogProtocol.Version,
                                        Type = WatchdogMessageType.HeartbeatAck,
                                        SessionId = attach.SessionId,
                                        AckSequence = message.Heartbeat?.Sequence ?? 0
                                    });
                                }
                            }
                            else if (message?.Type == WatchdogMessageType.ApplicationClosing)
                            {
                                return;
                            }

                            // Consume and ACK the mandatory startup heartbeat
                            // before blocking the next read.  This keeps the
                            // SendBusy barrier independent from Attach startup.
                            if (firstPostAttachRead)
                            {
                                firstPostAttachRead = false;
                                TaskCompletionSource<bool> blockEntered;
                                Task readBlock;
                                lock (_gate)
                                {
                                    blockEntered = _blockAfterAttach != 0 ? _readBlockEntered : null;
                                    readBlock = _blockAfterAttach != 0 ? _readBlockRelease?.Task : null;
                                }
                                if (readBlock != null)
                                {
                                    blockEntered?.TrySetResult(true);
                                    await readBlock.ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activeWriter, activeWriter))
                            _activeWriter = null;
                    }
                }
            }

            private bool WriteMessage(StreamWriter writer, WatchdogMessage message)
            {
                if (writer == null || message == null) return false;
                try
                {
                    lock (_writerGate)
                        writer.WriteLine(WatchdogProtocol.Serialize(message));
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private WatchdogMessage BuildAttachedResponse(WatchdogMessage attach)
            {
                AttachedMutation mutation;
                lock (_gate) mutation = _mutation;
                if (mutation == AttachedMutation.NoAttachedResponse)
                    return null;
                var response = new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.Attached,
                    SessionId = attach.SessionId,
                    SidecarProcessId = _sidecarProcessId,
                    SidecarProcessStartUtcTicks = _sidecarStartUtcTicks,
                    SidecarStartUtcTicks = _sidecarStartUtcTicks,
                    StartUtcTicks = _sidecarStartUtcTicks,
                    SidecarAuthoritySessionId = attach.SessionId,
                    SidecarSessionId = attach.SessionId,
                    SidecarInstanceNonce = _nonce,
                    InstanceNonce = _nonce
                };
                switch (mutation)
                {
                    case AttachedMutation.ProtocolV0:
                        response.ProtocolVersion = 0;
                        break;
                    case AttachedMutation.ProtocolV2:
                        response.ProtocolVersion = 2;
                        break;
                    case AttachedMutation.ProtocolV4:
                        response.ProtocolVersion = 4;
                        break;
                    case AttachedMutation.MissingSessionId:
                        response.SessionId = null;
                        break;
                    case AttachedMutation.MissingAuthoritySessionId:
                        response.SidecarAuthoritySessionId = null;
                        break;
                    case AttachedMutation.MissingSidecarSessionId:
                        response.SidecarSessionId = null;
                        break;
                    case AttachedMutation.WrongSessionId:
                        response.SessionId = attach.SessionId + "-wrong";
                        break;
                    case AttachedMutation.WrongAuthoritySessionId:
                        response.SidecarAuthoritySessionId = attach.SessionId + "-wrong";
                        break;
                    case AttachedMutation.WrongSidecarSessionId:
                        response.SidecarSessionId = attach.SessionId + "-wrong";
                        break;
                    case AttachedMutation.MissingPid:
                        response.SidecarProcessId = 0;
                        break;
                    case AttachedMutation.WrongPid:
                        response.SidecarProcessId = Process.GetCurrentProcess().Id;
                        break;
                    case AttachedMutation.MissingProcessStart:
                        response.SidecarProcessStartUtcTicks = 0;
                        break;
                    case AttachedMutation.MissingSidecarStart:
                        response.SidecarStartUtcTicks = 0;
                        break;
                    case AttachedMutation.MissingStart:
                        response.StartUtcTicks = 0;
                        break;
                    case AttachedMutation.WrongStart:
                        response.StartUtcTicks = _sidecarStartUtcTicks + 1;
                        break;
                    case AttachedMutation.MissingSidecarNonce:
                        response.SidecarInstanceNonce = null;
                        break;
                    case AttachedMutation.MissingNonce:
                        response.SidecarInstanceNonce = null;
                        response.InstanceNonce = null;
                        break;
                    case AttachedMutation.WrongNonce:
                        response.InstanceNonce = Guid.NewGuid().ToString("N");
                        break;
                }
                return response;
            }

            private NamedPipeServerStream CreateServer()
            {
                return new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }

            private string ExtractSession(string pipeName)
            {
                var marker = "MTTFTest.ClientEngine.";
                var index = (pipeName ?? string.Empty).IndexOf(marker, StringComparison.Ordinal);
                return index >= 0 ? pipeName.Substring(index + marker.Length) : pipeName;
            }

            public void Dispose()
            {
                StopListening();
                _nextServer.Dispose();
                _stop.Dispose();
            }
        }
    }
}
