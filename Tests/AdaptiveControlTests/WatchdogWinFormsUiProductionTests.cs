using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Controller;
using MTEmbTest;
using MtEmbTest;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Production WinForms callback seam.  The tests use the real STA message
    /// pump and WinFormsWatchdogPostTarget, then drive the same runtime
    /// BindTarget/RegisterHandler/Attach path used by the application.
    /// No fake post target or private reflection is used here.
    /// </summary>
    internal static class WatchdogWinFormsUiProductionTests
    {
        private const int TimeoutMilliseconds = 10000;

        internal static int RunAll()
        {
            var tests = new Action[]
            {
                RuntimeDependencyClosureIsComplete,
                WatchdogTerminalAuthorizationOverridesOnlyLegacyMdiGuard,
                ApplicationExitUsesDedicatedShutdownExpectedMessage,
                WinFormsTargetPostsOnStaMessagePump,
                AcceptedPostSurvivesDispose,
                BeginInvokeFaultIsReturned,
                ClosedTargetRejectsNewAdmission,
                PipelinePreattachPipeDurableMergeBindsOnce,
                PipelineHandlerFaultIsObservable,
                ConcurrentOperatorCloseJoinsAcceptedHandler,
                NonTerminalPipelineRetainsTargetUntilTerminalCleanup,
                ProductionAttachBeforeBindBecomesReady,
                ProductionNormalEntryBindsExactIdentity,
                ProductionRecoveryEntryDoesNotCreateSecondTarget,
                ProductionStopEnvelopePreservesCorrelation,
                ProductionDisconnectedCloseKeepsTargetAlive,
                ProductionNonTerminalRetryThenTerminalReleasesOnce,
                ProductionReceiptCarriesCompleteIdentity,
                ProductionStopHandlerUsesSharedProductionCore,
                ProductionGlobalEngineExactBindingAndReplacement,
                ProductionMainResourceOwnerReleasesTerminalOnce,
                ProductionMainFormDelegatesResourceReleaseExactlyOnce,
                ProductionMainAdapterDisposeRacesBindingCompletion,
                ProductionMainNormalStopReleasesAndRebindsSameMain,
                ProductionEntryPolicyBlocksBatchStartBeforePort,
                ProductionStaleMonitorLeaseAndRejectedBranchesCloseSafely
            };

            var passed = 0;
            foreach (var test in tests)
            {
                try
                {
                    Console.WriteLine("RUN Watchdog WinForms UI: " + test.Method.Name);
                    test();
                    passed++;
                    Console.WriteLine("PASS Watchdog WinForms UI: " + test.Method.Name);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Watchdog WinForms UI production case failed: " + test.Method.Name,
                        ex);
                }
            }
            return passed;
        }

        private static void RuntimeDependencyClosureIsComplete()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var required = new[]
            {
                "MTTFTest.exe",
                "MTTFTest.Watchdog.exe",
                "MTTFTest.Watchdog.Client.dll",
                "MTTFTest.Watchdog.Protocol.dll",
                "DataOperation.dll",
                "Config.dll",
                "DevExpress.Mvvm.v24.2.dll",
                "DevExpress.Office.v24.2.Core.dll",
                "DevExpress.RichEdit.v24.2.Core.dll",
                "DevExpress.XtraGrid.v24.2.dll",
                "DevExpress.XtraLayout.v24.2.dll",
                "DevExpress.XtraPrinting.v24.2.dll",
                "DevExpress.XtraTreeList.v24.2.dll"
            };
            var missing = required
                .Where(name => !File.Exists(Path.Combine(baseDirectory, name)))
                .ToArray();
            Assert(missing.Length == 0,
                "CopyMttfTestRuntimeClosure produced an incomplete Release dependency closure: " +
                string.Join(", ", missing));
        }

        private static void WatchdogTerminalAuthorizationOverridesOnlyLegacyMdiGuard()
        {
            Assert(WinFormsWatchdogUiCloseCoordinator.ShouldCoordinateMainClose(
                       hasActiveEvidence: false,
                       mdiChildCount: 1) &&
                   !WinFormsWatchdogUiCloseCoordinator.ShouldCoordinateMainClose(
                       hasActiveEvidence: false,
                       mdiChildCount: 0),
                "人工停止释放Watchdog后，主窗X没有继续收口仍存活的MDI子窗");
            Assert(!WinFormsWatchdogUiCloseCoordinator.ShouldApplyLegacyMdiGuard(
                       watchdogCloseAuthorized: true,
                       mdiChildCount: 1),
                "Watchdog终态退出授权仍被旧MDI子窗保护否决");
            Assert(WinFormsWatchdogUiCloseCoordinator.ShouldApplyLegacyMdiGuard(
                       watchdogCloseAuthorized: false,
                       mdiChildCount: 1) &&
                   !WinFormsWatchdogUiCloseCoordinator.ShouldApplyLegacyMdiGuard(
                       watchdogCloseAuthorized: false,
                       mdiChildCount: 0),
                "普通操作员误关保护被放宽，或无子窗仍错误阻止退出");
        }

        private static void ApplicationExitUsesDedicatedShutdownExpectedMessage()
        {
            Assert(WatchdogRuntime.SelectShutdownMessageTypeForRetention(false) ==
                   WatchdogMessageType.ApplicationClosing,
                "普通会话关闭被错误升级为主进程退出监督");
            Assert(WatchdogRuntime.SelectShutdownMessageTypeForRetention(true) ==
                   WatchdogMessageType.ShutdownExpected,
                "正式应用退出没有启用Sidecar主PID退出监督");
            Assert(WatchdogRuntime.SelectShutdownMessageTypeForRetention(
                       RuntimeShutdownIntent.WatchdogTakeoverExit) ==
                   WatchdogMessageType.WatchdogTakeoverExit,
                "Watchdog接管退出仍被错误归类为人工ShutdownExpected");
            Assert(!WatchdogLifecyclePolicy.IsTerminalMessage(
                       WatchdogMessageType.WatchdogTakeoverExit),
                "Watchdog接管退出错误撤销了仍待消费的自动恢复许可");
        }

        private static void WinFormsTargetPostsOnStaMessagePump()
        {
            using (var sta = new StaFormHost())
            using (var target = new WinFormsWatchdogPostTarget(sta.Control))
            {
                var invoked = 0;
                var receipt = target.TryPost(() =>
                {
                    sta.MarkCallbackSta();
                    Interlocked.Increment(ref invoked);
                    return Task.CompletedTask;
                });
                Assert(receipt.Accepted, "STA target did not accept BeginInvoke admission.");
                Wait(receipt.Completion, TimeoutMilliseconds);
                Assert(invoked == 1, "Accepted WinForms callback was not invoked exactly once.");
                Assert(sta.IsStaThread, "WinForms callback was not executed on an STA thread.");
            }
        }

        private static void AcceptedPostSurvivesDispose()
        {
            using (var sta = new StaFormHost())
            using (var target = new WinFormsWatchdogPostTarget(sta.Control))
            {
                var started = new ManualResetEventSlim(false);
                var release = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var accepted = target.TryPost(async () =>
                {
                    started.Set();
                    await release.Task.ConfigureAwait(true);
                });
                Assert(accepted.Accepted, "Slow callback was not admitted.");
                Assert(started.Wait(TimeoutMilliseconds), "Slow callback never reached the UI pump.");
                target.Dispose();
                var rejected = target.TryPost(() => Task.CompletedTask);
                Assert(!rejected.Accepted, "Disposed target admitted a new callback.");
                release.TrySetResult(true);
                Wait(accepted.Completion, TimeoutMilliseconds);
            }
        }

        private static void BeginInvokeFaultIsReturned()
        {
            using (var sta = new StaFormHost())
            using (var target = new WinFormsWatchdogPostTarget(sta.Control))
            {
                var receipt = target.TryPost(() =>
                    Task.FromException(new InvalidOperationException("ui-handler-fault")));
                Assert(receipt.Accepted, "Faulting callback was not admitted.");
                try
                {
                    receipt.Completion.GetAwaiter().GetResult();
                    throw new InvalidOperationException("Faulting callback completed successfully.");
                }
                catch (InvalidOperationException ex)
                {
                    Assert(ex.Message == "ui-handler-fault",
                        "WinForms target did not preserve handler exception: " + ex.Message);
                }
            }
        }

        private static void ClosedTargetRejectsNewAdmission()
        {
            using (var sta = new StaFormHost())
            using (var target = new WinFormsWatchdogPostTarget(sta.Control))
            {
                target.Dispose();
                var receipt = target.TryPost(() => Task.CompletedTask);
                Assert(!receipt.Accepted && receipt.Reason == "TargetDisposed",
                    "Closed target did not produce the typed rejection.");
            }
        }

        private static void PipelinePreattachPipeDurableMergeBindsOnce()
        {
            using (var sta = new StaFormHost())
            using (var target = new WinFormsWatchdogPostTarget(sta.Control))
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                var observed = new List<WatchdogStopAllOfferEnvelope>();
                var observedGate = new object();
                var observedSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var targetLease = session.BindTarget("Main_Frm.WatchdogSafety", target);
                Assert(targetLease != null && targetLease.Accepted,
                    "Production BindTarget did not accept the real WinForms target.");
                var handler = session.RegisterHandler(targetLease, "FrmEpbMainMonitor.WatchdogSafety",
                    envelope =>
                    {
                        lock (observedGate) observed.Add(envelope);
                        observedSignal.TrySetResult(true);
                        return Task.CompletedTask;
                    });
                Assert(handler != null && handler.Accepted,
                    "Production RegisterHandler did not reserve the UI safety handler.");

                Assert(session.PublishPipe("operator-stop", "ui-correlation", "ui-request"),
                    "Pre-attach Pipe source was not accepted into the runtime outbox.");
                Assert(session.PublishDurable("operator-stop", "ui-correlation", "ui-request"),
                    "Pre-attach Durable source was not accepted into the runtime outbox.");
                Assert(session.Attach(), "Production callback pipeline did not reach Ready.");
                Wait(observedSignal.Task, TimeoutMilliseconds);
                WaitUntil(() =>
                {
                    var snapshot = session.CaptureCoordinator();
                    return snapshot != null && snapshot.Registrations.Any(item => item.CompletionTerminal);
                }, TimeoutMilliseconds);

                var coordinator = session.CaptureCoordinator();
                Assert(coordinator != null && coordinator.Active,
                    "Coordinator was not active after exact Attach.");
                Assert(coordinator.CanonicalRequests.Count == 1,
                    "Pipe/Durable merge created more than one canonical request.");
                var canonical = coordinator.CanonicalRequests[0];
                Assert(canonical.SourceFlags == (WatchdogStopAllSourceFlags.Pipe | WatchdogStopAllSourceFlags.Durable),
                    "Canonical request did not retain both Pipe and Durable evidence.");
                Assert(canonical.CorrelationId == "ui-correlation" && canonical.RequestId == "ui-request",
                    "Canonical correlation/request identity changed during merge.");
                Assert(coordinator.Registrations.Count == 1,
                    "Preattach merge registered more than one safety delivery.");
                lock (observedGate)
                    Assert(observed.Count >= 1, "Merged request did not reach the UI handler.");
                handler.Dispose();
                var close = session.Close();
                Assert(close != null, "Pipeline close returned no receipt.");
            }
        }

        private static void PipelineHandlerFaultIsObservable()
        {
            using (var sta = new StaFormHost())
            using (var target = new WinFormsWatchdogPostTarget(sta.Control))
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                var targetLease = session.BindTarget("Main_Frm.WatchdogSafety", target);
                Assert(targetLease != null && targetLease.Accepted, "Safety target bind failed.");
                var handler = session.RegisterHandler(targetLease, "FaultingUiHandler",
                    envelope => Task.FromException(new ApplicationException("ui-fault")));
                Assert(handler != null && handler.Accepted, "Faulting handler reserve failed.");
                Assert(session.Attach(), "Faulting pipeline did not attach.");
                Assert(session.PublishPipe("fault-stop", "fault-correlation", "fault-request"),
                    "Faulting StopAll request was not admitted.");
                WaitUntil(() =>
                {
                    var snapshot = session.CaptureCoordinator();
                    return snapshot != null && snapshot.Registrations.Any(item => item.CompletionTerminal);
                }, TimeoutMilliseconds);
                var registration = session.CaptureCoordinator().Registrations.Single();
                Assert(registration.DeliveryState == WatchdogStopAllRegistrationState.HandlerFaulted,
                    "Handler fault was not represented as HandlerFaulted delivery state.");
                Assert(!string.IsNullOrWhiteSpace(registration.FailureReason),
                    "Handler fault reason was not retained; state=" + registration.DeliveryState);
                handler.Dispose();
                session.Close();
            }
        }

        private static void ConcurrentOperatorCloseJoinsAcceptedHandler()
        {
            using (var sta = new StaFormHost())
            using (var target = new WinFormsWatchdogPostTarget(sta.Control))
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                var started = new ManualResetEventSlim(false);
                var release = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var targetLease = session.BindTarget("Main_Frm.WatchdogSafety", target);
                Assert(targetLease != null && targetLease.Accepted, "Safety target bind failed.");
                var handler = session.RegisterHandler(targetLease, "OperatorStopHandler",
                    async envelope =>
                    {
                        started.Set();
                        await release.Task.ConfigureAwait(false);
                    });
                Assert(handler != null && handler.Accepted, "Operator handler reserve failed.");
                Assert(session.Attach(), "Operator-stop pipeline did not attach.");
                Assert(session.PublishPipe("operator-stop", "operator-correlation", "operator-request"),
                    "Operator-stop request was not admitted.");
                Assert(started.Wait(TimeoutMilliseconds), "Operator-stop handler did not start.");

                var closeTask = Task.Run(() => session.Close());
                Thread.Sleep(25);
                Assert(!closeTask.IsCompleted, "Close completed before accepted handler was released.");
                release.TrySetResult(true);
                Assert(closeTask.Wait(TimeoutMilliseconds), "Concurrent close did not join accepted handler.");
                var close = closeTask.Result;
                Assert(close != null && close.IsTerminal && close.WorkerResourcesReleased,
                    "Concurrent close did not produce a terminal resource receipt.");
                Assert(!target.IsDisposed,
                    "Non-owned UI target was disposed by pipeline close.");
                handler.Dispose();
            }
        }

        private static void NonTerminalPipelineRetainsTargetUntilTerminalCleanup()
        {
            using (var sta = new StaFormHost())
            using (var target = new WinFormsWatchdogPostTarget(sta.Control))
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                var targetLease = session.BindTarget("Main_Frm.WatchdogSafety", target);
                Assert(targetLease != null && targetLease.Accepted, "Safety target bind failed.");
                var handler = session.RegisterHandler(targetLease, "RetainedSafetyHandler",
                    envelope => Task.CompletedTask);
                Assert(handler != null && handler.Accepted, "Retained safety handler reserve failed.");
                Assert(session.Attach(), "Retained pipeline attach failed.");
                // Remove the only subscriber after the exact Ready transition;
                // the production coordinator must retain the observed request
                // as nonterminal rather than inventing a successful safety
                // outcome.
                handler.Dispose();
                Assert(session.PublishPipe("retained-stop", "retained-correlation", "retained-request"),
                    "Pre-attach retained request was not observed.");
                var receipt = session.Close();
                Assert(receipt != null && !receipt.IsTerminal,
                    "Missing safety subscriber incorrectly became terminal.");
                Assert(receipt.TerminalReason.IndexOf("Safety", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       receipt.TerminalReason.IndexOf("Subscriber", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       receipt.TerminalReason.IndexOf("Pending", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Nonterminal receipt did not retain a safety evidence reason.");
                Assert(!target.IsDisposed,
                    "Nonterminal retained pipeline disposed the Main-owned target.");
                var second = session.Close();
                Assert(ReferenceEquals(receipt, second) ||
                       (second != null && second.RetentionVersion >= receipt.RetentionVersion),
                    "Nonterminal close did not remain idempotent/retained.");
            }
        }

        private static void ProductionAttachBeforeBindBecomesReady()
        {
            using (var sta = new StaFormHost())
            using (var session = new WinFormsWatchdogUiProductionSession(sta.Control))
            {
                Assert(session.PublishValidatedAttachedBeforeBind(),
                    "Production transport identity was not accepted before UI binding.");
                Assert(session.Context.PipelineState == RuntimeCallbackPipelineState.Unbound,
                    "Attach-before-bind unexpectedly created a callback pipeline.");
                var callback = new ProductionHandler();
                var receipt = session.BindReadyAsync(
                        "Main_Frm.WatchdogSafety",
                        "FrmEpbMainMonitor.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(receipt != null && receipt.Accepted && receipt.Ready,
                    "Attach-before-bind shared transaction did not reach Ready.");
                Assert(session.Context.PipelineState == RuntimeCallbackPipelineState.Ready &&
                       receipt.HandlerLease.IsBound,
                    "Attach-before-bind did not publish a Ready binding.");
                receipt.HandlerLease.Dispose();
                session.Close();
            }
        }

        private static void ProductionNormalEntryBindsExactIdentity()
        {
            using (var sta = new StaFormHost())
            using (var session = new WinFormsWatchdogUiProductionSession(sta.Control))
            {
                var callback = new ProductionHandler();
                var receipt = session.BindReadyAsync(
                        "Main_Frm.Normal.WatchdogSafety",
                        "FrmEpbMainMonitor.Normal.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(receipt != null && receipt.Accepted && receipt.Ready,
                    "Production normal attach/bind path did not complete.");
                var identity = session.Context.ValidatedAttachIdentity;
                Assert(identity != null && identity.ExactKey.Split('|').Length >= 9,
                    "Normal binding did not freeze the complete exact identity key.");
                Assert(session.Context.PipelineState == RuntimeCallbackPipelineState.Ready,
                    "Normal entry did not become Ready.");
                Assert(receipt.ExactKey == identity.ExactKey &&
                       receipt.SessionId == identity.SessionId &&
                       receipt.SessionGeneration == identity.SessionGeneration &&
                       receipt.SessionLease == identity.SessionLease &&
                       receipt.AuthorityGeneration == identity.AuthorityGeneration &&
                       receipt.AttachedConnectionGeneration == identity.AttachedConnectionGeneration &&
                       receipt.AttachEpoch == identity.AttachEpoch &&
                       receipt.AuthorityProcessId == identity.AuthorityProcessId &&
                       receipt.AuthorityProcessStartUtcTicks == identity.AuthorityProcessStartUtcTicks &&
                       receipt.AuthorityInstanceNonceHash == identity.AuthorityInstanceNonceHash,
                    "Binding receipt did not carry the complete frozen ExactKey.");
                receipt.HandlerLease.Dispose();
                session.Close();
            }
        }

        private static void ProductionRecoveryEntryDoesNotCreateSecondTarget()
        {
            using (var sta = new StaFormHost())
            using (var session = new WinFormsWatchdogUiProductionSession(sta.Control))
            {
                var callback = new ProductionHandler();
                var receipt = session.BindReadyAsync(
                        "Main_Frm.Recovery.WatchdogSafety",
                        "FrmEpbMainMonitor.Recovery.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                var second = WatchdogRuntime.BindStopAllSafetyTarget(
                    session.Context, "Main_Frm.Recovery.WatchdogSafety", session.Target, false);
                Assert(receipt != null && receipt.Accepted && receipt.Ready &&
                       receipt.TargetLease != null && ReferenceEquals(receipt.TargetLease, second),
                    "Recovery attach path did not reuse the one Main-owned target lease.");
                Assert(session.Context.PipelineState == RuntimeCallbackPipelineState.Ready,
                    "Recovery attach-only path was not Ready.");
                receipt.HandlerLease.Dispose();
                session.Close();
            }
        }

        private static void ProductionStopEnvelopePreservesCorrelation()
        {
            using (var sta = new StaFormHost())
            using (var session = new WinFormsWatchdogUiProductionSession(sta.Control))
            {
                var callback = new ProductionHandler();
                var receipt = session.BindReadyAsync(
                        "Main_Frm.WatchdogSafety",
                        "FrmEpbMainMonitor.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(receipt != null && receipt.Accepted && receipt.Ready,
                    "Stop runner production seam did not reach Ready.");
                Assert(session.PublishPipe("operator-stop", "prod-correlation", "prod-request"),
                    "Production StopAll envelope was not accepted.");
                Wait(callback.Seen.Task, TimeoutMilliseconds);
                Assert(callback.LastEnvelope.CorrelationId == "prod-correlation" &&
                       callback.LastEnvelope.RequestId == "prod-request",
                    "Stop runner envelope lost correlation/request identity.");
                receipt.HandlerLease.Dispose();
                session.Close();
            }
        }

        private static void ProductionDisconnectedCloseKeepsTargetAlive()
        {
            using (var sta = new StaFormHost())
            using (var session = new WinFormsWatchdogUiProductionSession(sta.Control))
            {
                var callback = new ProductionHandler();
                var binding = session.BindReadyAsync(
                        "Main_Frm.WatchdogSafety",
                        "FrmEpbMainMonitor.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(binding != null && binding.Accepted && binding.Ready,
                    "Disconnected production seam did not attach.");
                binding.HandlerLease.Dispose();
                var closeDecision = session.CaptureCloseDecision();
                Assert(closeDecision.HasActiveEvidence && closeDecision.KeepMessagePump &&
                       closeDecision.ReleaseOnlyAfterTerminal,
                    "FormClosing production decision did not retain the pump before terminal cleanup.");
                var receipt = session.Close();
                Assert(receipt != null && !session.Target.IsDisposed,
                    "Disconnected/non-owned target was disposed during close.");
                Assert(receipt.WorkerResourcesReleased,
                    "Disconnected close did not release pipeline workers.");
            }
        }

        private static void ProductionNonTerminalRetryThenTerminalReleasesOnce()
        {
            using (var sta = new StaFormHost())
            using (var session = new WinFormsWatchdogUiProductionSession(sta.Control))
            {
                var started = new ManualResetEventSlim(false);
                var release = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var callback = new ProductionHandler(started, release);
                var binding = session.BindReadyAsync(
                        "Main_Frm.WatchdogSafety",
                        "FrmEpbMainMonitor.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(binding != null && binding.Accepted && binding.Ready,
                    "Retained production seam did not attach.");
                Assert(session.PublishPipe("retained", "retained-correlation", "retained-request"),
                    "Retained production request was not observed.");
                Assert(started.Wait(TimeoutMilliseconds),
                    "Retained production handler did not start.");
                var retained = session.Close(TimeSpan.FromMilliseconds(20));
                Assert(retained != null && !retained.IsTerminal && !session.Target.IsDisposed,
                    "Nonterminal production close did not retain the target.");
                release.TrySetResult(true);
                var terminal = session.Close(TimeSpan.FromSeconds(2));
                Assert(terminal != null && terminal.IsTerminal &&
                       terminal.RetentionVersion > retained.RetentionVersion,
                    "Production close retry did not publish a newer terminal receipt.");
                Assert(!session.Target.IsDisposed,
                    "Non-owned production UI target was disposed at terminal close.");
                binding.HandlerLease.Dispose();
            }
        }

        private static void ProductionReceiptCarriesCompleteIdentity()
        {
            using (var sta = new StaFormHost())
            using (var session = new WinFormsWatchdogUiProductionSession(sta.Control))
            {
                var callback = new ProductionHandler();
                var receipt = session.BindReadyAsync(
                        "Main_Frm.Identity.WatchdogSafety",
                        "FrmEpbMainMonitor.Identity.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(receipt != null && receipt.Accepted && receipt.Ready,
                    "Identity production seam did not attach.");
                var identity = session.Context.ValidatedAttachIdentity;
                Assert(identity != null && identity.SessionLease > 0 &&
                       identity.AuthorityGeneration > 0 &&
                       identity.AttachedConnectionGeneration == identity.AttachEpoch &&
                       identity.AuthorityProcessId > 0 &&
                       identity.AuthorityProcessStartUtcTicks > 0 &&
                       !string.IsNullOrWhiteSpace(identity.AuthorityInstanceNonceHash),
                    "Production binding receipt omitted a frozen authority identity component.");
                Assert(receipt.ExactKey == identity.ExactKey &&
                       receipt.SessionId == identity.SessionId &&
                       receipt.SessionGeneration == identity.SessionGeneration &&
                       receipt.SessionLease == identity.SessionLease &&
                       receipt.AuthorityGeneration == identity.AuthorityGeneration &&
                       receipt.AttachedConnectionGeneration == identity.AttachedConnectionGeneration &&
                       receipt.AttachEpoch == identity.AttachEpoch &&
                       receipt.AuthorityProcessId == identity.AuthorityProcessId &&
                       receipt.AuthorityProcessStartUtcTicks == identity.AuthorityProcessStartUtcTicks &&
                       receipt.AuthorityInstanceNonceHash == identity.AuthorityInstanceNonceHash,
                    "Identity production receipt did not preserve complete ExactKey.");
                receipt.HandlerLease.Dispose();
                session.Close();
            }
        }

        private static void ProductionStopHandlerUsesSharedProductionCore()
        {
            using (var sta = new StaFormHost())
            using (var session = new WinFormsWatchdogUiProductionSession(sta.Control))
            {
                var callback = new ProductionHandler();
                var binding = session.BindReadyAsync(
                        "Main_Frm.WatchdogSafety",
                        "FrmEpbMainMonitor.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(binding != null && binding.Accepted && binding.Ready,
                    "Stop handler production core could not establish Ready binding.");

                var envelope = new WatchdogStopAllOfferEnvelope(
                    session.ScopeLease.Scope,
                    session.ScopeLease,
                    "production-stop-core-correlation",
                    "ProductionStopCore",
                    "production-stop-core-detail",
                    WatchdogStopAllSourceFlags.Pipe,
                    1,
                    0,
                    "production-stop-core-request");
                var port = new ControlledStopSafetyPort
                {
                    Result = CreateCompletedStopResult()
                };
                session.HandleStopEnvelopeAsync(envelope, port)
                    .GetAwaiter().GetResult();
                Assert(port.PrepareCalls == 1 && port.NotifyCalls == 1 &&
                       port.ExitCalls == 1 &&
                       port.LastContext.CorrelationId == envelope.CorrelationId,
                    "Production stop handler did not preserve correlation or terminal exit ordering.");

                var fault = new ControlledStopSafetyPort
                {
                    Result = CreateCompletedStopResult(),
                    ThrowOnPrepare = true
                };
                try
                {
                    session.HandleStopEnvelopeAsync(envelope, fault)
                        .GetAwaiter().GetResult();
                    throw new InvalidOperationException(
                        "Faulting production stop port unexpectedly completed.");
                }
                catch (InvalidOperationException ex)
                {
                    Assert(ex.Message.IndexOf("ControlledStopSafetyFailure",
                        StringComparison.Ordinal) >= 0,
                        "Faulting production stop port changed the original failure.");
                }
                Assert(fault.NotifyCalls == 0 && fault.ExitCalls == 0,
                    "Faulting stop handler published completion/exit despite safety failure.");
                binding.HandlerLease.Dispose();
                session.Close();
            }
        }

        /// <summary>
        /// Drives the process-global Runtime facade and its real transport
        /// Engine/sidecar, then uses the same binding transaction as Main_Frm.
        /// The old frozen identity must be rejected after the global Runtime
        /// replacement; the transaction must fail before reserving a new UI
        /// target or handler.
        /// </summary>
        private static void ProductionGlobalEngineExactBindingAndReplacement()
        {
            var journal = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.UiGlobalExact." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journal);
            try
            {
                try { WatchdogRuntime.ShutdownRuntimeWithReceipt(); } catch { }
                WatchdogRuntime.ConfigureJournalExportPath(journal);
                WatchdogRuntime.SetHeartbeatProvider(() => new WatchdogHeartbeat
                {
                    Phase = "UiGlobalExact"
                });

                var start = WatchdogRuntime.StartSessionAsync(new[] { 4, 10 })
                    .GetAwaiter().GetResult();
                Assert(start != null && start.Attached,
                    "Process-global Runtime/sidecar did not reach exact Attached.");
                var before = WatchdogRuntime.CaptureTransportSnapshot();
                Assert(before != null && before.IsStable && before.Context != null &&
                       before.Engine != null && WatchdogRuntime.IsExactAttachedSnapshot(before),
                    "Global Engine snapshot was not a stable exact-attached composite.");
                var context = before.Context;
                var identity = context.ValidatedAttachIdentity;
                Assert(identity != null && identity.ExactKey.Split('|').Length >= 9,
                    "Global Runtime did not freeze complete UI identity.");

                using (var sta = new StaFormHost())
                using (var target = new WinFormsWatchdogPostTarget(sta.Control))
                {
                    var callback = new ProductionHandler();
                    var receipt = WinFormsWatchdogUiBindingCore.ExecuteAsync(
                            context,
                            identity,
                            "Main_Frm.GlobalExact.WatchdogSafety",
                            target,
                            false,
                            "FrmEpbMainMonitor.GlobalExact.WatchdogSafety",
                            callback.Handle,
                            null,
                            true)
                        .GetAwaiter().GetResult();
                    Assert(receipt != null && receipt.Accepted && receipt.Ready,
                        "Global exact UI binding transaction did not reach Ready.");
                    Assert(receipt.ExactKey == identity.ExactKey &&
                           receipt.SessionId == identity.SessionId &&
                           receipt.SessionGeneration == identity.SessionGeneration &&
                           receipt.SessionLease == identity.SessionLease &&
                           receipt.AuthorityGeneration == identity.AuthorityGeneration &&
                           receipt.AttachedConnectionGeneration == identity.AttachedConnectionGeneration &&
                           receipt.AttachEpoch == identity.AttachEpoch &&
                           receipt.AuthorityProcessId == identity.AuthorityProcessId &&
                           receipt.AuthorityProcessStartUtcTicks == identity.AuthorityProcessStartUtcTicks &&
                           receipt.AuthorityInstanceNonceHash == identity.AuthorityInstanceNonceHash,
                        "Global binding receipt omitted one of the frozen nine identity fields.");

                    var terminal = WatchdogRuntime.ShutdownRuntimeWithReceipt();
                    Assert(terminal != null && terminal.IsTerminal &&
                           terminal.SessionId == identity.SessionId &&
                           terminal.SessionLease == identity.SessionLease,
                        "Global Runtime retention did not produce exact terminal receipt.");

                    var stale = WinFormsWatchdogUiBindingCore.ExecuteAsync(
                            context,
                            identity,
                            "Main_Frm.GlobalExact.Replacement",
                            target,
                            false,
                            "FrmEpbMainMonitor.GlobalExact.Replacement",
                            callback.Handle,
                            null,
                            true)
                        .GetAwaiter().GetResult();
                    Assert(stale != null && !stale.Accepted &&
                           stale.Reason == "ExactAttachedRequired",
                        "Old global identity was allowed to bind after Runtime replacement.");
                    Assert(context.SafetyTargetLease == null ||
                           context.PipelineState == RuntimeCallbackPipelineState.Terminal ||
                           context.PipelineState == RuntimeCallbackPipelineState.Closing,
                        "Global terminal cleanup left an active old UI binding.");
                }
            }
            finally
            {
                try { WatchdogRuntime.ShutdownRuntimeWithReceipt(); } catch { }
                WatchdogRuntime.SetHeartbeatProvider(null);
                WatchdogRuntime.ConfigureJournalExportPath(null);
                try { if (Directory.Exists(journal)) Directory.Delete(journal, true); }
                catch { }
            }
        }

        private static void ProductionMainResourceOwnerReleasesTerminalOnce()
        {
            using (var sta = new StaFormHost())
            using (var session = new WinFormsWatchdogUiProductionSession(sta.Control))
            {
                var callback = new ProductionHandler();
                var binding = session.BindReadyAsync(
                        "Main_Frm.ResourceOwner.WatchdogSafety",
                        "FrmEpbMainMonitor.ResourceOwner.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(binding != null && binding.Accepted && binding.Ready,
                    "Main UI resource-owner case did not establish Ready binding.");

                var owner = new WinFormsWatchdogUiResourceOwner();
                Assert(owner.Publish(session.Context, binding.TargetLease, session.Target),
                    "Main production resource owner rejected the exact binding tuple.");
                Assert(!owner.ReleaseAfterTerminal(null) && owner.ReleaseCount == 0,
                    "Main UI resources were released before a terminal retention receipt.");

                var terminal = session.Close(TimeSpan.FromSeconds(2));
                Assert(terminal != null && terminal.IsTerminal,
                    "Main UI resource-owner case did not obtain terminal retention evidence.");

                // This is a real terminal receipt with only its frozen
                // generation tampered.  A lease/session match alone must not
                // authorize disposal of the published tuple.
                var wrongGeneration = new RuntimeShutdownReceipt(
                    terminal.ClosingAttempt, terminal.EngineReceipt,
                    terminal.IngressReceipt, terminal.StopAllReceipt,
                    terminal.CallbackReceipt, terminal.PipelineState,
                    terminal.TerminalReason, terminal.TestAbandoned,
                    terminal.RetentionVersion, terminal.SessionId,
                    terminal.SessionGeneration + 1, terminal.SessionLease,
                    terminal.Retained, terminal.JournalFlushCompleted,
                    terminal.JournalDisposed,
                    terminal.PreviousRuntimeShutdownIncomplete);
                Assert(!owner.ReleaseAfterTerminal(wrongGeneration) &&
                       owner.ReleaseCount == 0 && owner.HasResources,
                    "Generation-mismatched terminal evidence released Main UI resources.");
                Parallel.For(0, 64, _ => owner.ReleaseAfterTerminal(terminal));
                Assert(owner.ReleaseCount == 1 && !owner.HasResources,
                    "Main UI resource release was not exactly once after terminal receipt.");
                binding.HandlerLease.Dispose();
            }
        }

        private static void ProductionMainAdapterDisposeRacesBindingCompletion()
        {
            Exception failure = null;
            var journal = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.UiAdapterDisposeRace." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journal);
            try
            {
                using (var sta = new StaFormHost())
                {
                    var completed = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    sta.Control.BeginInvoke((Action)(async () =>
                    {
                        Main_Frm main = null;
                        RuntimeTransportSessionContext raceContext = null;
                        var originalDirectory = Environment.CurrentDirectory;
                        try
                        {
                            Environment.CurrentDirectory =
                                AppDomain.CurrentDomain.BaseDirectory;
                            WatchdogRuntime.ConfigureJournalExportPath(journal);
                            WatchdogRuntime.SetHeartbeatProvider(() => new WatchdogHeartbeat
                            {
                                Phase = "MainAdapterDisposeRace"
                            });
                            main = new Main_Frm();
                            main.CreateControl();
                            var mainHandle = main.Handle;
                            Assert(mainHandle != IntPtr.Zero && main.IsHandleCreated,
                                "Dispose race Main_Frm did not create a native handle.");

                            var started = await WatchdogRuntime.StartSessionAsync(new[] { 4 })
                                .ConfigureAwait(true);
                            Assert(started != null && started.Attached,
                                "Dispose race Runtime session did not Attach.");
                            var context = WatchdogRuntime.CaptureTransportSnapshot().Context;
                            raceContext = context;
                            Assert(context != null,
                                "Dispose race lost the exact active Runtime context.");

                            var probeReached = new TaskCompletionSource<bool>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            var releaseProbe = new TaskCompletionSource<bool>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            context.AfterActivateAndTakeBeforeReadyProbe = _ =>
                            {
                                probeReached.TrySetResult(true);
                                releaseProbe.Task.GetAwaiter().GetResult();
                            };

                            var bindTask = Task.Run(() =>
                                main.BindWatchdogUiProductionAsync(
                                    "Main_Frm.DisposeRace.WatchdogSafety",
                                    "Main_Frm.DisposeRace.SafetyHandler",
                                    ProductionMainSafetyHandler));
                            Assert(probeReached.Task.Wait(TimeoutMilliseconds),
                                "Dispose race did not reach the production activation boundary.");

                            // Main_Frm.Dispose calls only the adapter admission
                            // close.  It must not dispose _bindGate or an
                            // active handler while BindingCompletion is held.
                            main.Dispose();
                            releaseProbe.TrySetResult(true);
                            var binding = await bindTask.ConfigureAwait(true);
                            Assert(binding != null && !binding.Accepted &&
                                   (binding.Reason == "UiAdapterDisposed" ||
                                    binding.Reason == "UiBindingIdentityChanged"),
                                "Dispose race published a binding after adapter disposal.");
                            Assert(!main.WatchdogUiHasResources &&
                                   main.WatchdogPostTarget == null &&
                                   main.WatchdogTargetLease == null &&
                                   main.WatchdogUiResourceReleaseCount == 0,
                                "Dispose race left a half-published Main UI tuple.");
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                        finally
                        {
                            try
                            {
                                if (raceContext != null)
                                    raceContext.AfterActivateAndTakeBeforeReadyProbe = null;
                                var cleanup = WatchdogRuntime.ShutdownRuntimeWithReceipt();
                                if (cleanup == null || !cleanup.IsTerminal)
                                    failure ??= new InvalidOperationException(
                                        "Dispose race Runtime cleanup was not terminal: " +
                                        (cleanup?.TerminalReason ?? "<null>"));
                            }
                            catch (Exception ex) { failure ??= ex; }
                            try { main?.Dispose(); } catch (Exception ex) { failure ??= ex; }
                            try { Environment.CurrentDirectory = originalDirectory; }
                            catch (Exception ex) { failure ??= ex; }
                            completed.TrySetResult(true);
                        }
                    }));
                    Assert(completed.Task.Wait(60000),
                        "Dispose race STA did not complete in time.");
                }
            }
            finally
            {
                WatchdogRuntime.SetHeartbeatProvider(null);
                WatchdogRuntime.ConfigureJournalExportPath(null);
                try { if (Directory.Exists(journal)) Directory.Delete(journal, true); }
                catch { }
            }
            if (failure != null)
                throw new InvalidOperationException(
                    "Main adapter Dispose/BindingCompletion race failed.", failure);
        }

        /// <summary>
        /// Exercises the actual Main_Frm resource owner adapter rather than
        /// only the shared owner type.  The Main form is constructed on an
        /// STA thread, the same production binding tuple is published through
        /// Main_Frm.PublishWatchdogUiResourcesForProduction, and terminal
        /// release is fanned in concurrently through Main_Frm's real
        /// ReleaseWatchdogUiResources path.
        /// </summary>
        private static void ProductionMainFormDelegatesResourceReleaseExactlyOnce()
        {
            Exception failure = null;
            var stage = "scheduled";
            using (var sta = new StaFormHost())
            {
                var completed = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                sta.Control.BeginInvoke((Action)(async () =>
                {
                    Main_Frm main = null;
                    WinFormsWatchdogUiProductionSession session = null;
                    var originalDirectory = Environment.CurrentDirectory;
                    try
                    {
                        // Main_Frm_Load is the real production load boundary;
                        // point it at the copied Release Config closure rather
                        // than the repository's source Config directory.
                        Environment.CurrentDirectory =
                            AppDomain.CurrentDomain.BaseDirectory;
                        stage = "construct-main";
                        main = new Main_Frm();
                        stage = "main-constructed";
                        // Keep the real Main control on the running STA pump,
                        // but do not enter Main_Frm.Load: that lifecycle reads
                        // the operator's project/UNC configuration and may
                        // show a modal error unrelated to UI ownership.
                        main.CreateControl();
                        var mainHandle = main.Handle;
                        Assert(mainHandle != IntPtr.Zero && main.IsHandleCreated,
                            "Real Main_Frm did not create a native handle on STA.");
                        stage = "main-control-created";
                        stage = "construct-session";
                        session = new WinFormsWatchdogUiProductionSession(main);
                        var callback = new ProductionHandler();
                        stage = "bind-ready";
                        var binding = await session.BindReadyAsync(
                                "Main_Frm.RealOwner.WatchdogSafety",
                                "FrmEpbMainMonitor.RealOwner.WatchdogSafety",
                                callback.Handle)
                            .ConfigureAwait(true);
                        Assert(binding != null && binding.Accepted && binding.Ready,
                            "Real Main_Frm resource-owner binding did not reach Ready.");
                        stage = "publish-resources";
                        Assert(main.PublishWatchdogUiResourcesForProduction(
                                session.Context, session.Target, binding.TargetLease),
                            "Main_Frm rejected its exact production resource tuple.");
                        Assert(!main.ReleaseWatchdogUiResources(null) &&
                               main.WatchdogUiResourceReleaseCount == 0,
                            "Main_Frm released resources before terminal retention evidence.");

                        stage = "close-session";
                        var terminal = session.Close(TimeSpan.FromSeconds(2));
                        Assert(terminal != null && terminal.IsTerminal,
                            "Real Main_Frm resource-owner close did not become terminal.");
                        Parallel.For(0, 64, _ => main.ReleaseWatchdogUiResources(terminal));
                        Assert(main.WatchdogUiResourceReleaseCount == 1 &&
                               main.WatchdogPostTarget == null &&
                               main.WatchdogTargetLease == null,
                            "Main_Frm did not release its target/lease exactly once after terminal.");
                        stage = "dispose-binding";
                        binding.HandlerLease.Dispose();
                        stage = "completed";
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        try { session?.Dispose(); } catch (Exception ex) { failure ??= ex; }
                        try { main?.Dispose(); } catch (Exception ex) { failure ??= ex; }
                        try { Environment.CurrentDirectory = originalDirectory; }
                        catch (Exception ex) { failure ??= ex; }
                        completed.TrySetResult(true);
                    }
                }));
                Assert(completed.Task.Wait(TimeoutMilliseconds),
                    "Real Main_Frm resource-owner STA did not complete in time (stage=" +
                    stage + ").");
            }
            if (failure != null) throw new InvalidOperationException(
                "Real Main_Frm resource-owner production case failed.", failure);
        }

        /// <summary>
        /// Exercises the Main-owned normal-stop transaction with the real
        /// process Runtime/Sidecar.  The same Main instance must retain its
        /// ownership boundary until a terminal receipt, release exactly once
        /// per session, and then bind the next exact Attached session without
        /// reusing the old context or target lease.
        /// </summary>
        private static void ProductionMainNormalStopReleasesAndRebindsSameMain()
        {
            Exception failure = null;
            var journal = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.UiMainNormalStop." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journal);
            try
            {
                using (var sta = new StaFormHost())
                {
                    var completed = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    sta.Control.BeginInvoke((Action)(async () =>
                    {
                        Main_Frm main = null;
                        try
                        {
                            try { WatchdogRuntime.ShutdownRuntimeWithReceipt(); } catch { }
                            WatchdogRuntime.ConfigureJournalExportPath(journal);
                            WatchdogRuntime.SetHeartbeatProvider(() => new WatchdogHeartbeat
                            {
                                Phase = "MainNormalStop"
                            });

                            // This callback is running on the real STA pump.
                            // Create a native Main handle without entering the
                            // operator Load/UNC/modal-dialog lifecycle.
                            main = new Main_Frm();
                            main.CreateControl();
                            var mainHandle = main.Handle;
                            Assert(mainHandle != IntPtr.Zero && main.IsHandleCreated,
                                "Main normal-stop Main_Frm did not create a native handle.");
                            var first = await WatchdogRuntime.StartSessionAsync(new[] { 4 })
                                .ConfigureAwait(true);
                            Assert(first != null && first.Attached,
                                "Main normal-stop first Runtime session did not Attach.");
                            var firstBinding = await main.BindWatchdogUiProductionAsync(
                                    "Main_Frm.NormalStop.WatchdogSafety",
                                    "Main_Frm.NormalStop.SafetyHandler",
                                    ProductionMainSafetyHandler)
                                .ConfigureAwait(true);
                            Assert(firstBinding != null && firstBinding.Accepted && firstBinding.Ready,
                                "Main normal-stop first exact UI binding was not Ready; reason=" +
                                (firstBinding == null ? "<null>" : firstBinding.Reason));
                            var firstContext = firstBinding.Context;
                            var firstTarget = main.WatchdogPostTarget;
                            var firstStop = await main.ShutdownWatchdogSessionAndReleaseUiAsync(
                                    "ProductionNormalStopFirst")
                                .ConfigureAwait(true);
                            Assert(firstStop != null && firstStop.IsTerminal &&
                                   firstStop.SessionId == firstContext.SessionId &&
                                   firstStop.SessionLease == firstContext.SessionLease,
                                "Main normal stop did not return an exact terminal receipt.");
                            Assert(main.WatchdogUiResourceReleaseCount == 1 &&
                                   main.WatchdogPostTarget == null &&
                                   main.WatchdogTargetLease == null &&
                                   firstTarget != null && firstTarget.IsDisposed,
                                "Main normal stop did not release its target/lease exactly once.");

                            var second = await WatchdogRuntime.StartSessionAsync(new[] { 4 })
                                .ConfigureAwait(true);
                            Assert(second != null && second.Attached &&
                                   !string.Equals(second.SessionId, first.SessionId,
                                       StringComparison.Ordinal),
                                "Main normal stop did not create the next exact Runtime session.");
                            var secondBinding = await main.BindWatchdogUiProductionAsync(
                                    "Main_Frm.NormalStop.WatchdogSafety",
                                    "Main_Frm.NormalStop.SafetyHandler",
                                    ProductionMainSafetyHandler)
                                .ConfigureAwait(true);
                            Assert(secondBinding != null && secondBinding.Accepted &&
                                   secondBinding.Ready &&
                                   !ReferenceEquals(secondBinding.Context, firstContext) &&
                                   secondBinding.Context.SessionId == second.SessionId,
                                "The same Main instance did not become Ready for the next exact session.");

                            var closes = Enumerable.Range(0, 64)
                                .Select(_ => Task.Run(() =>
                                    main.ShutdownWatchdogSessionAndReleaseUiAsync(
                                            "ProductionNormalStopConcurrent")))
                                .ToArray();
                            Assert(await Task.WhenAll(closes).ConfigureAwait(true) != null,
                                "Concurrent Main normal-stop callers did not join in time.");
                            Assert(closes.All(task => task.Status == TaskStatus.RanToCompletion &&
                                                      task.Result != null && task.Result.IsTerminal),
                                "Concurrent Main normal-stop callers did not share terminal evidence.");
                            Assert(main.WatchdogUiResourceReleaseCount == 2 &&
                                   main.WatchdogPostTarget == null &&
                                   main.WatchdogTargetLease == null,
                                "Concurrent Main release was not exactly once for the second session.");
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                        finally
                        {
                            try { main?.Dispose(); } catch (Exception ex) { failure ??= ex; }
                            completed.TrySetResult(true);
                        }
                    }));
                    Assert(completed.Task.Wait(60000),
                        "Main normal-stop/rebind production STA did not complete in time.");
                }
            }
            finally
            {
                try { WatchdogRuntime.ShutdownRuntimeWithReceipt(); } catch { }
                WatchdogRuntime.SetHeartbeatProvider(null);
                WatchdogRuntime.ConfigureJournalExportPath(null);
                try { if (Directory.Exists(journal)) Directory.Delete(journal, true); }
                catch { }
            }
            if (failure != null)
                throw new InvalidOperationException(
                    "Main normal-stop/rebind production case failed.", failure);
        }

        private static async Task ProductionMainSafetyHandler(
            WatchdogStopAllOfferEnvelope envelope)
        {
            await Task.CompletedTask.ConfigureAwait(true);
        }

        private static void ProductionEntryPolicyBlocksBatchStartBeforePort()
        {
            var calls = 0;
            var blockedDecision = WinFormsWatchdogUiEntryPolicy.Evaluate(
                WinFormsWatchdogUiEntryKind.Normal,
                false,
                true);
            try
            {
                WinFormsWatchdogUiEntryCoordinator.StartIfAllowedAsync(
                        blockedDecision,
                        () =>
                        {
                            Interlocked.Increment(ref calls);
                            return Task.FromResult(1);
                        })
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException(
                    "Entry coordinator unexpectedly started a batch without exact Attached.");
            }
            catch (InvalidOperationException ex)
            {
                Assert(ex.Message.IndexOf("ExactAttachedRequired",
                    StringComparison.Ordinal) >= 0,
                    "Entry coordinator changed the exact-attached rejection reason.");
            }
            Assert(calls == 0,
                "Batch start port was invoked after the exact-attached gate failed.");

            var allowedDecision = WinFormsWatchdogUiEntryPolicy.Evaluate(
                WinFormsWatchdogUiEntryKind.Normal,
                true,
                true);
            var result = WinFormsWatchdogUiEntryCoordinator.StartIfAllowedAsync(
                    allowedDecision,
                    () =>
                    {
                        Interlocked.Increment(ref calls);
                        return Task.FromResult(7);
                    })
                .GetAwaiter().GetResult();
            Assert(result == 7 && calls == 1,
                "Entry coordinator did not invoke the batch start port exactly once after Ready.");

            var recoveryIntent = new WatchdogRecoveryIntent
            {
                SessionId = "entry-recovery-session",
                PipeName = "entry-recovery-pipe",
                SidecarProcessId = 1,
                SidecarProcessStartUtcTicks = 1,
                SidecarInstanceNonce = new string('a', 32)
            };
            var recoveryDecision = WinFormsWatchdogUiEntryPolicy.EvaluateRecoveryIntent(
                recoveryIntent,
                true);
            Assert(recoveryDecision.Allowed && recoveryDecision.AttachExistingAuthorityOnly,
                "Recovery/StartIdle entry did not retain AttachOnly policy.");
        }

        /// <summary>
        /// Exercises the real binding transaction with a monitor-style lease
        /// that belongs to a different session, then drives every early exit
        /// after target admission.  Each rejected transaction must withdraw
        /// only its unpublished target lease and leave the context in the
        /// normal Closing path so the real retention coordinator can finish;
        /// no half-published target/handler tuple is allowed.
        /// </summary>
        private static void ProductionStaleMonitorLeaseAndRejectedBranchesCloseSafely()
        {
            using (var sta = new StaFormHost())
            using (var sessionA = new WinFormsWatchdogUiProductionSession(sta.Control))
            using (var sessionB = new WinFormsWatchdogUiProductionSession(sta.Control))
            using (var monitor = new FrmEpbMainMonitor())
            {
                var callback = new ProductionHandler();
                var bindingA = sessionA.BindReadyAsync(
                        "Main_Frm.StaleA.WatchdogSafety",
                        "FrmEpbMainMonitor.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(bindingA != null && bindingA.Accepted && bindingA.Ready,
                    "Session A did not establish the monitor-style handler lease.");

                // Commit through the production monitor holder so the stale
                // lease follows exactly the application ownership path.
                monitor.CommitWatchdogSafetyHandler(bindingA);
                var staleMonitorLease = monitor.WatchdogSafetyHandlerLease;
                Assert(staleMonitorLease != null && staleMonitorLease.IsActive,
                    "Monitor did not retain the active session-A handler lease.");

                Assert(MainWatchdogUiLifecycleAdapter.SelectExistingHandlerForContext(
                           staleMonitorLease, sessionB.Context,
                           "FrmEpbMainMonitor.WatchdogSafety") == null,
                    "A monitor lease from session A was selected for session B.");

                var bindingB = sessionB.BindReadyAsync(
                        "Main_Frm.StaleB.WatchdogSafety",
                        "FrmEpbMainMonitor.WatchdogSafety",
                        callback.Handle)
                    .GetAwaiter().GetResult();
                Assert(bindingB != null && bindingB.Accepted && bindingB.Ready,
                    "Session B did not reach Ready after stale-lease filtering.");
                Assert(!ReferenceEquals(bindingA.HandlerLease, bindingB.HandlerLease) &&
                       bindingB.HandlerLease.IsActive &&
                       ReferenceEquals(bindingB.HandlerLease.Context, sessionB.Context),
                    "Session B reused the stale handler lease or lost its exact context.");

                monitor.CommitWatchdogSafetyHandler(bindingB);
                Assert(!staleMonitorLease.IsActive &&
                       ReferenceEquals(monitor.WatchdogSafetyHandlerLease,
                           bindingB.HandlerLease),
                    "Monitor did not atomically replace and dispose the stale lease.");
                monitor.ReleaseWatchdogSafetyHandler();

                Assert(sessionA.Close(TimeSpan.FromSeconds(2))?.IsTerminal == true,
                    "Session A stale-lease close did not reach terminal retention.");
                Assert(sessionB.Close(TimeSpan.FromSeconds(2))?.IsTerminal == true,
                    "Session B exact-lease close did not reach terminal retention.");

                using (var mismatch = new WinFormsWatchdogUiProductionSession(sta.Control))
                {
                    var mismatchReady = mismatch.PublishValidatedAttachedBeforeBind();
                    Assert(mismatchReady,
                        "Mismatch branch could not establish the production pre-attach identity.");
                    var mismatchReceipt = WinFormsWatchdogUiBindingCore.ExecuteAsync(
                            mismatch.Context,
                            mismatch.Context.ValidatedAttachIdentity,
                            "Main_Frm.Mismatch.WatchdogSafety",
                            mismatch.Target,
                            false,
                            "FrmEpbMainMonitor.WatchdogSafety",
                            callback.Handle,
                            bindingB.HandlerLease,
                            false)
                        .GetAwaiter().GetResult();
                    Assert(mismatchReceipt != null && !mismatchReceipt.Accepted &&
                           mismatchReceipt.Reason == "ExistingHandlerIdentityMismatch",
                        "Existing-handler identity mismatch did not fail closed at the transaction boundary.");
                    Assert(mismatch.Context.SafetyTargetLease == null &&
                           mismatch.TargetLease == null &&
                           mismatch.Context.PipelineState == RuntimeCallbackPipelineState.Closing,
                        "Mismatch branch left a half-published target or non-Closing pipeline.");
                    Assert(mismatch.Close(TimeSpan.FromSeconds(2))?.IsTerminal == true,
                        "Mismatch branch could not complete real retention after cleanup.");
                }

                using (var missing = new WinFormsWatchdogUiProductionSession(sta.Control))
                {
                    Assert(missing.PublishValidatedAttachedBeforeBind(),
                        "Missing-handler branch could not establish production identity.");
                    var missingReceipt = WinFormsWatchdogUiBindingCore.ExecuteAsync(
                            missing.Context,
                            missing.Context.ValidatedAttachIdentity,
                            "Main_Frm.MissingHandler.WatchdogSafety",
                            missing.Target,
                            false,
                            "FrmEpbMainMonitor.WatchdogSafety",
                            null, null, false)
                        .GetAwaiter().GetResult();
                    Assert(missingReceipt != null && !missingReceipt.Accepted &&
                           missingReceipt.Reason == "StopHandlerMissing",
                        "Missing stop handler did not reject after target admission.");
                    Assert(missing.Context.SafetyTargetLease == null &&
                           missing.TargetLease == null &&
                           missing.Context.PipelineState == RuntimeCallbackPipelineState.Closing,
                        "Missing-handler branch left a half-published target or lease.");
                    Assert(missing.Close(TimeSpan.FromSeconds(2))?.IsTerminal == true,
                        "Missing-handler branch did not reach terminal retention.");
                }

                using (var rejected = new WinFormsWatchdogUiProductionSession(sta.Control))
                {
                    Assert(rejected.PublishValidatedAttachedBeforeBind(),
                        "Register-rejection branch could not establish production identity.");
                    var rejectedReceipt = WinFormsWatchdogUiBindingCore.ExecuteAsync(
                            rejected.Context,
                            rejected.Context.ValidatedAttachIdentity,
                            "Main_Frm.RegisterRejected.WatchdogSafety",
                            rejected.Target,
                            false,
                            string.Empty,
                            callback.Handle,
                            null, false)
                        .GetAwaiter().GetResult();
                    Assert(rejectedReceipt != null && !rejectedReceipt.Accepted &&
                           rejectedReceipt.Reason == "SubscriberIdMissing",
                        "Register rejection did not preserve the typed subscriber failure.");
                    Assert(rejected.Context.SafetyTargetLease == null &&
                           rejected.TargetLease == null &&
                           rejected.Context.PipelineState == RuntimeCallbackPipelineState.Closing,
                        "Register-rejection branch left a half-published target or lease.");
                    Assert(rejected.Close(TimeSpan.FromSeconds(2))?.IsTerminal == true,
                        "Register-rejection branch did not reach terminal retention.");
                }
            }
        }

        private static StopSafetyResult CreateCompletedStopResult()
        {
            return new StopSafetyResult
            {
                Outcome = StopSafetyOutcome.CompletedSafe,
                LastStage = StopSafetyStage.Completed,
                CorrelationId = "production-stop-core-correlation",
                MotorOffCommandSucceeded = true,
                PowerOffConfirmed = true,
                PressureSafeConfirmed = true,
                PersistenceBoundaryConfirmed = true,
                RawStorageFlushed = true,
                LogicalQuiescenceConfirmed = true,
                DataContinuityCompromised = false,
                CompletedUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Named production handler used by the binding transaction tests.
        /// Keeping the callback as a real method (rather than a test lambda)
        /// makes the acceptance path identical to the monitor's typed
        /// handler registration.
        /// </summary>
        private sealed class ProductionHandler
        {
            private readonly ManualResetEventSlim _started;
            private readonly TaskCompletionSource<bool> _release;
            internal readonly TaskCompletionSource<WatchdogStopAllOfferEnvelope> Seen =
                new TaskCompletionSource<WatchdogStopAllOfferEnvelope>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            internal WatchdogStopAllOfferEnvelope LastEnvelope { get; private set; }

            internal ProductionHandler(
                ManualResetEventSlim started = null,
                TaskCompletionSource<bool> release = null)
            {
                _started = started;
                _release = release;
            }

            internal Task Handle(WatchdogStopAllOfferEnvelope envelope)
            {
                LastEnvelope = envelope;
                Seen.TrySetResult(envelope);
                if (_release == null) return Task.CompletedTask;
                _started?.Set();
                return _release.Task;
            }
        }

        private sealed class ControlledStopSafetyPort : IWinFormsWatchdogStopSafetyPort
        {
            internal StopSafetyResult Result { get; set; }
            internal bool ThrowOnPrepare { get; set; }
            internal int PrepareCalls;
            internal int NotifyCalls;
            internal int ExitCalls;
            internal StopContext LastContext { get; private set; }

            public Task<StopSafetyResult> PrepareForFreshRestartAsync(
                StopContext context)
            {
                Interlocked.Increment(ref PrepareCalls);
                LastContext = context;
                if (ThrowOnPrepare)
                    throw new InvalidOperationException("ControlledStopSafetyFailure");
                return Task.FromResult(Result);
            }

            public void NotifyStopCompleted(
                WatchdogStopSummary summary, string reason)
            {
                Interlocked.Increment(ref NotifyCalls);
            }

            public void RequestWatchdogOwnedExit(string reason)
            {
                Interlocked.Increment(ref ExitCalls);
            }
        }

        private static void Wait(Task task, int milliseconds)
        {
            if (task == null || !task.Wait(milliseconds))
                throw new TimeoutException("Timed out waiting for production WinForms callback.");
            if (task.IsFaulted) task.GetAwaiter().GetResult();
        }

        private static void WaitUntil(Func<bool> predicate, int milliseconds)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < milliseconds)
            {
                if (predicate()) return;
                Thread.Sleep(10);
            }
            throw new TimeoutException("Timed out waiting for production WinForms pipeline state.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class StaFormHost : IDisposable
        {
            private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
            private readonly Thread _thread;
            private Form _form;
            private Exception _startupException;
            private int _disposed;
            private int _callbackWasSta;

            internal StaFormHost()
            {
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "WatchdogWinFormsUiProduction.STA"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                if (!_ready.Wait(TimeoutMilliseconds))
                    throw new TimeoutException("STA WinForms message pump did not start.");
                if (_startupException != null) throw _startupException;
            }

            internal Control Control => _form;
            internal bool IsStaThread => Volatile.Read(ref _callbackWasSta) != 0;

            private void Run()
            {
                try
                {
                    _form = new Form
                    {
                        ShowInTaskbar = false,
                        FormBorderStyle = FormBorderStyle.FixedToolWindow,
                        StartPosition = FormStartPosition.Manual,
                        Location = new System.Drawing.Point(-32000, -32000),
                        Width = 1,
                        Height = 1,
                        Text = "Watchdog UI production seam"
                    };
                    _form.Shown += (sender, args) => _form.Hide();
                    _form.HandleCreated += (sender, args) => _ready.Set();
                    _form.Show();
                    if (!_form.IsHandleCreated) _form.CreateControl();
                    _ready.Set();
                    Application.Run(_form);
                }
                catch (Exception ex)
                {
                    _startupException = ex;
                    _ready.Set();
                }
                finally
                {
                    try { _form?.Dispose(); } catch { }
                }
            }

            internal void MarkCallbackSta()
            {
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                    Interlocked.Exchange(ref _callbackWasSta, 1);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try
                {
                    if (_form != null && !_form.IsDisposed && _form.IsHandleCreated)
                    {
                        _form.BeginInvoke((Action)(() =>
                        {
                            try { _form.Close(); } catch { }
                        }));
                    }
                }
                catch { }
                if (_thread.IsAlive) _thread.Join(TimeoutMilliseconds);
                _ready.Dispose();
            }
        }
    }
}
