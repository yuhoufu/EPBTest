using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Client;
using MTEmbTest;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Direct production-pipeline acceptance tests.  The only injected
    /// boundary is the callback post target; all context, ingress, target
    /// bind, coordinator, advance, admission, canonicalization and close
    /// behavior is executed by WatchdogRuntime's production core.
    /// </summary>
    internal static class WatchdogRuntimeCallbackPipelineProductionTests
    {
        private sealed class ImmediatePostTarget : IWatchdogCallbackPostTarget, IDisposable
        {
            private int _closed;
            private long _sequence;
            private int _calls;

            internal int Calls => Volatile.Read(ref _calls);

            public WatchdogPostReceipt TryPost(Func<Task> callback)
            {
                if (callback == null) return WatchdogPostReceipt.Rejected("CallbackMissing");
                if (Volatile.Read(ref _closed) != 0)
                    return WatchdogPostReceipt.Rejected("TargetClosed");
                Interlocked.Increment(ref _calls);
                var sequence = Interlocked.Increment(ref _sequence);
                try
                {
                    var completion = Task.Run(async () =>
                    {
                        var task = callback();
                        if (task != null) await task.ConfigureAwait(false);
                    });
                    return new WatchdogPostReceipt(true, completion, string.Empty, sequence);
                }
                catch (Exception ex)
                {
                    return WatchdogPostReceipt.Rejected("PostInfrastructureFault:" + ex.GetType().Name);
                }
            }

            public void Dispose() => Interlocked.Exchange(ref _closed, 1);
        }

        /// <summary>
        /// Holds accepted post callbacks until the test has published both
        /// evidence sources.  This is still only an injected I/O boundary;
        /// all dispatch/coordinator/canonical logic remains production code.
        /// </summary>
        private sealed class DeferredPostTarget : IWatchdogCallbackPostTarget, IDisposable
        {
            private sealed class PendingPost
            {
                internal readonly Func<Task> Callback;
                internal readonly TaskCompletionSource<bool> Completion =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                internal PendingPost(Func<Task> callback) { Callback = callback; }
            }

            private readonly ConcurrentQueue<PendingPost> _pending = new ConcurrentQueue<PendingPost>();
            private int _closed;
            private long _sequence;
            private int _calls;

            internal int Calls => Volatile.Read(ref _calls);

            public WatchdogPostReceipt TryPost(Func<Task> callback)
            {
                if (callback == null) return WatchdogPostReceipt.Rejected("CallbackMissing");
                if (Volatile.Read(ref _closed) != 0)
                    return WatchdogPostReceipt.Rejected("TargetClosed");
                var pending = new PendingPost(callback);
                _pending.Enqueue(pending);
                Interlocked.Increment(ref _calls);
                return new WatchdogPostReceipt(true, pending.Completion.Task, string.Empty,
                    Interlocked.Increment(ref _sequence));
            }

            internal int ReleaseAll()
            {
                var released = 0;
                while (_pending.TryDequeue(out var pending))
                {
                    released++;
                    Task.Run(async () =>
                    {
                        try
                        {
                            var callback = pending.Callback();
                            if (callback != null) await callback.ConfigureAwait(false);
                            pending.Completion.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            pending.Completion.TrySetException(ex);
                        }
                    });
                }
                return released;
            }

            public void Dispose() => Interlocked.Exchange(ref _closed, 1);
        }

        internal static int RunAll()
        {
            var passed = 0;
            var failures = new List<string>();
            Run("永久FailClosed不产生重试代次", PermanentFailClosedHasNoRetry,
                ref passed, failures);
            Run("双源并发reservation冲突最终收敛", DualSourceReservationCollisionConverges,
                ref passed, failures);
            Run("ActivateAndTake与Close竞态无孤儿", ActivateAndTakeCloseRaceHasNoOrphan,
                ref passed, failures);
            Run("held admission跨Close drain token不串代", HeldAdmissionCloseDrainToken,
                ref passed, failures);
            Run("admission token ABA迟到结算被拒", AdmissionTokenAbaIsRejected,
                ref passed, failures);
            Run("64并发publish/register/dispose/attach收敛", ConcurrentPipelineOperationsConverge,
                ref passed, failures);
            Run("Pipe-first/Durable-first canonical detail与单次delivery",
                CanonicalOrderPreservesSourcesAndOnce, ref passed, failures);
            if (failures.Count != 0)
                throw new InvalidOperationException(
                    "Runtime callback pipeline专项失败: " + string.Join("; ", failures));
            return passed;
        }

        private static void PermanentFailClosedHasNoRetry()
        {
            using (var target = new ImmediatePostTarget())
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                var targetLease = session.BindTarget("production-safety", target);
                Assert(targetLease.Accepted, "生产target绑定失败: " + targetLease.RejectionReason);
                var calls = 0;
                var handler = session.RegisterHandler(targetLease, "safety",
                    _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
                Assert(handler.Accepted, "生产handler注册失败: " + handler.FailureKind);
                Assert(session.Attach(), "生产pipeline未进入Ready");
                var closeStart = session.BeginCoordinatorClose();
                Assert(closeStart != null && closeStart.Accepted,
                    "真实coordinator BeginClose未成功");
                var beforeCloseOffer = session.Capture();
                var beforeCoordinator = session.CaptureCoordinator();
                var beforePosts = target.Calls;
                // Ingress observation is accepted into the immutable ledger;
                // the coordinator offer itself is the rejection boundary.
                Assert(session.PublishPipe("StopAllAfterClose", "closed-correlation"),
                    "Closed offer未被production ingress观察");
                Assert(WaitUntil(() => session.Capture().State == RuntimeCallbackPipelineState.FailClosed,
                    1000), "Closed offer未进入FailClosed");
                var after = session.Capture();
                var afterCoordinator = session.CaptureCoordinator();
                Assert(after.FailureReason.StartsWith("StopAllOffer:Closed:",
                        StringComparison.Ordinal),
                    "Closed首因未粘性保留: " + after.FailureReason);
                Assert(after.RetryGeneration == beforeCloseOffer.RetryGeneration &&
                       after.RetryGeneration == 0 && after.AdvanceTerminal && after.RetryTerminal,
                    "FailClosed后RetryGeneration发生增长");
                Assert(afterCoordinator != null && beforeCoordinator != null &&
                       afterCoordinator.Revision == beforeCoordinator.Revision,
                       "Closed offer错误推进coordinator revision");
                Assert(target.Calls == beforePosts && calls == 0,
                        "Closed offer错误触发post/handler");
                Assert(after.Ingress.ObservedSources == WatchdogStopAllSourceFlags.Pipe &&
                       after.Ingress.AdmittedSources == WatchdogStopAllSourceFlags.None &&
                       after.Ingress.PendingCount == 1,
                    string.Format("Closed evidence未严格保留Observed&~Admitted: observed={0};admitted={1};pending={2}",
                        after.Ingress.ObservedSources, after.Ingress.AdmittedSources,
                        after.Ingress.PendingCount));
                var close = session.Close();
                Assert(close.TerminalReason.StartsWith("StopAllOffer:Closed:",
                        StringComparison.Ordinal),
                    "关闭receipt未保留FailClosed首因: " + close.TerminalReason);
                Assert(close.PipelineState == RuntimeCallbackPipelineState.FailClosed,
                        "FailClosed cleanup错误改变为活动/假Terminal状态: " + close.PipelineState);
                Assert(close.IngressReceipt != null && close.IngressReceipt.PendingRequests == 1 &&
                       !close.IngressReceipt.DrainTerminal &&
                       close.WorkerResourcesReleased && !close.SafetyEvidenceResolved &&
                       !close.AllResourcesReleased && !close.IsTerminal,
                    "FailClosed错误把未解决安全证据报告为整体Terminal");
                var retained = session.Capture();
                Assert(retained.SafetyTarget != null && retained.CoordinatorCreated &&
                       retained.Ingress != null && !retained.Ingress.Disposed &&
                       retained.Ingress.PendingCount == 1,
                    "生产FailClosed错误清理context/owner/canonical证据");
                var abandoned = session.Abandon("FixtureAbandonEvidence", TimeSpan.FromMilliseconds(100));
                Assert(abandoned.TestAbandoned && !abandoned.SafetyEvidenceResolved &&
                       !abandoned.IsTerminal,
                    "fixture Abandon错误复用生产terminal/evidence receipt");
            }
        }

        private static void DualSourceReservationCollisionConverges()
        {
            using (var target = new ImmediatePostTarget())
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                var targetLease = session.BindTarget("dual-source", target);
                Assert(targetLease.Accepted, "双源target绑定失败");
                var calls = 0;
                var handler = session.RegisterHandler(targetLease, "dual-handler",
                    _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
                Assert(handler.Accepted, "双源handler注册失败");
                // Buffer both source observations before Attached, then hold
                // both exact production admission tokens across activation.
                // The advance owner must converge without spinning or
                // manufacturing a second canonical request.
                Assert(session.PublishPipe("PipeStop", "dual-correlation"),
                    "预attach Pipe evidence未接受");
                Assert(session.PublishDurable("DurableEvidence", "dual-correlation"),
                    "预attach Durable evidence未接受");
                var held = session.ReserveBothAdmissions();
                Assert(held[0] != null && held[1] != null,
                    "预attach双source reservation未建立");
                var attachTask = session.AttachAsync();
                Assert(attachTask.Wait(3000) && attachTask.Result,
                    "预attach双source AttachAsync未Ready");
                Assert(session.Capture().Ingress.PendingCount == 2,
                    "双source held admission错误提前消费");
                var aliasPipe = session.AliasAdmission(held[0]);
                Assert(!aliasPipe.Commit() && !aliasPipe.Release(),
                    "旧Pipe reservation错误跨结算边界修改状态");
                Assert(held[0].Release(), "Pipe held token释放失败");
                session.Advance();
                Assert(session.Capture().Ingress.PendingCount == 1,
                    "Pipe token release后pending水位错误");
                // Settle the second exact token and explicitly use the
                // production advance executor after each release.
                Assert(held[1].Release(), "Durable held token释放失败");
                session.Advance();
                Assert(WaitUntil(() =>
                {
                    var snapshot = session.Capture();
                    return snapshot.Ingress.PendingCount == 0 &&
                        (snapshot.Ingress.AdmittedSources & WatchdogStopAllSourceFlags.Both) == WatchdogStopAllSourceFlags.Both;
                }, 3000), "双源reservation未最终收敛");
                Assert(session.Capture().RetryGeneration == 0,
                    "双源CAS竞争创建了retry generation");
                Assert(WaitUntil(() => calls == 1, 3000),
                    "双源canonical未保持单次delivery，calls=" + calls);
                var coordinator = session.CaptureCoordinator();
                Assert(coordinator.CanonicalRequests.Count == 1,
                    "双源产生了多个canonical request");
                var canonical = coordinator.CanonicalRequests.Single();
                Assert(canonical.SourceFlags == WatchdogStopAllSourceFlags.Both &&
                       canonical.CorrelationId == "dual-correlation" &&
                       canonical.PipeSequence == 1 && canonical.DurableVersion == 1,
                    "双源canonical身份/水位错误");
                var close = session.Close();
                Assert(close.AllResourcesReleased, "双源pipeline cleanup未释放资源");
            }
        }

        private static void ActivateAndTakeCloseRaceHasNoOrphan()
        {
            using (var target = new ImmediatePostTarget())
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                Assert(session.BindTarget("activation-race", target).Accepted,
                    "activation-race target绑定失败");
                var handler = session.RegisterHandler(
                    session.Context.SafetyTargetLease, "race-handler",
                    _ => Task.CompletedTask);
                Assert(handler.Accepted, "activation-race handler注册失败");
                Assert(session.PublishPipe("RaceStop", "race-correlation"),
                    "activation-race buffer publish失败");

                var probeEntered = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseProbe = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.SetAfterActivateAndTakeBeforeReadyProbe(_ =>
                {
                    probeEntered.TrySetResult(true);
                    releaseProbe.Task.GetAwaiter().GetResult();
                });
                var attachTask = session.AttachAsync();
                Assert(probeEntered.Task.Wait(3000),
                    "生产ActivateAndTake->Ready phase probe未命中");
                var probeSnapshot = session.Capture();
                Assert(probeSnapshot.State == RuntimeCallbackPipelineState.Activating &&
                       probeSnapshot.ScopeLease != null && probeSnapshot.Ingress.ActiveAdmissions == 0,
                    "phase probe未观察到真实Activating边界");
                var closeTask = Task.Run(() => session.Close());
                Assert(WaitUntil(() => session.Capture().State == RuntimeCallbackPipelineState.Closing,
                    1000), "Close未赢得Activating边界");
                releaseProbe.TrySetResult(true);
                Assert(Task.WaitAll(new Task[] { attachTask, closeTask }, 10000),
                    "ActivateAndTake/Close竞态未收口");
                var receipt = closeTask.Result;
                var snapshot = session.Capture();
                Assert(receipt != null && receipt.AllWorkersTerminal,
                    "竞态关闭缺少terminal ShutdownReceipt");
                Assert(receipt.IngressReceipt == null || receipt.IngressReceipt.ActiveAdmissions == 0,
                    "竞态关闭遗留active admission");
                Assert(snapshot.State == RuntimeCallbackPipelineState.Terminal ||
                       snapshot.State == RuntimeCallbackPipelineState.FailClosed,
                    "竞态后pipeline仍处于活动态: " + snapshot.State);
                Assert(snapshot.Ingress == null || snapshot.Ingress.ActiveAdmissions == 0,
                    "竞态后Ingress存在孤儿reservation");
                if (receipt.PipelineState == RuntimeCallbackPipelineState.Terminal)
                {
                    Assert(receipt.IngressReceipt == null || receipt.IngressReceipt.PendingRequests == 0,
                        "Terminal竞态receipt仍有未解决Pending证据");
                    Assert(receipt.SafetyEvidenceResolved && receipt.IsTerminal,
                        "Terminal竞态receipt未完成安全证据/真实handler收敛");
                }
                else
                {
                    Assert(receipt.PipelineState == RuntimeCallbackPipelineState.FailClosed &&
                           receipt.IngressReceipt != null && receipt.IngressReceipt.PendingRequests > 0 &&
                           !receipt.SafetyEvidenceResolved && !receipt.IsTerminal,
                        "FailClosed竞态必须保留Pending且保持非终态");
                }
            }
        }

        private static void HeldAdmissionCloseDrainToken()
        {
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                Assert(session.PublishPipe("BufferedStop", "held-correlation"),
                    "buffered Pipe证据未接受");
                var held = session.ReserveAdmission(WatchdogStopAllSourceFlags.Pipe);
                Assert(held != null, "未能建立production admission reservation");
                var oldDrainToken = held.DrainToken;
                var closeTask = Task.Run(() => session.Close());
                Assert(WaitUntil(() => session.Capture().Ingress.ActiveDrainToken != 0, 1000),
                    "Close未发布drain token boundary");
                Assert(!held.Release(), "跨drain token的旧reservation错误报告成功");
                // CloseAndDrain uses the production bounded drain budget; the
                // test wait includes scheduler/join overhead and must not
                // turn a boundary assertion into a wall-clock race.
                Assert(Task.WaitAll(new[] { closeTask }, 10000), "held admission close未返回");
                var receipt = closeTask.Result;
                var snapshot = session.Capture();
                Assert(oldDrainToken == 0, "测试reservation未冻结旧drain token");
                Assert(snapshot.Ingress.ActiveAdmissions == 0,
                    "旧drain token释放后仍有active admission");
                Assert(snapshot.Ingress.PendingCount == 1,
                    "无scope时Close错误消费了pending evidence");
                Assert(receipt.PipelineState == RuntimeCallbackPipelineState.FailClosed &&
                       receipt.TerminalReason == "RequiredSafetySubscriberMissing",
                    "held admission close未给出明确failclosed receipt: " + receipt.TerminalReason);
            }
        }

        private static void AdmissionTokenAbaIsRejected()
        {
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                Assert(session.PublishPipe("AbaStop", "aba-correlation"),
                    "ABA buffered evidence未接受");
                var first = session.ReserveAdmission(WatchdogStopAllSourceFlags.Pipe);
                Assert(first != null, "ABA首reservation创建失败");
                var firstId = first.ReservationId;
                var aliasCommit = session.AliasAdmission(first);
                var aliasRelease = session.AliasAdmission(first);
                Assert(!aliasCommit.Commit() && !aliasRelease.Release(),
                    "同reservationId别名错误获得settle权限");
                Assert(session.Capture().Ingress.ActiveAdmissions == 1,
                    "别名settle错误改变真实active admission");
                Assert(first.Release(), "ABA首reservation释放失败");
                var second = session.ReserveAdmission(WatchdogStopAllSourceFlags.Pipe);
                Assert(second != null && second.ReservationId != firstId,
                    "ABA未生成新的exact reservation id");
                var lateAlias = session.AliasAdmission(first);
                Assert(!lateAlias.Commit() && !lateAlias.Release(),
                    "迟到旧token错误修改新reservation");
                Assert(second.Commit(), "当前reservation提交失败");
                var ingress = session.Capture().Ingress;
                Assert(ingress.ActiveAdmissions == 0 && ingress.PendingCount == 0 &&
                       (ingress.AdmittedSources & WatchdogStopAllSourceFlags.Pipe) != 0,
                    "ABA提交后canonical admission状态错误");
                Assert(session.Close().AllResourcesReleased,
                    "ABA cleanup未完全释放");
            }
        }

        private static void ConcurrentPipelineOperationsConverge()
        {
            using (var target = new DeferredPostTarget())
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                var targetLease = session.BindTarget("concurrent-64", target);
                Assert(targetLease.Accepted, "64并发target绑定失败");
                var start = new ManualResetEventSlim(false);
                var leases = new ConcurrentBag<WatchdogRuntime.RuntimeStopAllHandlerLease>();
                var tasks = new List<Task>();
                var disposedCount = 0;
                var handlerCalls = 0;
                var handlerCallsBySubscriber = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < 64; i++)
                {
                    var index = i;
                    tasks.Add(Task.Run(() =>
                    {
                        start.Wait();
                        var subscriberId = "subscriber-" + index;
                        var lease = session.RegisterHandler(targetLease, subscriberId,
                            _ =>
                            {
                                Interlocked.Increment(ref handlerCalls);
                                handlerCallsBySubscriber.AddOrUpdate(subscriberId, 1, (_, count) => count + 1);
                                return Task.CompletedTask;
                            });
                        if (lease.Accepted) leases.Add(lease);
                        if ((index % 3) == 0)
                        {
                            Interlocked.Increment(ref disposedCount);
                            lease.Dispose();
                        }
                    }));
                }
                tasks.Add(Task.Run(() => { start.Wait(); session.Attach(); }));
                tasks.Add(Task.Run(() => { start.Wait(); session.PublishPipe("ConcurrentStop", "concurrent-correlation"); }));
                start.Set();
                Assert(Task.WaitAll(tasks.ToArray(), 5000), "64并发pipeline操作未收口");
                start.Dispose();
                session.Advance();
                foreach (var lease in leases)
                {
                    Assert(lease.BindingCompletion.Wait(3000),
                        "并发registration BindingCompletion未完成: " + lease.SubscriberId);
                    if (lease.IsActive)
                        Assert(lease.IsBound && lease.BindingCompletion.Result != null,
                            "活动registration未绑定真实coordinator lease: " + lease.SubscriberId);
                }
                Assert(WaitUntil(() =>
                {
                    var snapshot = session.Capture();
                    return snapshot.State == RuntimeCallbackPipelineState.Ready &&
                           snapshot.Ingress.PendingCount == 0;
                }, 3000), "64并发pipeline无最终状态");
                var final = session.Capture();
                Assert(final.State != RuntimeCallbackPipelineState.FailClosed,
                    "64并发操作错误进入FailClosed: " + final.FailureReason);
                Assert(final.Ingress.ActiveAdmissions == 0 && final.Ingress.PendingCount == 0,
                    "64并发操作遗留Ingress admission/pending");
                Assert(final.RetryGeneration == 0, "64并发操作产生retry generation");
                var coordinator = session.CaptureCoordinator();
                var activeLeases = leases.Count(item => item.IsActive);
                Assert(activeLeases == leases.Count - disposedCount,
                    "并发registration disposed计数与active lease不一致");
                var activeSubscriberIds = new HashSet<string>(
                    leases.Where(item => item.IsActive).Select(item => item.SubscriberId),
                    StringComparer.Ordinal);
                var coordinatorRegistrations = coordinator?.Registrations ?? Array.Empty<WatchdogStopAllRegistrationSnapshot>();
                var duplicateRegistrationIds = coordinatorRegistrations.Count -
                    coordinatorRegistrations.Select(item => item.RegistrationId).Distinct().Count();
                var activeRegistrationCounts = coordinatorRegistrations
                    .Where(item => activeSubscriberIds.Contains(item.SubscriberId))
                    .GroupBy(item => item.SubscriberId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                var disposedRegistrations = coordinatorRegistrations.Count(item =>
                    !activeSubscriberIds.Contains(item.SubscriberId));
                Assert(coordinator != null &&
                       duplicateRegistrationIds == 0 &&
                       activeSubscriberIds.All(id => activeRegistrationCounts.TryGetValue(id, out var count) && count == 1) &&
                       coordinator.Subscribers.Count(item => item.Active) == activeLeases &&
                       disposedRegistrations <= disposedCount,
                    string.Format("并发registration存在重复/孤儿/未激活handler: accepted={0};disposed={1};active={2};registrations={3};subscribers={4};disposedRecords={5};duplicateIds={6}",
                        leases.Count, disposedCount, activeLeases,
                        coordinatorRegistrations.Count,
                        coordinator == null ? -1 : coordinator.Subscribers.Count(item => item.Active),
                        disposedRegistrations, duplicateRegistrationIds));
                Assert(WaitUntil(() =>
                {
                    target.ReleaseAll();
                    var liveExactlyOnce = activeSubscriberIds.All(id =>
                        handlerCallsBySubscriber.TryGetValue(id, out var count) && count == 1);
                    var noDuplicate = handlerCallsBySubscriber.Values.All(count => count <= 1);
                    var racedDisposedPosts = target.Calls - handlerCalls;
                    return liveExactlyOnce && noDuplicate && racedDisposedPosts >= 0 &&
                           racedDisposedPosts <= disposedCount;
                }, 3000), string.Format("并发publish未对每个live handler恰好delivery一次: target={0};handler={1};active={2};accepted={3};disposed={4};registrations={5};liveCalls={6}",
                        target.Calls, handlerCalls, activeLeases, leases.Count, disposedCount,
                        coordinatorRegistrations.Count,
                        string.Join(",", handlerCallsBySubscriber.OrderBy(item => item.Key).Select(item => item.Key + "=" + item.Value))));
                var close = session.Close();
                Assert(close.AllResourcesReleased, "64并发pipeline关闭未释放资源");
            }
        }

        private static void CanonicalOrderPreservesSourcesAndOnce()
        {
            VerifyCanonicalOrder(false);
            VerifyCanonicalOrder(true);
        }

        private static void VerifyCanonicalOrder(bool durableFirst)
        {
            using (var target = new DeferredPostTarget())
            using (var session = new RuntimeCallbackPipelineTestSession())
            {
                var targetLease = session.BindTarget(durableFirst ? "durable-first" : "pipe-first", target);
                Assert(targetLease.Accepted, "canonical order target绑定失败");
                var seen = new ConcurrentQueue<WatchdogStopAllOfferEnvelope>();
                var handler = session.RegisterHandler(targetLease, "canonical-order",
                    envelope => { seen.Enqueue(envelope); return Task.CompletedTask; });
                Assert(handler.Accepted, "canonical order handler注册失败");

                var first = durableFirst
                    ? session.PublishDurable("DurableReason", "order-correlation")
                    : session.PublishPipe("PipeReason", "order-correlation");
                var second = durableFirst
                    ? session.PublishPipe("PipeDetail", "different-opposite-correlation")
                    : session.PublishDurable("DurableDetail", "different-opposite-correlation");
                Assert(first && second, "canonical order source publish未接受");
                Assert(session.Attach(), "canonical order pipeline未Ready");
                Assert(WaitUntil(() =>
                {
                    target.ReleaseAll();
                    return seen.Count == 1 && session.Capture().Ingress.PendingCount == 0;
                }, 3000), "canonical order未单次delivery");
                var envelope = seen.Single();
                var ingress = session.Capture().Ingress;
                var expectedReason = durableFirst ? "DurableReason" : "PipeReason";
                Assert(envelope.SourceFlags == WatchdogStopAllSourceFlags.Both &&
                       envelope.CorrelationId == "order-correlation" &&
                       envelope.PipeSequence == 1 && envelope.DurableVersion == 1,
                    string.Format("canonical order source/identity水位错误: envelopeFlags={0};corr={1};pipe={2};durable={3}",
                        envelope.SourceFlags, envelope.CorrelationId, envelope.PipeSequence, envelope.DurableVersion));
                var expectedDetail = durableFirst ? "PipeDetail" : "DurableDetail";
                Assert(envelope.Detail == expectedDetail,
                    string.Format("canonical order第二source detail未进入最终envelope: durableFirst={0};envelope={1};ingressPipe={2};ingressDurable={3};coord={4}",
                        durableFirst, envelope.Detail, session.Capture().Ingress.PipeDetail,
                        session.Capture().Ingress.DurableDetail,
                        session.CaptureCoordinator().CanonicalRequests.Single().Detail));
                var expectedPipeDetail = durableFirst ? "PipeDetail" : "PipeReason";
                var expectedDurableDetail = durableFirst ? "DurableReason" : "DurableDetail";
                Assert(ingress.FrozenReasonCode == expectedReason &&
                       ingress.FrozenCorrelationId == "order-correlation" &&
                       ingress.FrozenRequestId == "order-correlation" &&
                       ingress.ObservedSources == WatchdogStopAllSourceFlags.Both &&
                       ingress.AdmittedSources == WatchdogStopAllSourceFlags.Both &&
                       ingress.PipeSequence == 1 && ingress.DurableVersion == 1 &&
                       ingress.PipeDetail == expectedPipeDetail &&
                       ingress.DurableDetail == expectedDurableDetail,
                    string.Format("canonical order frozen identity/source detail ledger不完整: reason={0};corr={1};request={2};obs={3};adm={4};pipeSeq={5};durable={6};pipeDetail={7};durableDetail={8}",
                        ingress.FrozenReasonCode, ingress.FrozenCorrelationId,
                        ingress.FrozenRequestId, ingress.ObservedSources,
                        ingress.AdmittedSources, ingress.PipeSequence,
                        ingress.DurableVersion, ingress.PipeDetail,
                        ingress.DurableDetail));
                var coordinator = session.CaptureCoordinator();
                Assert(coordinator.CanonicalRequests.Count == 1 &&
                       coordinator.CanonicalRequests.Single().SourceFlags == WatchdogStopAllSourceFlags.Both &&
                       coordinator.CanonicalRequests.Single().ReasonCode == expectedReason &&
                       coordinator.CanonicalRequests.Single().RequestId == "order-correlation" &&
                       coordinator.CanonicalRequests.Single().PipeSequence == 1 &&
                       coordinator.CanonicalRequests.Single().DurableVersion == 1,
                    "canonical order coordinator状态不是单一Both request");
                Assert(session.Close().AllResourcesReleased,
                    "canonical order cleanup未完全释放");
            }
        }

        private static bool WaitUntil(Func<bool> predicate, int timeoutMilliseconds)
        {
            var spinner = new SpinWait();
            var started = Environment.TickCount;
            while (!predicate())
            {
                if (unchecked(Environment.TickCount - started) >= timeoutMilliseconds) return false;
                spinner.SpinOnce();
            }
            return true;
        }

        private static void Run(string name, Action action, ref int passed,
            List<string> failures)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failures.Add(name + ": " + ex.GetBaseException().Message);
                Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
