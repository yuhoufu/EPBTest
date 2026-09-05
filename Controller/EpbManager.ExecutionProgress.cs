using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace Controller
{
    public sealed partial class EpbManager
    {
        private sealed class ExecutionExpectation
        {
            internal Guid RunId;
            internal long RunEpoch, AfterAttempt, LastProgress, Since;
            internal ChannelRuntimeState Phase;
        }

        private readonly ConcurrentDictionary<Guid, long> _recoveryAttemptFences = new();
        private readonly ConcurrentDictionary<int, ExecutionExpectation> _executionExpectations = new();
        private readonly ConcurrentDictionary<int, ExecutionExpectation> _businessRejoinPending = new();

        private void ExpectVerifiedBusinessCycle(int channel)
        {
            _businessRejoinPending[channel] = new ExecutionExpectation
            {
                RunId = _activeBatchId, RunEpoch = Interlocked.Read(ref _runEpoch),
                AfterAttempt = Interlocked.Read(ref _cycleAttemptSequence), Since = Stopwatch.GetTimestamp()
            };
        }

        internal static bool RecoveryOwnsAttempt(RecoveryContractSnapshot contract, long fence, CycleAttemptContext attempt)
            => contract != null && attempt != null && attempt.RunId == contract.RunId &&
               attempt.RunEpoch == contract.RunEpoch && attempt.AttemptId <= fence;

        internal static bool IsVerifiedBusinessRejoin(Guid runId, long epoch, long afterAttempt,
            CycleAttemptContext attempt, bool actionSucceeded)
            => actionSucceeded && attempt != null && attempt.RunId == runId && attempt.RunEpoch == epoch &&
               attempt.AttemptId > afterAttempt && attempt.IsDurablyCommitted &&
               attempt.TerminalState == CycleAttemptTerminalState.Completed;

        private void ConfirmBusinessCycle(int channel, CycleAttemptContext attempt, Adaptive.EpbCycleOutcome outcome)
        {
            if (!_businessRejoinPending.TryGetValue(channel, out var pending) ||
                !IsVerifiedBusinessRejoin(pending.RunId, pending.RunEpoch, pending.AfterAttempt,
                    attempt, outcome != null && outcome.IsSuccess && outcome.MechanicalCycleCompleted)) return;
            if (!((ICollection<KeyValuePair<int, ExecutionExpectation>>)_businessRejoinPending)
                .Remove(new KeyValuePair<int, ExecutionExpectation>(channel, pending))) return;
            PublishChannelRuntimeState(channel, ChannelRuntimeState.Running, "BusinessRecoveryVerified",
                $"首圈动作和有效数据已提交；Attempt={attempt.AttemptId} Cycle={attempt.Cycle}",
                correlationId: attempt.RunId);
        }

        // Desired membership comes from enabled channels, never from the set of surviving timers.
        // Only a physical completed action advances this clock; UI refresh and retries do not.
        private void InspectExpectedExecutionProgress()
        {
            var runId = _activeBatchId;
            if (runId == Guid.Empty) return;
            var epoch = Interlocked.Read(ref _runEpoch);
            var now = Stopwatch.GetTimestamp();
            foreach (var channel in Enumerable.Range(1, 12).Where(IsChannelEnabled))
            {
                var state = _channelRuntimeStateStore.Get(channel);
                if (state == null || state.RunId != runId || state.RunEpoch != epoch ||
                    IsAlarmStopRequested(channel) || _channelPausedUtc.ContainsKey(channel) ||
                    state.State == ChannelRuntimeState.ManualStopped || state.State == ChannelRuntimeState.Completed ||
                    state.State == ChannelRuntimeState.Paused || state.State == ChannelRuntimeState.PausePending ||
                    state.State == ChannelRuntimeState.Recovering || state.State == ChannelRuntimeState.SystemFault ||
                    state.State == ChannelRuntimeState.NotEnabled || state.State == ChannelRuntimeState.AlarmStopped ||
                    state.State == ChannelRuntimeState.InterlockStopped)
                {
                    _executionExpectations.TryRemove(channel, out _);
                    continue;
                }
                _watchdogMechanicalCompletedCount.TryGetValue(channel, out var progress);
                _recentExecutionEvidence.Enqueue($"{DateTime.UtcNow:O} EPB={channel} Phase={state.State} " +
                    $"Mechanical={progress} Timer={_timers.ContainsKey(channel)} Runner={_runners.ContainsKey(channel)} " +
                    $"Run={runId:N} Epoch={epoch} Reason={state.ReasonCode}");
                while (_recentExecutionEvidence.Count > 1200) _recentExecutionEvidence.TryDequeue(out _);
                var current = _executionExpectations.GetOrAdd(channel, _ => new ExecutionExpectation
                { RunId = runId, RunEpoch = epoch, LastProgress = progress, Since = now, Phase = state.State });
                if (current.RunId != runId || current.RunEpoch != epoch || current.LastProgress != progress)
                {
                    _executionExpectations[channel] = new ExecutionExpectation
                    { RunId = runId, RunEpoch = epoch, LastProgress = progress, Since = now, Phase = state.State };
                    continue;
                }
                var limitMs = Math.Max(RecoveryGroupHardDeadlineMs, (double)PeriodMs * 3);
                var elapsedMs = (now - current.Since) * 1000d / Stopwatch.Frequency;
                if (elapsedMs < limitMs) continue;
                var reason = $"EPB={channel};Phase={state.State};NoActionProgressMs={elapsedMs:F0};" +
                    $"Timer={_timers.ContainsKey(channel)};Runner={_runners.ContainsKey(channel)};" +
                    $"Run={runId:N};Epoch={epoch};LastMechanical={progress}";
                QueueExecutionProgressEvidence(reason);
                _log?.Error("ExecutionProgressDeadline " + reason, "运行监督");
                TryEscalateSoftwareRecoveryCircuitOpen("ExecutionProgressDeadline", reason,
                    new[] { channel }, runId, epoch, SoftwareRecoveryEscalationAttempts, "ExecutionProgressDeadline");
            }
        }
        private readonly ConcurrentQueue<string> _recentExecutionEvidence = new();
        private int _executionEvidenceBusy;
        private long _lastExecutionEvidenceTick;

        private void QueueExecutionProgressEvidence(string reason)
        {
            var now = Stopwatch.GetTimestamp();
            if ((now - Interlocked.Read(ref _lastExecutionEvidenceTick)) * 1000d / Stopwatch.Frequency < 60000 ||
                Interlocked.CompareExchange(ref _executionEvidenceBusy, 1, 0) != 0) return;
            Interlocked.Exchange(ref _lastExecutionEvidenceTick, now);
            var before = _recentExecutionEvidence.ToArray();
            // Diagnostics never enter the controller drain set or acquire a hardware lease.
            _ = Task.Run(async () =>
            {
                try
                {
                    var directory = Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName,
                        "IncidentSnapshots", "ExecutionProgress");
                    Directory.CreateDirectory(directory);
                    var files = new DirectoryInfo(directory).GetFiles("*.txt").OrderBy(f => f.CreationTimeUtc).ToList();
                    long bytes = files.Sum(f => f.Length);
                    while (files.Count >= 100 || bytes > 50L * 1024 * 1024)
                    {
                        if (files.Count == 0) break;
                        var oldest = files[0]; files.RemoveAt(0);
                        bytes -= oldest.Length; oldest.Delete();
                    }
                    var path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".txt");
                    var text = new StringBuilder(reason).AppendLine().AppendLine("BEFORE");
                    foreach (var item in before) text.AppendLine(item);
                    AppendExecutionThreadEvidence(text);
                    File.WriteAllText(path, text.ToString(), Encoding.UTF8);
                    await Task.Delay(5000).ConfigureAwait(false);
                    text.Clear().AppendLine("AFTER");
                    foreach (var item in _recentExecutionEvidence.ToArray()) text.AppendLine(item);
                    AppendExecutionThreadEvidence(text);
                    File.AppendAllText(path, text.ToString(), Encoding.UTF8);
                }
                catch (Exception ex) { try { _log?.Warn("运行诊断未完成：" + ex.Message, "运行监督"); } catch { } }
                finally { Interlocked.Exchange(ref _executionEvidenceBusy, 0); }
            });
        }

        private void AppendExecutionThreadEvidence(StringBuilder text)
        {
            foreach (var task in _taskSupervisor.Snapshot())
                text.AppendLine($"Task={task.Operation} Channel={task.Channel} Run={task.RunId:N} " +
                    $"Started={task.StartedUtc:O} Status={task.Task.Status}");
            using var process = Process.GetCurrentProcess();
            foreach (ProcessThread thread in process.Threads)
            {
                try { text.AppendLine($"Thread={thread.Id} State={thread.ThreadState} CPU={thread.TotalProcessorTime}"); }
                catch { }
                finally { thread.Dispose(); }
            }
        }
    }
}
