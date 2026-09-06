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
    /// Production coordinator seam. Only the dispatch post target is
    /// controlled; offer canonicalization, subscriber leases, pump, CAS and
    /// drain are the production implementations.
    /// </summary>
    internal static class WatchdogStopAllOfferCoordinatorProductionTests
    {
        private sealed class HeldPost
        {
            internal readonly TaskCompletionSource<object> Completion =
                new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly Func<Task> Callback;

            internal HeldPost(Func<Task> callback) { Callback = callback; }
        }

        private sealed class ControlledPostTarget : IWatchdogCallbackPostTarget, IDisposable
        {
            private readonly object _gate = new object();
            private readonly List<HeldPost> _held = new List<HeldPost>();
            private bool _passThrough;
            private bool _reject;
            private int _calls;

            internal int Calls => Volatile.Read(ref _calls);

            internal void RejectAll()
            {
                lock (_gate) _reject = true;
            }

            public WatchdogPostReceipt TryPost(Func<Task> callback)
            {
                bool pass;
                bool reject;
                HeldPost held = null;
                lock (_gate)
                {
                    pass = _passThrough;
                    reject = _reject;
                    if (!pass && !reject)
                    {
                        held = new HeldPost(callback);
                        _held.Add(held);
                    }
                }
                var sequence = Interlocked.Increment(ref _calls);
                if (reject) return new WatchdogPostReceipt(false, Task.CompletedTask, "ControlledReject", sequence);
                if (pass) return new WatchdogPostReceipt(true, Invoke(callback), string.Empty, sequence);
                return new WatchdogPostReceipt(true, held.Completion.Task, string.Empty, sequence);
            }

            internal void ReleaseAllAndPassThrough()
            {
                HeldPost[] held;
                lock (_gate)
                {
                    _passThrough = true;
                    _reject = false;
                    held = _held.ToArray();
                    _held.Clear();
                }
                foreach (var item in held) CompleteHeld(item);
            }

            public void Dispose() => ReleaseAllAndPassThrough();

            private static void CompleteHeld(HeldPost held)
            {
                try
                {
                    var task = held.Callback == null ? Task.CompletedTask : held.Callback();
                    if (task != null) task.GetAwaiter().GetResult();
                    held.Completion.TrySetResult(null);
                }
                catch (Exception ex) { held.Completion.TrySetException(ex); }
            }

            private static Task Invoke(Func<Task> callback)
            {
                try { return callback == null ? Task.CompletedTask : callback() ?? Task.CompletedTask; }
                catch (Exception ex) { return Task.FromException(ex); }
            }
        }

        /// <summary>
        /// A real single-thread SynchronizationContext seam.  TryPost only
        /// schedules the async callback; the returned completion is completed
        /// after the callback task (including captured-context continuations)
        /// reaches its terminal state.
        /// </summary>
        private sealed class SingleThreadSynchronizationContextPostTarget : IWatchdogCallbackPostTarget, IDisposable
        {
            private sealed class CallbackContext : SynchronizationContext
            {
                private readonly BlockingCollection<Action> _queue;

                internal CallbackContext(BlockingCollection<Action> queue) { _queue = queue; }

                public override void Post(SendOrPostCallback callback, object state)
                {
                    if (callback == null) return;
                    try { _queue.Add(() => callback(state)); }
                    catch (InvalidOperationException) { }
                }
            }

            private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
            private readonly CallbackContext _context;
            private readonly Thread _thread;
            private int _closed;
            private int _calls;
            private long _sequence;

            internal SingleThreadSynchronizationContextPostTarget()
            {
                _context = new CallbackContext(_queue);
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "Watchdog.Test.SingleThreadPostTarget"
                };
                _thread.Start();
            }

            internal int Calls => Volatile.Read(ref _calls);

            public WatchdogPostReceipt TryPost(Func<Task> callback)
            {
                if (callback == null) return WatchdogPostReceipt.Rejected("CallbackMissing");
                if (Volatile.Read(ref _closed) != 0) return WatchdogPostReceipt.Rejected("TargetClosed");
                var completion = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var sequence = Interlocked.Increment(ref _sequence);
                Interlocked.Increment(ref _calls);
                try
                {
                    _queue.Add(() => _ = RunPostedAsync(callback, completion));
                    return new WatchdogPostReceipt(true, completion.Task, string.Empty, sequence);
                }
                catch (InvalidOperationException)
                {
                    return WatchdogPostReceipt.Rejected("TargetClosed");
                }
            }

            private void Run()
            {
                SynchronizationContext.SetSynchronizationContext(_context);
                foreach (var callback in _queue.GetConsumingEnumerable())
                {
                    try { callback(); } catch { }
                }
            }

            private static async Task RunPostedAsync(
                Func<Task> callback,
                TaskCompletionSource<object> completion)
            {
                try
                {
                    var task = callback();
                    if (task != null) await task.ConfigureAwait(false);
                    completion.TrySetResult(null);
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                _queue.CompleteAdding();
                try { _thread.Join(3000); } catch { }
                _queue.Dispose();
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly ControlledPostTarget Target = new ControlledPostTarget();
            internal readonly WatchdogRuntimeCallbackDispatch Dispatcher;
            internal readonly WatchdogStopAllOfferCoordinator Coordinator;
            internal readonly WatchdogStopAllExactScopeIdentity Scope;
            internal readonly WatchdogStopAllScopeLease Lease;

            internal Fixture()
            {
                Dispatcher = new WatchdogRuntimeCallbackDispatch(new WatchdogRuntimeCallbackDispatchOptions
                {
                    SafetyPostTarget = Target,
                    DiagnosticPostTarget = new WatchdogThreadPoolPostTarget(),
                    DrainTimeout = TimeSpan.FromSeconds(3)
                });
                Coordinator = new WatchdogStopAllOfferCoordinator(Dispatcher);
                Scope = NewScope();
                Lease = Coordinator.Activate(Scope);
            }

            public void Dispose()
            {
                try
                {
                    Coordinator.BeginClose(Lease);
                    Target.ReleaseAllAndPassThrough();
                    Coordinator.Drain(Lease, TimeSpan.FromSeconds(3));
                }
                catch { }
                try { Coordinator.Dispose(); } catch { }
                try { Dispatcher.Dispose(); } catch { }
                Target.Dispose();
            }
        }

        internal static int RunAll()
        {
            var passed = 0;
            var failures = new List<string>();
            Run("three subscribers each post once", ThreeSubscribersEachPostOnce, ref passed, failures);
            Run("offer before register and late subscriber", OfferBeforeRegisterLateSubscriber, ref passed, failures);
            Run("dispose A preserves B and C takes over", DisposeAReservesBAndC, ref passed, failures);
            Run("late disposed A completion cannot mutate B generation", HeldDisposedACompletionDoesNotMutateB, ref passed, failures);
            Run("twenty subscribers capacity sixteen pumps four", TwentySubscribersCapacityPump, ref passed, failures);
            Run("BeginClose race does not drop pending", BeginCloseRacePreservesPending, ref passed, failures);
            Run("zero subscriber is failclosed and nonterminal", ZeroSubscriberFailClosed, ref passed, failures);
            Run("dual source canonical envelope reaches handler", DualSourceCanonicalEnvelope, ref passed, failures);
            Run("identity and activation CAS rejects ABA", IdentityAndActivationCas, ref passed, failures);
            Run("drain timeout can resume", DrainTimeoutCanResume, ref passed, failures);
            Run("sixty-four offers exactly once", SixtyFourOffersExactlyOnce, ref passed, failures);
            Run("handler fault is delivery state only", HandlerFaultIsDeliveryStateOnly, ref passed, failures);
            Run("post rejection is separate from offer result", PostRejectionIsSeparate, ref passed, failures);
            Run("concurrent same correlation coalesces", ConcurrentSameCorrelationCoalesces, ref passed, failures);
            Run("dispose is idempotent and unsubscribes", DisposeIsIdempotent, ref passed, failures);
            Run("async handler completion is real post completion", AsyncHandlerCompletionIsNonBlocking, ref passed, failures);
            if (failures.Count != 0)
                throw new InvalidOperationException("StopAll coordinator专项失败: " + string.Join("; ", failures));
            return passed;
        }

        private static void ThreeSubscribersEachPostOnce()
        {
            using (var fixture = new Fixture())
            {
                var counts = new int[3];
                var seen = new List<WatchdogStopAllOfferEnvelope>();
                var leases = new List<WatchdogStopAllRegistrationLease>();
                for (var i = 0; i < 3; i++)
                {
                    var index = i;
                    leases.Add(fixture.Coordinator.RegisterSafety(fixture.Lease, "S" + i, envelope =>
                    {
                        Interlocked.Increment(ref counts[index]);
                        lock (seen) seen.Add(envelope);
                        return Task.CompletedTask;
                    }));
                }
                Assert(leases.All(item => item.Accepted), "3个subscriber未全部注册");
                var offer = fixture.Coordinator.OfferPipe(fixture.Lease,
                    Envelope(fixture, "three", "STOP", WatchdogStopAllSourceFlags.Pipe, 11, 0));
                Assert(WaitUntil(() => fixture.Target.Calls == 3, 2000), "3个subscriber未各自进入dispatcher");
                var merged = fixture.Coordinator.OfferDurable(fixture.Lease,
                    Envelope(fixture, "three", "STOP", WatchdogStopAllSourceFlags.Durable, 0, 23));
                Assert(merged.Status == WatchdogStopAllOfferStatus.Coalesced && merged.Deliveries.Count == 3,
                    "Durable同corr未coalesce到3个delivery");
                fixture.Target.ReleaseAllAndPassThrough();
                fixture.Coordinator.BeginClose(fixture.Lease);
                var drain = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3));
                Assert(drain.IsTerminal && counts.All(item => item == 1), "3个subscriber未各执行一次");
                Assert(seen.Count == 3 && seen.All(item => item.SourceFlags == WatchdogStopAllSourceFlags.Both),
                    "held post执行时未读取双源canonical envelope");
                Assert(seen.All(item => item.Scope.SessionLease == 17),
                    "production target回调scope lease错误，不能把ActivationId当SessionLease");
                Assert(offer.Deliveries.Count == 3 && offer.Deliveries.All(item => item.ActivationId == 1),
                    "首个offer未返回3个独立registration或ActivationId不稳定");
            }
        }

        private static void OfferBeforeRegisterLateSubscriber()
        {
            using (var fixture = new Fixture())
            {
                var first = fixture.Coordinator.OfferPipe(fixture.Lease,
                    Envelope(fixture, "late", "STOP", WatchdogStopAllSourceFlags.Pipe, 1, 0));
                Assert(first.Status == WatchdogStopAllOfferStatus.RequiredSafetySubscriberMissing && first.Deliveries.Count == 0,
                    "无subscriber offer未保留RequiredSafetySubscriberMissing");
                fixture.Coordinator.BeginClose(fixture.Lease);
                var late = fixture.Coordinator.RegisterSafety(fixture.Lease, "late-subscriber", _ => Task.CompletedTask);
                Assert(late.Accepted, "seal前late subscriber未接管pending canonical");
                Assert(WaitUntil(() => fixture.Target.Calls == 1, 2000), "late subscriber未触发delivery");
                fixture.Target.ReleaseAllAndPassThrough();
                var drain = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3));
                Assert(drain.IsTerminal && drain.Snapshot.Sealed &&
                       drain.Snapshot.Registrations.Single().DeliveryState == WatchdogStopAllRegistrationState.Completed,
                    "late subscriber delivery未在seal后稳定完成");
            }
        }

        private static void DisposeAReservesBAndC()
        {
            using (var fixture = new Fixture())
            {
                var aCount = 0;
                var bCount = 0;
                var cCount = 0;
                var a = fixture.Coordinator.RegisterSafety(fixture.Lease, "A", _ => { Interlocked.Increment(ref aCount); return Task.CompletedTask; });
                var b = fixture.Coordinator.RegisterSafety(fixture.Lease, "B", _ => { Interlocked.Increment(ref bCount); return Task.CompletedTask; });
                fixture.Coordinator.OfferPipe(fixture.Lease, Envelope(fixture, "dispose", "STOP", WatchdogStopAllSourceFlags.Pipe, 1, 0));
                Assert(WaitUntil(() => fixture.Target.Calls == 2, 2000), "A/B delivery未进入held target");
                a.Dispose();
                var before = fixture.Coordinator.Capture(fixture.Lease);
                Assert(before.Subscribers.Single(item => item.SubscriberId == "B").Active && before.Registrations.Any(item => item.SubscriberId == "B"),
                    "Dispose A错误删除B");
                var c = fixture.Coordinator.RegisterSafety(fixture.Lease, "C", _ => { Interlocked.Increment(ref cCount); return Task.CompletedTask; });
                Assert(c.Accepted, "C未能在seal前接管canonical");
                Assert(WaitUntil(() => fixture.Target.Calls == 3, 2000), "C未获得新的delivery");
                fixture.Target.ReleaseAllAndPassThrough();
                fixture.Coordinator.BeginClose(fixture.Lease);
                var drain = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3));
                Assert(drain.IsTerminal, "Dispose A场景未收口");
                Assert(aCount == 0 && bCount == 1 && cCount == 1, "A/B/C handler执行次数错误");
                Assert(b.IsActive && c.IsActive, "B/C lease被A Dispose污染");
                Assert(drain.Snapshot.Registrations.Single(item => item.SubscriberId == "A").DeliveryState ==
                       WatchdogStopAllRegistrationState.SkippedDisposed,
                    "A已Dispose但迟到completion未稳定为SkippedDisposed");
            }
        }

        private static void TwentySubscribersCapacityPump()
        {
            using (var fixture = new Fixture())
            {
                var counts = new int[20];
                for (var i = 0; i < counts.Length; i++)
                {
                    var index = i;
                    Assert(fixture.Coordinator.RegisterSafety(fixture.Lease, "S" + i, _ => { Interlocked.Increment(ref counts[index]); return Task.CompletedTask; }).Accepted,
                        "20 subscriber注册失败");
                }
                fixture.Coordinator.OfferPipe(fixture.Lease, Envelope(fixture, "capacity", "STOP", WatchdogStopAllSourceFlags.Pipe, 1, 0));
                Assert(WaitUntil(() => fixture.Target.Calls == WatchdogRuntimeCallbackDispatch.SafetyCapacity, 3000), "首轮dispatcher未达到16 capacity");
                Assert(WaitUntil(() => fixture.Coordinator.Capture(fixture.Lease).Registrations.Count(item => item.DeliveryState == WatchdogStopAllRegistrationState.Pending) == 4, 3000),
                    "capacity16后未保留4个Pending delivery; calls=" + fixture.Target.Calls + "; states=" +
                    string.Join(",", fixture.Coordinator.Capture(fixture.Lease).Registrations.GroupBy(item => item.DeliveryState).Select(item => item.Key + "=" + item.Count())));
                fixture.Target.ReleaseAllAndPassThrough();
                Assert(WaitUntil(() => fixture.Target.Calls == 20, 3000), "SpaceAvailable未自动泵出4个Pending");
                fixture.Coordinator.BeginClose(fixture.Lease);
                var drain = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3));
                Assert(drain.IsTerminal && counts.All(item => item == 1), "20 subscriber未各执行一次");
                var terminalGeneration = drain.Snapshot.PumpGeneration;
                Assert(!drain.Snapshot.PumpActive, "terminal receipt前Pump仍active");
                // Release can raise the dispatcher's final SpaceAvailable
                // notification after the coordinator has published terminal.
                fixture.Target.ReleaseAllAndPassThrough();
                Assert(WaitUntil(() => !fixture.Coordinator.Capture(fixture.Lease).PumpActive, 500),
                    "terminal后Pump重新active");
                var afterFinalSpace = fixture.Coordinator.Capture(fixture.Lease);
                Assert(afterFinalSpace.PumpGeneration == terminalGeneration && !afterFinalSpace.PumpActive,
                    "terminal后SpaceAvailable错误创建新Pump generation");
            }
        }

        private static void BeginCloseRacePreservesPending()
        {
            using (var fixture = new Fixture())
            {
                var count = 0;
                Assert(fixture.Coordinator.RegisterSafety(fixture.Lease, "race", _ => { Interlocked.Increment(ref count); return Task.CompletedTask; }).Accepted, "race subscriber注册失败");
                var offers = new WatchdogStopAllOfferResult[32];
                Parallel.For(0, offers.Length, index => offers[index] = fixture.Coordinator.OfferPipe(fixture.Lease,
                    Envelope(fixture, "race-" + index, "STOP", WatchdogStopAllSourceFlags.Pipe, index + 1, 0)));
                var close = fixture.Coordinator.BeginClose(fixture.Lease);
                Assert(close.Accepted, "BeginClose race未成功发布closing");
                var before = fixture.Coordinator.Capture(fixture.Lease);
                Assert(before.Registrations.Count == 32 &&
                       !before.Registrations.Any(item => item.DeliveryState == WatchdogStopAllRegistrationState.Canceled ||
                                                         item.DeliveryState == WatchdogStopAllRegistrationState.SkippedDisposed),
                    "BeginClose race丢失或关闭了既有registration");
                var first = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromMilliseconds(5));
                Assert(first.TimedOut || first.IsTerminal, "首轮Drain结果非法");
                fixture.Target.ReleaseAllAndPassThrough();
                var second = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(5));
                Assert(second.IsTerminal && count == 32 && second.Snapshot.Registrations.Count == 32,
                    "BeginClose race丢失pending offer; count=" + count + "; calls=" + fixture.Target.Calls + "; regs=" + second.Snapshot.Registrations.Count +
                    "; states=" + string.Join(",", second.Snapshot.Registrations.GroupBy(item => item.DeliveryState).Select(item => item.Key + "=" + item.Count())));
                var late = fixture.Coordinator.OfferPipe(fixture.Lease, Envelope(fixture, "after-close", "STOP", WatchdogStopAllSourceFlags.Pipe, 1, 0));
                Assert(late.Status == WatchdogStopAllOfferStatus.Closed, "closing后仍接受新offer");
            }
        }

        private static void ZeroSubscriberFailClosed()
        {
            using (var fixture = new Fixture())
            {
                var offer = fixture.Coordinator.OfferPipe(fixture.Lease, Envelope(fixture, "no-safety", "STOP", WatchdogStopAllSourceFlags.Pipe, 1, 0));
                Assert(offer.Status == WatchdogStopAllOfferStatus.RequiredSafetySubscriberMissing, "0 subscriber未显式报告缺失");
                fixture.Coordinator.BeginClose(fixture.Lease);
                var drain = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromMilliseconds(20));
                Assert(!drain.IsTerminal && drain.FailClosed && drain.Snapshot.FailureReason == "RequiredSafetySubscriberMissing" && drain.Snapshot.Registrations.Count == 0,
                    "0 subscriber未failclosed且保持nonterminal");
                var late = fixture.Coordinator.RegisterSafety(fixture.Lease, "too-late", _ => Task.CompletedTask);
                Assert(!late.Accepted && late.RejectionReason == "RegistrationsSealed", "seal后late subscriber错误接管");
            }
        }

        private static void DualSourceCanonicalEnvelope()
        {
            using (var fixture = new Fixture())
            {
                WatchdogStopAllOfferEnvelope seen = null;
                Assert(fixture.Coordinator.RegisterSafety(fixture.Lease, "canonical", envelope => { seen = envelope; return Task.CompletedTask; }).Accepted, "canonical subscriber注册失败");
                var beforeForged = fixture.Coordinator.Capture(fixture.Lease);
                var forgedBoth = fixture.Coordinator.OfferPipe(fixture.Lease,
                    Envelope(fixture, "forged-both", "STOP", WatchdogStopAllSourceFlags.Both, 40, 98));
                var afterForged = fixture.Coordinator.Capture(fixture.Lease);
                Assert(forgedBoth.Status == WatchdogStopAllOfferStatus.Invalid &&
                       forgedBoth.Reason == "SourceFlagsMustBeSingleSource" &&
                       beforeForged.Revision == afterForged.Revision &&
                       beforeForged.CanonicalRequests.Count == afterForged.CanonicalRequests.Count,
                    "OfferPipe错误接受外部Both或改变canonical/version");
                fixture.Coordinator.OfferPipe(fixture.Lease, Envelope(fixture, "both", "STOP", WatchdogStopAllSourceFlags.Pipe, 41, 0));
                Assert(WaitUntil(() => fixture.Target.Calls == 1, 2000), "Pipe canonical未post");
                var beforeDurableForged = fixture.Coordinator.Capture(fixture.Lease);
                var invalidDurableBoth = fixture.Coordinator.OfferDurable(fixture.Lease,
                    Envelope(fixture, "both", "STOP", WatchdogStopAllSourceFlags.Both, 999, 999));
                var afterDurableForged = fixture.Coordinator.Capture(fixture.Lease);
                var forgedBaseline = beforeDurableForged.CanonicalRequests.Single();
                var forgedAfter = afterDurableForged.CanonicalRequests.Single();
                Assert(invalidDurableBoth.Status == WatchdogStopAllOfferStatus.Invalid &&
                       invalidDurableBoth.Reason == "SourceFlagsMustBeSingleSource" &&
                       beforeDurableForged.Revision == afterDurableForged.Revision &&
                       forgedBaseline.SourceFlags == forgedAfter.SourceFlags &&
                       forgedBaseline.PipeSequence == forgedAfter.PipeSequence &&
                       forgedBaseline.DurableVersion == forgedAfter.DurableVersion &&
                       forgedBaseline.CanonicalSequence == forgedAfter.CanonicalSequence,
                    "OfferDurable错误接受外部Both或改变pipe/durable canonical版本");
                fixture.Coordinator.OfferDurable(fixture.Lease, Envelope(fixture, "both", "STOP", WatchdogStopAllSourceFlags.Durable, 0, 99));
                fixture.Target.ReleaseAllAndPassThrough();
                fixture.Coordinator.BeginClose(fixture.Lease);
                Assert(fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3)).IsTerminal && seen != null, "双源canonical未完成");
                Assert(seen.SourceFlags == WatchdogStopAllSourceFlags.Both && seen.PipeSequence == 41 && seen.DurableVersion == 99,
                    "handler未看到合并后的SourceFlags/sequence/version");
                var canonical = fixture.Coordinator.Capture(fixture.Lease).CanonicalRequests.Single();
                Assert(canonical.SourceFlags == WatchdogStopAllSourceFlags.Both && canonical.Envelope.SourceFlags == WatchdogStopAllSourceFlags.Both,
                    "Capture canonical状态与handler不一致");
            }
        }

        private static void IdentityAndActivationCas()
        {
            using (var fixture = new Fixture())
            {
                var before = fixture.Coordinator.Capture(fixture.Lease);
                var wrong = new WatchdogStopAllExactScopeIdentity(fixture.Scope.RunId, fixture.Scope.RunEpoch, fixture.Scope.SessionId,
                    fixture.Scope.SessionGeneration, fixture.Scope.SessionLease + 1, fixture.Scope.AuthorityGeneration,
                    fixture.Scope.ScopeGeneration, fixture.Scope.OwnerKind, fixture.Scope.ChannelGroup, fixture.Scope.Channel,
                    fixture.Scope.TargetPhase, fixture.Scope.ResourceScope, fixture.Scope.ScopeId, fixture.Scope.ProcessId,
                    fixture.Scope.ProcessStartTicks, fixture.Scope.AttachEpoch, fixture.Scope.IncidentId);
                var invalid = new WatchdogStopAllOfferEnvelope(wrong, fixture.Lease, "aba", "STOP", "wrong", WatchdogStopAllSourceFlags.Pipe, 1, 0);
                var result = fixture.Coordinator.OfferPipe(fixture.Lease, invalid);
                var after = fixture.Coordinator.Capture(fixture.Lease);
                Assert(result.Status == WatchdogStopAllOfferStatus.Invalid && before.CanonicalRequests.Count == after.CanonicalRequests.Count, "SessionLease identity mismatch未拒绝");
                Assert(fixture.Coordinator.RegisterSafety(fixture.Lease, "aba", _ => Task.CompletedTask).Accepted, "ABA subscriber注册失败");
                var accepted = fixture.Coordinator.OfferPipe(fixture.Lease, Envelope(fixture, "aba", "STOP", WatchdogStopAllSourceFlags.Pipe, 1, 0));
                Assert(WaitUntil(() => fixture.Target.Calls == 1, 2000), "ABA delivery未post");
                var posted = fixture.Coordinator.Capture(fixture.Lease).Registrations.Single();
                var expectedDispatcherIdentity = new WatchdogRuntimeCallbackIdentity(
                    fixture.Scope.SessionId, fixture.Scope.SessionGeneration, fixture.Scope.SessionLease,
                    fixture.Scope.AuthorityGeneration, fixture.Scope.ScopeGeneration, fixture.Scope.ScopeId);
                Assert(accepted.Deliveries.Count == 1 && posted.ActivationId == fixture.Lease.ActivationId &&
                       posted.DeliveryGeneration > 0 && posted.AdmissionSequence > 0 &&
                       posted.RequestCanonicalSequence == accepted.CanonicalSequence &&
                       posted.RequestRevision == accepted.CanonicalSequence &&
                       posted.DispatcherCanonicalSequence > 0 &&
                       posted.DispatcherIdentity != null &&
                       posted.DispatcherIdentity.SessionLease == 17 &&
                       posted.DispatcherIdentity.SessionLease != posted.ActivationId &&
                       posted.DispatcherIdentityExactKey == expectedDispatcherIdentity.ExactKey &&
                       posted.CanonicalSequence == accepted.CanonicalSequence &&
                       posted.DeliveryState == WatchdogStopAllRegistrationState.Posted,
                    "delivery CAS/dispatcher offer identity或两侧canonical sequence未冻结");
                fixture.Target.ReleaseAllAndPassThrough();
                fixture.Coordinator.BeginClose(fixture.Lease);
                Assert(fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3)).IsTerminal, "ABA场景未收口");
            }
        }

        private static void HeldDisposedACompletionDoesNotMutateB()
        {
            using (var fixture = new Fixture())
            {
                var aCount = 0;
                var bCount = 0;
                var a = fixture.Coordinator.RegisterSafety(fixture.Lease, "held-A", _ =>
                {
                    Interlocked.Increment(ref aCount);
                    return Task.CompletedTask;
                });
                Assert(a.Accepted, "held A subscriber注册失败");
                fixture.Coordinator.OfferPipe(fixture.Lease,
                    Envelope(fixture, "held-generation", "STOP", WatchdogStopAllSourceFlags.Pipe, 7, 0));
                Assert(WaitUntil(() => fixture.Target.Calls == 1, 2000), "A未进入held target");
                a.Dispose();

                var b = fixture.Coordinator.RegisterSafety(fixture.Lease, "held-B", _ =>
                {
                    Interlocked.Increment(ref bCount);
                    return Task.CompletedTask;
                });
                Assert(b.Accepted, "A Dispose后B未能注册");
                Assert(WaitUntil(() => fixture.Target.Calls == 2, 2000), "B未获得新的dispatcher delivery");
                var beforeRelease = fixture.Coordinator.Capture(fixture.Lease);
                var bBefore = beforeRelease.Registrations.Single(item => item.SubscriberId == "held-B");
                var canonicalBefore = beforeRelease.CanonicalRequests.Single();

                fixture.Target.ReleaseAllAndPassThrough();
                fixture.Coordinator.BeginClose(fixture.Lease);
                var drain = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3));
                var final = fixture.Coordinator.Capture(fixture.Lease);
                var aAfter = final.Registrations.Single(item => item.SubscriberId == "held-A");
                var bAfter = final.Registrations.Single(item => item.SubscriberId == "held-B");
                var canonicalAfter = final.CanonicalRequests.Single();
                Assert(drain.IsTerminal && aCount == 0 && bCount == 1 &&
                       aAfter.DeliveryState == WatchdogStopAllRegistrationState.SkippedDisposed &&
                       bAfter.DeliveryState == WatchdogStopAllRegistrationState.Completed,
                    "A迟到completion或B delivery终态错误");
                Assert(bAfter.ActivationId == bBefore.ActivationId &&
                       bAfter.DeliveryGeneration == bBefore.DeliveryGeneration &&
                       bAfter.AdmissionSequence == bBefore.AdmissionSequence &&
                       bAfter.RequestCanonicalSequence == bBefore.RequestCanonicalSequence &&
                       bAfter.RequestRevision == bBefore.RequestRevision &&
                       bAfter.DispatcherCanonicalSequence == bBefore.DispatcherCanonicalSequence &&
                       bAfter.DispatcherIdentityExactKey == bBefore.DispatcherIdentityExactKey,
                    "A旧completion污染B的identity/sequence");
                Assert(canonicalAfter.CorrelationId == canonicalBefore.CorrelationId &&
                       canonicalAfter.SourceFlags == canonicalBefore.SourceFlags &&
                       canonicalAfter.PipeSequence == canonicalBefore.PipeSequence &&
                       canonicalAfter.DurableVersion == canonicalBefore.DurableVersion &&
                       canonicalAfter.CanonicalSequence == canonicalBefore.CanonicalSequence &&
                       final.CanonicalRequests.Count == 1,
                    "A旧completion改变canonical request或创建第二canonical");
            }
        }

        private static void DrainTimeoutCanResume()
        {
            using (var fixture = new Fixture())
            {
                Assert(fixture.Coordinator.RegisterSafety(fixture.Lease, "timeout", _ => Task.CompletedTask).Accepted, "timeout subscriber注册失败");
                var result = fixture.Coordinator.OfferPipe(fixture.Lease, Envelope(fixture, "timeout", "STOP", WatchdogStopAllSourceFlags.Pipe, 1, 0));
                Assert(WaitUntil(() => fixture.Target.Calls == 1, 2000), "timeout offer未post");
                fixture.Coordinator.BeginClose(fixture.Lease);
                var first = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromMilliseconds(5));
                Assert(!first.IsTerminal && first.TimedOut, "首轮Drain未保留timeout状态");
                fixture.Target.ReleaseAllAndPassThrough();
                var second = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3));
                Assert(second.IsTerminal && second.AllResourcesReleased && result.Deliveries.Single().Completion.GetAwaiter().GetResult() != null, "timeout后第二次Drain未完成同一delivery");
            }
        }

        private static void SixtyFourOffersExactlyOnce()
        {
            using (var fixture = new Fixture())
            {
                var count = 0;
                Assert(fixture.Coordinator.RegisterSafety(fixture.Lease, "64", _ => { Interlocked.Increment(ref count); return Task.CompletedTask; }).Accepted, "64 subscriber注册失败");
                var results = new WatchdogStopAllOfferResult[64];
                Parallel.For(0, results.Length, index => results[index] = fixture.Coordinator.OfferPipe(fixture.Lease,
                    Envelope(fixture, "offer-" + index, "STOP", WatchdogStopAllSourceFlags.Pipe, index + 1, 0)));
                Assert(WaitUntil(() => fixture.Target.Calls == WatchdogRuntimeCallbackDispatch.SafetyCapacity, 3000), "64 offer未达到首轮capacity");
                fixture.Target.ReleaseAllAndPassThrough();
                Assert(WaitUntil(() => fixture.Target.Calls == 64 && count == 64, 5000),
                    "64 offer未各执行恰一次; count=" + count + "; calls=" + fixture.Target.Calls + "; states=" +
                    string.Join(",", fixture.Coordinator.Capture(fixture.Lease).Registrations.GroupBy(item => item.DeliveryState).Select(item => item.Key + "=" + item.Count())));
                fixture.Coordinator.BeginClose(fixture.Lease);
                var drain = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(5));
                Assert(drain.IsTerminal && drain.Snapshot.Registrations.Count == 64 && drain.Snapshot.Registrations.All(item => item.DeliveryState == WatchdogStopAllRegistrationState.Completed), "64 offer终态/注册数错误");
            }
        }

        private static void HandlerFaultIsDeliveryStateOnly()
        {
            using (var fixture = new Fixture())
            {
                Assert(fixture.Coordinator.RegisterSafety(fixture.Lease, "fault", _ => Task.FromException(new InvalidOperationException("fault"))).Accepted, "fault subscriber注册失败");
                var offer = fixture.Coordinator.OfferPipe(fixture.Lease, Envelope(fixture, "fault", "STOP", WatchdogStopAllSourceFlags.Pipe, 1, 0));
                Assert(WaitUntil(() => fixture.Target.Calls == 1, 2000), "fault offer未post");
                fixture.Target.ReleaseAllAndPassThrough();
                fixture.Coordinator.BeginClose(fixture.Lease);
                var drain = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3));
                var delivery = fixture.Coordinator.Capture(fixture.Lease).Registrations.Single();
                Assert(drain.IsTerminal && offer.Status != WatchdogStopAllOfferStatus.Rejected && delivery.DeliveryState == WatchdogStopAllRegistrationState.HandlerFaulted, "handler fault未落在delivery state");
            }
        }

        private static void PostRejectionIsSeparate()
        {
            using (var fixture = new Fixture())
            {
                Assert(fixture.Coordinator.RegisterSafety(fixture.Lease, "reject", _ => Task.CompletedTask).Accepted, "reject subscriber注册失败");
                fixture.Target.RejectAll();
                var offer = fixture.Coordinator.OfferPipe(fixture.Lease, Envelope(fixture, "reject", "STOP", WatchdogStopAllSourceFlags.Pipe, 1, 0));
                Assert(WaitUntil(() => fixture.Coordinator.Capture(fixture.Lease).Registrations.Single().CompletionTerminal, 2000), "post rejection未完成delivery");
                var delivery = fixture.Coordinator.Capture(fixture.Lease).Registrations.Single();
                Assert(offer.Status == WatchdogStopAllOfferStatus.Pending && delivery.DeliveryState == WatchdogStopAllRegistrationState.PostRejected, "PostRejected错误混入offer状态");
                fixture.Coordinator.BeginClose(fixture.Lease);
                Assert(fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3)).IsTerminal, "post rejection未收口");
            }
        }

        private static void ConcurrentSameCorrelationCoalesces()
        {
            using (var fixture = new Fixture())
            {
                var counts = new int[3];
                for (var i = 0; i < counts.Length; i++)
                {
                    var index = i;
                    Assert(fixture.Coordinator.RegisterSafety(fixture.Lease, "same-" + i, _ => { Interlocked.Increment(ref counts[index]); return Task.CompletedTask; }).Accepted, "same corr subscriber注册失败");
                }
                var results = new WatchdogStopAllOfferResult[64];
                Parallel.For(0, results.Length, index => results[index] = fixture.Coordinator.OfferPipe(fixture.Lease,
                    Envelope(fixture, "same", "STOP", WatchdogStopAllSourceFlags.Pipe, index + 1, 0)));
                Assert(WaitUntil(() => fixture.Target.Calls == 3, 3000), "同corr未收敛为3个delivery");
                fixture.Target.ReleaseAllAndPassThrough();
                fixture.Coordinator.BeginClose(fixture.Lease);
                var drain = fixture.Coordinator.Drain(fixture.Lease, TimeSpan.FromSeconds(3));
                Assert(drain.IsTerminal && counts.All(item => item == 1) && fixture.Coordinator.Capture(fixture.Lease).CanonicalRequests.Count == 1, "同corr并发未coalesce且各subscriber恰一次");
            }
        }

        private static void DisposeIsIdempotent()
        {
            using (var fixture = new Fixture())
            {
                var lease = fixture.Coordinator.RegisterSafety(fixture.Lease, "dispose", _ => Task.CompletedTask);
                lease.Dispose();
                lease.Dispose();
                var snapshot = fixture.Coordinator.Capture(fixture.Lease);
                Assert(snapshot.ActiveSubscriberCount == 0 && snapshot.Subscribers.Single().Active == false, "subscriber lease Dispose非幂等或未精确撤销");
                var noOfferBefore = fixture.Coordinator.Capture(fixture.Lease);
                var activeLease = fixture.Coordinator.RegisterSafety(fixture.Lease, "coordinator-owned", _ => Task.CompletedTask);
                var noOfferAfter = fixture.Coordinator.Capture(fixture.Lease);
                Assert(activeLease.IsActive && !noOfferAfter.PumpActive &&
                       noOfferBefore.PumpGeneration == noOfferAfter.PumpGeneration,
                    "无Pending registration时错误创建RequestPump");
                fixture.Coordinator.Dispose();
                fixture.Coordinator.Dispose();
                var disposed = fixture.Coordinator.Capture(fixture.Lease);
                Assert(disposed.Disposed && !activeLease.IsActive &&
                       disposed.Subscribers.All(item => !item.Active),
                    "Coordinator Dispose未幂等停用所有SubscriberRecord/lease");
            }
        }

        private static void AsyncHandlerCompletionIsNonBlocking()
        {
            using (var target = new SingleThreadSynchronizationContextPostTarget())
            using (var dispatcher = new WatchdogRuntimeCallbackDispatch(
                       new WatchdogRuntimeCallbackDispatchOptions
                       {
                           SafetyPostTarget = target,
                           DiagnosticPostTarget = new WatchdogThreadPoolPostTarget(),
                           DrainTimeout = TimeSpan.FromSeconds(3)
                       }))
            using (var coordinator = new WatchdogStopAllOfferCoordinator(dispatcher))
            {
                var scope = NewScope();
                var lease = coordinator.Activate(scope);
                var release = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var started = new int[3];
                for (var index = 0; index < started.Length; index++)
                {
                    var handlerIndex = index;
                    var registration = coordinator.RegisterSafety(lease, "async-" + index,
                        async envelope =>
                        {
                            Interlocked.Increment(ref started[handlerIndex]);
                            await Task.Yield();
                            await release.Task;
                            if (handlerIndex == 1)
                                throw new InvalidOperationException("async handler fault");
                            if (handlerIndex == 2)
                                throw new OperationCanceledException("async handler canceled");
                        });
                    Assert(registration.Accepted, "async subscriber注册失败: " + handlerIndex);
                }

                var stopwatch = Stopwatch.StartNew();
                var offer = coordinator.OfferPipe(lease,
                    Envelope(scope, lease, "async-real-task", "STOP",
                        WatchdogStopAllSourceFlags.Pipe, 1, 0));
                stopwatch.Stop();
                Assert(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
                    "OfferPipe被异步handler阻塞: " + stopwatch.Elapsed);
                Assert(offer.Deliveries.Count == 3, "async offer未生成3个delivery");
                Assert(WaitUntil(() => target.Calls == 3 && started.All(item => item == 1), 3000),
                    "3个async handler未全部post/start");
                Assert(offer.Deliveries.All(item => !item.Completion.IsCompleted),
                    "PostTarget completion未等待真实handler task结束");

                coordinator.BeginClose(lease);
                var timed = coordinator.Drain(lease, TimeSpan.FromMilliseconds(10));
                Assert(timed.TimedOut && !timed.IsTerminal,
                    "受控async task未使首轮drain保持非terminal");

                release.TrySetResult(null);
                var terminal = coordinator.Drain(lease, TimeSpan.FromSeconds(3));
                Assert(terminal.IsTerminal, "async handler释放后未terminal");
                var receipts = offer.Deliveries.Select(item => item.Completion.GetAwaiter().GetResult()).ToArray();
                Assert(receipts.Count(item => item.Status == WatchdogCallbackCompletionStatus.Completed) == 1 &&
                       receipts.Count(item => item.Status == WatchdogCallbackCompletionStatus.HandlerFaulted) == 1 &&
                       receipts.Count(item => item.Status == WatchdogCallbackCompletionStatus.Canceled) == 1,
                    "async handler completion未准确区分completed/faulted/canceled: " +
                    string.Join(",", receipts.Select(item => item.Status.ToString())));
                var states = terminal.Snapshot.Registrations.Select(item => item.DeliveryState).ToArray();
                Assert(states.Count(item => item == WatchdogStopAllRegistrationState.Completed) == 1 &&
                       states.Count(item => item == WatchdogStopAllRegistrationState.HandlerFaulted) == 1 &&
                       states.Count(item => item == WatchdogStopAllRegistrationState.Canceled) == 1,
                    "coordinator delivery state未与真实async completion对齐");
            }
        }

        private static WatchdogStopAllOfferEnvelope Envelope(Fixture fixture, string correlation, string reason,
            WatchdogStopAllSourceFlags source, long pipeSequence, long durableVersion, string requestId = null)
        {
            return new WatchdogStopAllOfferEnvelope(fixture.Scope, fixture.Lease, correlation, reason, "stop-all",
                source, pipeSequence, durableVersion, requestId);
        }

        private static WatchdogStopAllOfferEnvelope Envelope(
            WatchdogStopAllExactScopeIdentity scope,
            WatchdogStopAllScopeLease lease,
            string correlation,
            string reason,
            WatchdogStopAllSourceFlags source,
            long pipeSequence,
            long durableVersion)
        {
            return new WatchdogStopAllOfferEnvelope(scope, lease, correlation, reason, "stop-all",
                source, pipeSequence, durableVersion, correlation);
        }

        private static WatchdogStopAllExactScopeIdentity NewScope()
        {
            return new WatchdogStopAllExactScopeIdentity(
                "run-10358-029", 22, "session-10358", 3, 17, 7, 11,
                "UnattendedStop", "EPB-G1", "EPB10", "StopAll", "DAQ:Dev1",
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 145361, 638600000000000000, 9,
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        }

        private static bool WaitUntil(Func<bool> predicate, int timeoutMilliseconds)
        {
            var started = Environment.TickCount;
            while (!predicate())
            {
                if (unchecked(Environment.TickCount - started) >= timeoutMilliseconds) return false;
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
                failures.Add(name + ": " + ex.Message);
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
