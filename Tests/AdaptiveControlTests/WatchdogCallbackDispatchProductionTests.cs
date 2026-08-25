using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Client;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Direct tests for the two-lane production callback dispatcher.  The
    /// post target is only a boundary seam; admission, posting, coalescing,
    /// completion classification and drain ownership all remain production
    /// code.
    /// </summary>
    internal static class WatchdogCallbackDispatchProductionTests
    {
        private enum ScriptedPost
        {
            Hold,
            Success,
            InfrastructureFault,
            HandlerFault,
            Canceled
        }

        private sealed class ControlledPostTarget : IWatchdogCallbackPostTarget, IDisposable
        {
            private readonly object _gate = new object();
            private readonly Queue<ScriptedPost> _script = new Queue<ScriptedPost>();
            private readonly List<TaskCompletionSource<object>> _held =
                new List<TaskCompletionSource<object>>();
            private int _calls;
            private int _disposed;
            private bool _passThrough;

            internal int Calls => Volatile.Read(ref _calls);

            internal void Enqueue(params ScriptedPost[] values)
            {
                lock (_gate)
                    foreach (var value in values ?? new ScriptedPost[0]) _script.Enqueue(value);
            }

            public WatchdogPostReceipt TryPost(Func<Task> callback)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return WatchdogPostReceipt.Rejected("Disposed");
                Interlocked.Increment(ref _calls);
                ScriptedPost behavior;
                bool passThrough;
                lock (_gate)
                {
                    passThrough = _passThrough;
                    behavior = _script.Count == 0 ? ScriptedPost.Hold : _script.Dequeue();
                }
                if (passThrough)
                    return new WatchdogPostReceipt(true, Invoke(callback), string.Empty, Calls);
                switch (behavior)
                {
                    case ScriptedPost.InfrastructureFault:
                        throw new InvalidOperationException("post infrastructure fault");
                    case ScriptedPost.HandlerFault:
                        return new WatchdogPostReceipt(
                            true,
                            Task.FromException(new ApplicationException("handler fault")),
                            string.Empty,
                            Calls);
                    case ScriptedPost.Canceled:
                        return new WatchdogPostReceipt(
                            true,
                            Task.FromCanceled(new CancellationToken(true)),
                            string.Empty,
                            Calls);
                    case ScriptedPost.Success:
                        return new WatchdogPostReceipt(true, Invoke(callback), string.Empty, Calls);
                    default:
                        var held = new TaskCompletionSource<object>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        lock (_gate) _held.Add(held);
                        return new WatchdogPostReceipt(true, held.Task, string.Empty, Calls);
                }
            }

            private static Task Invoke(Func<Task> callback)
            {
                try
                {
                    var task = callback == null ? Task.CompletedTask : callback();
                    return task ?? Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
            }

            internal void ReleaseAll()
            {
                TaskCompletionSource<object>[] held;
                lock (_gate)
                {
                    held = _held.ToArray();
                    _held.Clear();
                }
                foreach (var item in held) item.TrySetResult(null);
            }

            internal void ReleaseAllAndPassThrough()
            {
                lock (_gate) _passThrough = true;
                ReleaseAll();
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
                ReleaseAll();
            }
        }

        private sealed class Gate
        {
            internal int Value;
            internal void Hit() => Interlocked.Increment(ref Value);
        }

        internal static int RunAll()
        {
            var passed = 0;
            var failures = new List<string>();
            Run("Safety worker posts every queued offer before first completion",
                SafetyPostsAllQueuedOffers, ref passed, failures);
            Run("Diagnostic coalescing key includes exact identity and semantic key",
                DiagnosticKeyIncludesExactIdentity, ref passed, failures);
            Run("Diagnostic held batch never exceeds 32 active posts",
                DiagnosticHeldBatchRespectsInflightFence, ref passed, failures);
            Run("Post infrastructure, handler fault and cancellation remain distinct",
                CompletionKindsRemainDistinct, ref passed, failures);
            Run("Admission rejection reasons and counters are lane-specific",
                AdmissionRejectionsAreCountedByReason, ref passed, failures);
            Run("Blocked diagnostic lane never blocks safety lane",
                DiagnosticCannotBlockSafety, ref passed, failures);
            Run("SpaceAvailable and Audit subscribers are isolated",
                SubscriberFailuresAreIsolated, ref passed, failures);
            Run("Timed drain can continue and terminal receipt releases resources",
                TimeoutCanContinueToTerminal, ref passed, failures);
            if (failures.Count != 0)
                throw new InvalidOperationException(
                    "Dispatcher专项失败: " + string.Join("; ", failures));
            return passed;
        }

        private static void SafetyPostsAllQueuedOffers()
        {
            using (var target = new ControlledPostTarget())
            using (var dispatch = new WatchdogRuntimeCallbackDispatch(
                       new WatchdogRuntimeCallbackDispatchOptions
                       {
                           SafetyPostTarget = target,
                           DiagnosticPostTarget = new WatchdogThreadPoolPostTarget()
                       }))
            {
                var admissions = new List<WatchdogRuntimeCallbackOfferResult>();
                for (var index = 0; index < 3; index++)
                    admissions.Add(dispatch.OfferSafety(Offer(
                        WatchdogRuntimeCallbackLane.Safety,
                        "S" + index,
                        "safety-" + index)));
                Assert(admissions.All(item => item.Accepted && item.Completion != null),
                    "accepted safety offer未返回completion task");
                Assert(WaitUntil(() => target.Calls == 3, 1000),
                    "首个completion阻塞时worker未连续TryPost全部queued offer");
                Assert(admissions.All(item => !item.Completion.IsCompleted),
                    "测试target仍hold时completion提前完成");
                target.ReleaseAll();
                var receipt = dispatch.CompleteAndDrain(TimeSpan.FromSeconds(3));
                Assert(receipt.IsTerminal && receipt.AllResourcesReleased,
                    "释放held completion后未得到terminal/released receipt");
                Assert(receipt.SafetyPostAccepted == 3 && receipt.SafetyCompletionCompleted == 3,
                    "safety posting/completion计数不完整");
            }
        }

        private static void DiagnosticKeyIncludesExactIdentity()
        {
            using (var target = new ControlledPostTarget())
            using (var dispatch = new WatchdogRuntimeCallbackDispatch(
                       new WatchdogRuntimeCallbackDispatchOptions
                       {
                           DiagnosticPostTarget = target,
                           SafetyPostTarget = new WatchdogThreadPoolPostTarget()
                       }))
            {
                var identity = Identity("exact", 1, 2, 3, 4, Guid.Parse("11111111-1111-1111-1111-111111111111"));
                var first = dispatch.OfferDiagnostic(Diagnostic(identity, "code", "reason", "detail", "semantic"));
                var duplicate = dispatch.OfferDiagnostic(Diagnostic(identity, "code", "reason", "detail", "semantic"));
                Assert(first.Status == WatchdogRuntimeCallbackOfferStatus.Accepted && duplicate.Coalesced,
                    "完全相同诊断未coalesce");
                Assert(first.AdmissionSequence != duplicate.AdmissionSequence &&
                       duplicate.CanonicalSequence == first.CanonicalSequence &&
                       !ReferenceEquals(first.Completion, duplicate.Completion),
                    "coalesce未产生独立completion或canonical sequence错误");

                var variants = new[]
                {
                    Diagnostic(Identity("other-session", 1, 2, 3, 4, identity.ScopeId), "code", "reason", "detail", "semantic"),
                    Diagnostic(Identity("exact", 2, 2, 3, 4, identity.ScopeId), "code", "reason", "detail", "semantic"),
                    Diagnostic(Identity("exact", 1, 9, 3, 4, identity.ScopeId), "code", "reason", "detail", "semantic"),
                    Diagnostic(Identity("exact", 1, 2, 8, 4, identity.ScopeId), "code", "reason", "detail", "semantic"),
                    Diagnostic(Identity("exact", 1, 2, 3, 7, identity.ScopeId), "code", "reason", "detail", "semantic"),
                    Diagnostic(Identity("exact", 1, 2, 3, 4, Guid.Parse("22222222-2222-2222-2222-222222222222")), "code", "reason", "detail", "semantic"),
                    Diagnostic(identity, "code", "reason", "detail", "other-semantic"),
                    Diagnostic(identity, "other-code", "reason", "detail", "semantic"),
                    Diagnostic(identity, "code", "other-reason", "detail", "semantic"),
                    Diagnostic(identity, "code", "reason", "other-detail", "semantic")
                };
                var variantAdmissions = variants.Select(offer => OfferDiagnosticUntilAccepted(dispatch, offer)).ToArray();
                Assert(variantAdmissions.All(item => item.Status == WatchdogRuntimeCallbackOfferStatus.Accepted),
                    "exact identity/coalesce/code/reason/detail变化被错误合并");
                Assert(WaitUntil(() => target.Calls == variants.Length + 1, 1000),
                    "diagnostic posting未覆盖queued/posting/executing key集合");
                target.ReleaseAll();
                var receipt = dispatch.CompleteAndDrain(TimeSpan.FromSeconds(3));
                Assert(receipt.IsTerminal && receipt.DiagnosticAccepted == variants.Length + 1,
                    "diagnostic accepted计数与exact key测试不一致");
                Assert(receipt.DiagnosticCoalesced == 1,
                    "diagnostic coalesce计数不正确");
                var firstCompletion = first.Completion.GetAwaiter().GetResult();
                var duplicateCompletion = duplicate.Completion.GetAwaiter().GetResult();
                Assert(firstCompletion.AdmissionSequence == first.AdmissionSequence &&
                       firstCompletion.CanonicalSequence == first.CanonicalSequence &&
                       duplicateCompletion.AdmissionSequence == duplicate.AdmissionSequence &&
                       duplicateCompletion.CanonicalSequence == duplicate.CanonicalSequence,
                    "coalesced completion的两类sequence未与admission对齐");
            }
        }

        private static void DiagnosticHeldBatchRespectsInflightFence()
        {
            using (var target = new ControlledPostTarget())
            using (var dispatch = new WatchdogRuntimeCallbackDispatch(
                       new WatchdogRuntimeCallbackDispatchOptions
                       {
                           DiagnosticPostTarget = target,
                           SafetyPostTarget = new WatchdogThreadPoolPostTarget()
                       }))
            {
                var admissions = new List<WatchdogRuntimeCallbackOfferResult>();
                for (var index = 0; index < 64; index++)
                    admissions.Add(dispatch.OfferDiagnostic(Diagnostic(
                        Identity("held-" + index, 1, 1, 1, 1, Guid.NewGuid()),
                        "held-" + index, "reason", "detail", "semantic-" + index)));
                Assert(admissions.All(item => item.Accepted),
                    "64个held diagnostic offer未全部accepted");
                Assert(WaitUntil(() => target.Calls == WatchdogRuntimeCallbackDispatch.DiagnosticMaxInflight, 2000),
                    "held diagnostic首批post数量不是32");
                Thread.Sleep(20);
                Assert(target.Calls == WatchdogRuntimeCallbackDispatch.DiagnosticMaxInflight,
                    "第33个diagnostic在首个completion释放前已post");
                target.ReleaseAllAndPassThrough();
                Assert(WaitUntil(() => target.Calls == 64, 2000),
                    "释放首批后剩余diagnostic未完成第二批post");
                var receipt = dispatch.CompleteAndDrain(TimeSpan.FromSeconds(3));
                Assert(receipt.IsTerminal && receipt.AllResourcesReleased &&
                       receipt.DiagnosticPostAccepted == 64 &&
                       receipt.DiagnosticInFlightRemaining == 0,
                    "64个diagnostic receipt未全部terminal");
            }
        }

        private static void CompletionKindsRemainDistinct()
        {
            using (var target = new ControlledPostTarget())
            using (var dispatch = new WatchdogRuntimeCallbackDispatch(
                       new WatchdogRuntimeCallbackDispatchOptions
                       {
                           SafetyPostTarget = target,
                           DiagnosticPostTarget = new WatchdogThreadPoolPostTarget()
                       }))
            {
                target.Enqueue(ScriptedPost.InfrastructureFault,
                               ScriptedPost.HandlerFault,
                               ScriptedPost.Canceled);
                var first = dispatch.OfferSafety(Offer(WatchdogRuntimeCallbackLane.Safety, "infra", "infra"));
                var second = dispatch.OfferSafety(Offer(WatchdogRuntimeCallbackLane.Safety, "handler", "handler"));
                var third = dispatch.OfferSafety(Offer(WatchdogRuntimeCallbackLane.Safety, "cancel", "cancel"));
                Assert(WaitUntil(() => first.Completion.IsCompleted &&
                                      second.Completion.IsCompleted && third.Completion.IsCompleted, 2000),
                    "三类completion未在有界时间内完成");
                var one = first.Completion.GetAwaiter().GetResult();
                var two = second.Completion.GetAwaiter().GetResult();
                var three = third.Completion.GetAwaiter().GetResult();
                Assert(one.Status == WatchdogCallbackCompletionStatus.PostRejected &&
                       one.Reason.IndexOf("PostInfrastructureFault", StringComparison.Ordinal) >= 0,
                    "post infrastructure fault未分类为PostRejected");
                Assert(two.Status == WatchdogCallbackCompletionStatus.HandlerFaulted &&
                       three.Status == WatchdogCallbackCompletionStatus.Canceled,
                    "handler fault/cancel未按completion状态区分");
                target.ReleaseAll();
                var receipt = dispatch.CompleteAndDrain(TimeSpan.FromSeconds(3));
                Assert(receipt.SafetyPostFaults >= 1 && receipt.SafetyHandlerFaults >= 1 &&
                       receipt.SafetyPostCanceled >= 1 &&
                       receipt.SafetyCallbackFailures >= 1,
                    "post/handler/cancel汇总计数缺失");
            }
        }

        private static void AdmissionRejectionsAreCountedByReason()
        {
            using (var safetyTarget = new ControlledPostTarget())
            using (var diagnosticTarget = new ControlledPostTarget())
            using (var dispatch = new WatchdogRuntimeCallbackDispatch(
                       new WatchdogRuntimeCallbackDispatchOptions
                       {
                           SafetyPostTarget = safetyTarget,
                           DiagnosticPostTarget = diagnosticTarget
                       }))
            {
                var invalidSafety = dispatch.OfferSafety(null);
                var invalidDiagnostic = dispatch.OfferDiagnostic(null);
                Assert(invalidSafety.Status == WatchdogRuntimeCallbackOfferStatus.RejectedInvalid &&
                       invalidDiagnostic.Status == WatchdogRuntimeCallbackOfferStatus.RejectedInvalid,
                    "invalid offer未按lane返回RejectedInvalid");
                for (var index = 0; index < WatchdogRuntimeCallbackDispatch.SafetyCapacity; index++)
                    Assert(dispatch.OfferSafety(Offer(
                        WatchdogRuntimeCallbackLane.Safety, "cap-" + index, "cap")).Accepted,
                        "safety capacity填充失败");
                var capacity = dispatch.OfferSafety(Offer(
                    WatchdogRuntimeCallbackLane.Safety, "capacity", "capacity"));
                Assert(capacity.Status == WatchdogRuntimeCallbackOfferStatus.RejectedSaturated,
                    "safety capacity rejection状态错误");

                var queueFullSeen = false;
                for (var index = 0; index < WatchdogRuntimeCallbackDispatch.DiagnosticCapacity +
                                             WatchdogRuntimeCallbackDispatch.DiagnosticMaxInflight + 1; index++)
                {
                    var result = OfferDiagnosticUntilAdmission(dispatch, Diagnostic(
                        Identity("reject-" + index, 1, 1, 1, 1, Guid.NewGuid()),
                        "reject-" + index, "reason", "detail", "semantic-" + index));
                    if (result.Status == WatchdogRuntimeCallbackOfferStatus.RejectedSaturated &&
                        string.Equals(result.RejectionReason, "DiagnosticQueueFull", StringComparison.Ordinal))
                        queueFullSeen = true;
                }
                Assert(queueFullSeen && dispatch.DiagnosticDroppedCount > 0,
                    "diagnostic queuefull未发生");
                diagnosticTarget.ReleaseAllAndPassThrough();
                safetyTarget.ReleaseAllAndPassThrough();
                var terminal = dispatch.CompleteAndDrain(TimeSpan.FromSeconds(3));
                Assert(terminal.IsTerminal, "rejection计数测试未收口");
                var closedSafety = dispatch.OfferSafety(Offer(
                    WatchdogRuntimeCallbackLane.Safety, "closed-safety", "closed"));
                var closedDiagnostic = dispatch.OfferDiagnostic(Diagnostic(
                    Identity("closed-diagnostic", 1, 1, 1, 1, Guid.NewGuid()),
                    "closed", "reason", "detail", "semantic"));
                Assert(closedSafety.Status == WatchdogRuntimeCallbackOfferStatus.RejectedClosed &&
                       closedDiagnostic.Status == WatchdogRuntimeCallbackOfferStatus.RejectedClosed,
                    "closing后offer未按lane返回RejectedClosed");
                Assert(terminal.SafetyRejected >= 2 && terminal.DiagnosticRejected >= 1 &&
                       terminal.DiagnosticDropped > 0,
                    "SafetyRejected/DiagnosticRejected/Dropped计数未按原因分离");
            }
        }

        private static void DiagnosticCannotBlockSafety()
        {
            using (var diagnosticTarget = new ControlledPostTarget())
            using (var dispatch = new WatchdogRuntimeCallbackDispatch(
                       new WatchdogRuntimeCallbackDispatchOptions
                       {
                           DiagnosticPostTarget = diagnosticTarget,
                           SafetyPostTarget = new WatchdogThreadPoolPostTarget()
                       }))
            {
                var diagnostic = dispatch.OfferDiagnostic(Diagnostic(
                    Identity("blocked", 1, 1, 1, 1, Guid.NewGuid()),
                    "diagnostic", "hold", "hold", "hold"));
                Assert(diagnostic.Accepted, "诊断held offer未接受");
                Assert(WaitUntil(() => diagnosticTarget.Calls >= 1, 1000),
                    "diagnostic test target未收到post");
                var stopwatch = Stopwatch.StartNew();
                var safety = dispatch.OfferSafety(Offer(
                    WatchdogRuntimeCallbackLane.Safety, "urgent", "urgent"));
                Assert(safety.Accepted && stopwatch.ElapsedMilliseconds < 200,
                    "诊断lane阻塞了safety admission");
                diagnosticTarget.ReleaseAll();
                var receipt = dispatch.CompleteAndDrain(TimeSpan.FromSeconds(3));
                Assert(receipt.IsTerminal && receipt.SafetyExecuted == 1,
                    "独立safety lane未完成");
            }
        }

        private static void SubscriberFailuresAreIsolated()
        {
            using (var target = new ControlledPostTarget())
            using (var dispatch = new WatchdogRuntimeCallbackDispatch(
                       new WatchdogRuntimeCallbackDispatchOptions
                       {
                           SafetyPostTarget = target,
                           DiagnosticPostTarget = target
                       }))
            {
                var spaceGood = new Gate();
                dispatch.SpaceAvailable += () => throw new InvalidOperationException("space observer");
                dispatch.SpaceAvailable += spaceGood.Hit;
                var auditGood = new Gate();
                dispatch.Audit += _ => throw new InvalidOperationException("audit observer");
                dispatch.Audit += _ => auditGood.Hit();
                var first = dispatch.OfferSafety(Offer(
                    WatchdogRuntimeCallbackLane.Safety, "space", "space"));
                Assert(first.Accepted, "space test offer未接受");
                Assert(WaitUntil(() => target.Calls >= 1, 1000),
                    "space test target未收到post");
                target.ReleaseAll();
                Assert(WaitUntil(() => Volatile.Read(ref spaceGood.Value) == 1, 2000),
                    "SpaceAvailable good subscriber被坏subscriber阻塞");

                for (var index = 0; index < WatchdogRuntimeCallbackDispatch.DiagnosticCapacity + 33; index++)
                    dispatch.OfferDiagnostic(Diagnostic(
                        Identity("audit-" + index, 1, 1, 1, 1, Guid.NewGuid()),
                        "drop-" + index, "reason", "detail", "semantic-" + index));
                Assert(WaitUntil(() => dispatch.DiagnosticDroppedCount >= 1, 3000),
                    "诊断队列未达到drop门槛，无法验证Audit隔离");
                Assert(WaitUntil(() => Volatile.Read(ref auditGood.Value) == 1, 3000),
                    "Audit good subscriber未被调度");
                var receipt = dispatch.CompleteAndDrain(TimeSpan.FromMilliseconds(5));
                target.ReleaseAllAndPassThrough();
                receipt = dispatch.CompleteAndDrain(TimeSpan.FromSeconds(3));
                Assert(receipt.SpaceAvailableFailureCount >= 1 && receipt.AuditFailureCount >= 1,
                    "Space/Audit坏subscriber异常计数缺失");
                Assert(receipt.AllObserversTerminal && receipt.AllResourcesReleased,
                    "Space/Audit observer未纳入drain/resource终态: terminal=" + receipt.IsTerminal +
                    ", observers=" + receipt.AllObserversTerminal +
                    ", resources=" + receipt.AllResourcesReleased +
                    ", workers=" + receipt.AllWorkersTerminal +
                    ", queues=" + receipt.SafetyQueueRemaining + "/" + receipt.DiagnosticQueueRemaining +
                    ", inFlight=" + receipt.InFlightRemaining +
                    ", producers=" + dispatch.ActiveOfferProducerCount +
                    ", lastFailure=" + receipt.LastFailureKind + ":" + receipt.LastFailureReason);
                Assert(receipt.WorkerExceptionCount == 0,
                    "subscriber异常污染dispatcher worker状态");
            }
        }

        private static void TimeoutCanContinueToTerminal()
        {
            using (var target = new ControlledPostTarget())
            using (var dispatch = new WatchdogRuntimeCallbackDispatch(
                       new WatchdogRuntimeCallbackDispatchOptions
                       {
                           SafetyPostTarget = target,
                           DiagnosticPostTarget = new WatchdogThreadPoolPostTarget()
                       }))
            {
                var admission = dispatch.OfferSafety(Offer(
                    WatchdogRuntimeCallbackLane.Safety, "timeout", "timeout"));
                Assert(admission.Accepted, "timeout offer未接受");
                var timedOut = dispatch.CompleteAndDrain(TimeSpan.FromMilliseconds(5));
                Assert(timedOut.TimedOut && !timedOut.IsTerminal && !timedOut.AllResourcesReleased,
                    "短deadline错误地缓存terminal或释放资源");
                target.ReleaseAllAndPassThrough();
                var terminal = dispatch.CompleteAndDrain(TimeSpan.FromSeconds(3));
                var repeated = dispatch.CompleteAndDrain(TimeSpan.FromMilliseconds(1));
                Assert(terminal.IsTerminal && terminal.AllResourcesReleased &&
                       terminal.AllObserversTerminal &&
                       ReferenceEquals(terminal, repeated),
                    "继续drain未得到稳定terminal receipt");
                Assert(terminal.AcceptingClosed && terminal.SafetyQueueRemaining == 0 &&
                       terminal.SafetyPostingRemaining == 0 && terminal.SafetyActiveRemaining == 0,
                    "terminal receipt未反映两阶段lane资源为零");
            }
        }

        private static WatchdogRuntimeCallbackOffer Offer(
            WatchdogRuntimeCallbackLane lane,
            string code,
            string detail)
        {
            return new WatchdogRuntimeCallbackOffer(
                Identity("run", 1, 1, 1, 1, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                lane,
                code,
                () => { },
                detail,
                code,
                "reason");
        }

        private static WatchdogRuntimeCallbackOffer Diagnostic(
            WatchdogRuntimeCallbackIdentity identity,
            string code,
            string reason,
            string detail,
            string semantic)
        {
            return new WatchdogRuntimeCallbackOffer(
                identity,
                WatchdogRuntimeCallbackLane.Diagnostic,
                code,
                () => { },
                detail,
                semantic,
                reason);
        }

        private static WatchdogRuntimeCallbackOfferResult OfferDiagnosticUntilAccepted(
            WatchdogRuntimeCallbackDispatch dispatch,
            WatchdogRuntimeCallbackOffer offer)
        {
            WatchdogRuntimeCallbackOfferResult last = null;
            for (var attempt = 0; attempt < 1000; attempt++)
            {
                last = dispatch.OfferDiagnostic(offer);
                if (last.Accepted) return last;
                if (last.Status != WatchdogRuntimeCallbackOfferStatus.RejectedSaturated ||
                    !string.Equals(last.RejectionReason, "DiagnosticGateBusy", StringComparison.Ordinal))
                    return last;
                Thread.Yield();
            }
            return last;
        }

        private static WatchdogRuntimeCallbackOfferResult OfferDiagnosticUntilAdmission(
            WatchdogRuntimeCallbackDispatch dispatch,
            WatchdogRuntimeCallbackOffer offer)
        {
            WatchdogRuntimeCallbackOfferResult last = null;
            for (var attempt = 0; attempt < 1000; attempt++)
            {
                last = dispatch.OfferDiagnostic(offer);
                if (last.Status != WatchdogRuntimeCallbackOfferStatus.RejectedSaturated ||
                    !string.Equals(last.RejectionReason, "DiagnosticGateBusy", StringComparison.Ordinal))
                    return last;
                Thread.Yield();
            }
            return last;
        }

        private static WatchdogRuntimeCallbackIdentity Identity(
            string session,
            long generation,
            long lease,
            long authority,
            long scopeGeneration,
            Guid scopeId)
        {
            return new WatchdogRuntimeCallbackIdentity(
                session, generation, lease, authority, scopeGeneration, scopeId);
        }

        private static bool WaitUntil(Func<bool> predicate, int timeoutMilliseconds)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!predicate())
            {
                if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds) return false;
                Thread.Sleep(1);
            }
            return true;
        }

        private static void Run(string name, Action test, ref int passed, List<string> failures)
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
                failures.Add(name + ": " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
