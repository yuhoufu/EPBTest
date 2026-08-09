using System;
using System.Collections.Generic;
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
                    recorder?.BeginCycle(channel, cycleNumber, beginUtc);
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
                return false;
            }
            catch (Exception ex)
            {
                // Begin 已经取得 registry 身份；异常时必须保留，供停止/报警/自恢复精确封圈。
                created.MarkBeginFailed(ex);
                ReportFormalPersistenceRecovery(channel, cycleNumber, "BeginCycle", ex);
                return false;
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
            context.Dispose();
        }
    }
}
