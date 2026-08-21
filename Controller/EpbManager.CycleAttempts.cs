using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DataOperation;

namespace Controller
{
    public sealed partial class EpbManager
    {
        private readonly CycleAttemptRegistry _cycleAttempts = new CycleAttemptRegistry();

        private bool TryBeginFormalCycleAttempt(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            DateTime beginUtc,
            Guid runId,
            CycleAttemptKind kind,
            CancellationToken parentToken,
            out CycleAttemptContext context)
        {
            var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            var device = string.Empty;
            try { device = _acq?.GetDeviceForEpbChannel(channel) ?? string.Empty; }
            catch { }

            var created = new CycleAttemptContext(
                runId,
                Interlocked.Read(ref _runEpoch),
                device,
                channel,
                Interlocked.Increment(ref _cycleAttemptSequence),
                kind,
                cycleNumber,
                attemptCts);
            context = created;

            try
            {
                var accepted = _cycleAttempts.TryRegisterBeforeBegin(created, () =>
                {
                    // 兼容现有报警/快照读取面：registry 是真相源，两个旧字典只是同步投影。
                    // 三者均在 Recorder.BeginCycle 之前可见。
                    _currentCycleNumberByChannel[channel] = cycleNumber;
                    _currentAttemptIdByChannel[channel] = created.AttemptId;
                    if (recorder is ISequencedEpbCycleRecorder sequenced &&
                        !string.IsNullOrWhiteSpace(device))
                    {
                        sequenced.BeginCycleAtDaqBoundary(
                            channel,
                            cycleNumber,
                            beginUtc,
                            device,
                            _acq.GetCurrentGeneration(device),
                            _acq.GetLastAcceptedSequence(device));
                    }
                    else
                    {
                        recorder?.BeginCycle(channel, cycleNumber, beginUtc);
                    }
                    created.MarkBeginSucceeded();
                });
                if (accepted &&
                    _cycleAttempts.IsCurrent(created) &&
                    created.BeginState == CycleAttemptBeginState.Begun &&
                    created.TerminalState == CycleAttemptTerminalState.Active &&
                    !created.AttemptCts.IsCancellationRequested)
                    return true;

                if (accepted)
                {
                    created.CancelAttempt();
                    _cycleAttempts.MarkExecutionCompleted(created);
                    if (_cycleAttempts.IsCurrent(created) &&
                        created.TerminalState == CycleAttemptTerminalState.Active)
                        ReportFormalPersistenceRecovery(
                            channel,
                            cycleNumber,
                            "BeginCycleInvalidatedBeforeRunner",
                            new OperationCanceledException(
                                "圈 Begin 返回时尝试已撤销或不再拥有当前身份。"));
                    return false;
                }

                created.Dispose();
                context = null;
                if (_cycleAttempts.TryGetCurrent(channel, out var existing))
                {
                    ReportFormalPersistenceRecovery(
                        channel,
                        existing.Cycle,
                        "CycleAttemptRegistryOccupied",
                        new InvalidOperationException(
                            $"旧圈尝试尚未耐久收口。ExistingAttempt={existing.AttemptId} " +
                            $"ExistingCycle={existing.Cycle} RequestedCycle={cycleNumber}"));
                }
                else if (_cycleAttempts.TryGetLastExecution(channel, out var executing))
                {
                    ReportFormalPersistenceRecovery(
                        channel,
                        executing.Cycle,
                        "CycleAttemptExecutionStillRunning",
                        new InvalidOperationException(
                            $"旧圈Runner尚未退出，拒绝复用通道执行器。" +
                            $"ExistingAttempt={executing.AttemptId} " +
                            $"ExistingCycle={executing.Cycle} RequestedCycle={cycleNumber}"));
                }
                return false;
            }
            catch (Exception ex)
            {
                // Begin 已经取得 registry 身份；异常时必须保留，供停止/报警/自恢复精确封圈。
                created.MarkBeginFailed(ex);
                _cycleAttempts.MarkExecutionCompleted(created);
                ReportFormalPersistenceRecovery(channel, cycleNumber, "BeginCycle", ex);
                return false;
            }
        }

        private async System.Threading.Tasks.Task<bool> WaitForPreviousCycleExecutionAsync(
            int channel,
            CancellationToken token)
        {
            var completed = await _cycleAttempts.WaitForPreviousExecutionAsync(
                    channel,
                    _daqPersistenceRecoveryTimeoutMs,
                    token)
                .ConfigureAwait(false);
            if (completed) return true;

            if (_cycleAttempts.TryGetLastExecution(channel, out var previous))
                _log?.Warn(
                    $"EPB[{channel}] 旧圈Runner在{_daqPersistenceRecoveryTimeoutMs}ms内未退出，" +
                    $"保持OFF并拒绝启动新圈。Attempt={previous.AttemptId} Cycle={previous.Cycle}",
                    "EPB并发");
            try
            {
                TryEnsureSoftwareRecoveryOutputOff(
                    channel,
                    "CycleExecutionQuiescenceTimeout");
            }
            catch (Exception ex)
            {
                _log?.Error(
                    $"EPB[{channel}] execution quiescence超时后的OFF兜底异常：{ex.Message}",
                    "EPB并发",
                    ex);
            }
            return false;
        }

        internal static async System.Threading.Tasks.Task InvokeAfterCycleExecutionQuiescenceAsync(
            IEnumerable<int> channels,
            Func<int, CancellationToken, System.Threading.Tasks.Task<bool>> waitOne,
            Func<CancellationToken, System.Threading.Tasks.Task> action,
            string stage,
            CancellationToken token)
        {
            if (waitOne == null) throw new ArgumentNullException(nameof(waitOne));
            if (action == null) throw new ArgumentNullException(nameof(action));

            var selected = (channels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var waits = selected.Select(async channel => new
            {
                Channel = channel,
                Completed = await waitOne(channel, token).ConfigureAwait(false)
            }).ToArray();
            var results = await System.Threading.Tasks.Task.WhenAll(waits)
                .ConfigureAwait(false);
            var blocked = results
                .Where(result => !result.Completed)
                .Select(result => result.Channel)
                .ToArray();
            if (blocked.Length > 0)
                throw new InvalidOperationException(
                    $"CycleExecutionQuiescenceTimeout Stage={stage ?? "Unknown"} " +
                    $"Channels=[{string.Join(",", blocked)}]");

            token.ThrowIfCancellationRequested();
            await action(token).ConfigureAwait(false);
        }

        private void CompleteCycleAttemptExecution(CycleAttemptContext context)
        {
            if (context != null)
                _cycleAttempts.MarkExecutionCompleted(context);
        }

        private IDisposable CompleteCycleAttemptExecutionOnCallbackExit(
            CycleAttemptContext context)
        {
            return new CycleAttemptExecutionScope(
                () => CompleteCycleAttemptExecution(context));
        }

        private sealed class CycleAttemptExecutionScope : IDisposable
        {
            private Action _complete;

            internal CycleAttemptExecutionScope(Action complete)
            {
                _complete = complete;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _complete, null)?.Invoke();
            }
        }

        private bool CompleteFormalCycleAttempt(
            CycleAttemptContext context,
            IEpbCycleRecorder recorder,
            int finalSampleCount,
            DateTime endUtc)
        {
            if (context == null) return false;
            return context.CompleteRecorderOnce(
                () => CompleteCycleAndScheduleEvidence(
                    recorder,
                    context.Channel,
                    context.Cycle,
                    finalSampleCount,
                    endUtc),
                RemoveCycleAttemptAfterDurableTerminal);
        }

        private bool AbortFormalCycleAttempt(
            CycleAttemptContext context,
            IEpbCycleRecorder recorder,
            DateTime endUtc,
            string status)
        {
            if (context == null) return false;
            return context.AbortRecorderOnce(
                () =>
                {
                    AbortCycleAfterPersistence(
                        recorder,
                        context.Channel,
                        context.Cycle,
                        endUtc,
                        status);
                    return true;
                },
                RemoveCycleAttemptAfterDurableTerminal);
        }

        private bool CommitExternallyAbortedCycleAttempt(CycleAttemptContext context)
        {
            if (context == null) return false;
            if (context.BeginState == CycleAttemptBeginState.Registered)
            {
                context.CancelAttempt();
                return false;
            }
            return context.AbortOnce(() => true, RemoveCycleAttemptAfterDurableTerminal);
        }

        private void CommitCycleAttemptAfterSnapshotEvidence(
            int channel,
            AlarmCycleSnapshotEvidence evidence)
        {
            if (!_cycleAttempts.TryGetCurrent(channel, out var context))
            {
                ClearCurrentCycleNumber(channel);
                return;
            }

            if (context.BeginState == CycleAttemptBeginState.Registered)
            {
                context.CancelAttempt();
                _log.Warn(
                    $"EPB[{channel}] Recorder.BeginCycle尚未返回，拒绝用报警证据清除圈身份。" +
                    $"Attempt={context.AttemptId} Cycle={context.Cycle}",
                    "落盘");
                return;
            }

            if (context.BeginState == CycleAttemptBeginState.Failed)
            {
                context.AbortOnce(() => true, RemoveCycleAttemptAfterDurableTerminal);
                return;
            }

            var status = evidence?.FinalStatus ?? string.Empty;
            bool committed;
            if (string.Equals(status, "alarm", StringComparison.OrdinalIgnoreCase))
                committed = context.AlarmOnce(
                    () => true,
                    RemoveCycleAttemptAfterDurableTerminal);
            else if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                committed = context.CompleteOnce(
                    () => true,
                    RemoveCycleAttemptAfterDurableTerminal);
            else
                committed = context.AbortOnce(
                    () => true,
                    RemoveCycleAttemptAfterDurableTerminal);

            if (!committed && !context.IsDurablyCommitted)
                _log.Warn(
                    $"EPB[{channel}] 报警证据已封存但统一圈终态正在由其它所有者提交；" +
                    $"Attempt={context.AttemptId} Cycle={context.Cycle} Status={status}",
                    "落盘");
        }

        private bool TryGetCycleAttempt(
            int channel,
            int cycleNumber,
            out CycleAttemptContext context)
        {
            return _cycleAttempts.TryGetCurrent(channel, out context) &&
                   context.Cycle == cycleNumber;
        }

        private void RemoveCycleAttemptAfterDurableTerminal(CycleAttemptContext context)
        {
            if (!_cycleAttempts.TryRemoveExact(context)) return;

            // 先以唯一 AttemptId 删除投影所有权。若新尝试已注册，旧 AttemptId 删除会失败，
            // 从而绝不继续删除新尝试即使恰好复用了同一 Cycle 号。
            var attemptRemoved =
                ((ICollection<KeyValuePair<int, long>>)_currentAttemptIdByChannel)
                .Remove(new KeyValuePair<int, long>(context.Channel, context.AttemptId));
            if (attemptRemoved)
                ((ICollection<KeyValuePair<int, int>>)_currentCycleNumberByChannel)
                    .Remove(new KeyValuePair<int, int>(context.Channel, context.Cycle));
        }
    }
}
