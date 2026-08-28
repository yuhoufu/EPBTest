using System;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Timing;

namespace Controller
{
    public sealed class PermanentAlarmResetResult
    {
        public bool Succeeded { get; set; }
        public bool Enabled { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    public sealed partial class EpbManager
    {
        public const int ConsecutivePeriodOverrunAlarmThreshold = 8;

        internal static Adaptive.PeriodOverrunKind ClassifyCompletedPeriodOverrun(
            long physicalActionElapsedMs,
            int periodMs,
            int consecutiveOverrunCount)
        {
            var normalizedPeriodMs = Math.Max(1L, periodMs);
            if (Math.Max(0L, physicalActionElapsedMs) <= normalizedPeriodMs)
                return Adaptive.PeriodOverrunKind.None;
            if (physicalActionElapsedMs >= normalizedPeriodMs * 2L)
                return Adaptive.PeriodOverrunKind.HardLimitReached;
            if (Math.Max(0, consecutiveOverrunCount) >=
                ConsecutivePeriodOverrunAlarmThreshold)
                return Adaptive.PeriodOverrunKind.ConsecutiveLimitReached;
            return Adaptive.PeriodOverrunKind.CompletedOverrun;
        }

        internal static bool IsSharedCoordinationWaitAtHardDeadline(
            bool physicalActionTerminal,
            bool channelEnergized)
        {
            return physicalActionTerminal && !channelEnergized;
        }

        private async Task<bool> EvaluateCommittedPeriodOverrunAsync(
            int channel,
            int cycleNumber,
            Adaptive.EpbCycleOutcome outcome,
            HighPrecisionTimer timer)
        {
            if (outcome == null || !outcome.IsSuccess || !outcome.MechanicalCycleCompleted)
                return false;

            var physicalMs = Math.Max(0L, outcome.PhysicalActionElapsedMs);
            var hardLimitMs = Math.Max(1L, PeriodMs) * 2L;
            if (physicalMs <= PeriodMs)
            {
                if (_periodOverrunStreaks.TryGetValue(channel, out var existing) && existing > 0)
                {
                    if (!TryPersistPeriodOverrunState(channel, 0, null, out var error))
                    {
                        await EscalatePeriodAlarmPersistenceFailureAsync(
                                channel,
                                "PeriodOverrunResetPersistenceFailed: " + error)
                            .ConfigureAwait(false);
                        return true;
                    }
                    _log?.Info(
                        $"EPB[{channel}] 自身动作恢复到设定周期内，连续超限次数已清零。" +
                        $"Cycle={cycleNumber} Physical={physicalMs}ms Period={PeriodMs}ms",
                        "周期屏障");
                }
                outcome.PeriodOverrunKind = Adaptive.PeriodOverrunKind.None;
                return false;
            }

            var nowUtc = DateTime.UtcNow;
            var next = Math.Max(0, _periodOverrunStreaks.TryGetValue(channel, out var count)
                ? count
                : 0) + 1;
            if (!TryPersistPeriodOverrunState(channel, next, nowUtc, out var persistError))
            {
                await EscalatePeriodAlarmPersistenceFailureAsync(
                        channel,
                        "PeriodOverrunPersistenceFailed: " + persistError)
                    .ConfigureAwait(false);
                return true;
            }

            var classification = ClassifyCompletedPeriodOverrun(
                physicalMs,
                PeriodMs,
                next);
            if (classification == Adaptive.PeriodOverrunKind.HardLimitReached)
            {
                outcome.PeriodOverrunKind = classification;
                return await LatchPermanentPeriodAlarmAsync(
                        channel,
                        timer,
                        "PeriodOverrunHardLimit",
                        $"卡钳自身动作耗时 {physicalMs}ms 达到硬上限 {hardLimitMs}ms；" +
                        $"Cycle={cycleNumber}")
                    .ConfigureAwait(false);
            }

            if (classification == Adaptive.PeriodOverrunKind.ConsecutiveLimitReached)
            {
                outcome.PeriodOverrunKind = classification;
                return await LatchPermanentPeriodAlarmAsync(
                        channel,
                        timer,
                        "ConsecutivePeriodOverrun",
                        $"卡钳自身动作连续 {next} 圈超过设定周期；" +
                        $"Cycle={cycleNumber} Physical={physicalMs}ms Period={PeriodMs}ms")
                    .ConfigureAwait(false);
            }

            outcome.PeriodOverrunKind = classification;
            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.WarningRunning,
                "PeriodOverrun",
                $"自身动作超过设定周期：{physicalMs}ms/{PeriodMs}ms，连续 {next}/" +
                $"{ConsecutivePeriodOverrunAlarmThreshold} 圈；等待共享周期收尾");
            _log?.Warn(
                $"EPB[{channel}] 完成圈自身动作超限。Cycle={cycleNumber} " +
                $"Physical={physicalMs}ms Period={PeriodMs}ms Streak={next}/" +
                $"{ConsecutivePeriodOverrunAlarmThreshold} SharedWait={outcome.SharedCoordinationWaitMs}ms",
                "周期屏障");
            return false;
        }

        private async Task<bool> HandleFormalPeriodHardLimitAsync(
            int channel,
            int cycleNumber,
            CycleAttemptContext cycleAttempt,
            HighPrecisionTimer timer,
            Task<bool> runnerTask,
            bool physicalActionTerminalAndOff)
        {
            // 只有“本代次所有可能上电阶段已终止 + 当前 OFF”才是共享收口等待。
            // Hold/正反转间隙中的瞬时 OFF 不能放行，否则 Runner 可在硬截止后重新上电。
            if (physicalActionTerminalAndOff)
            {
                _log?.Warn(
                    $"EPB[{channel}] 回调达到 {PeriodMs * 2L}ms，但电机已经 OFF；" +
                    "判定为共享协调等待，不计卡钳超限。",
                    "周期屏障");
                try { return await runnerTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }

            cycleAttempt?.CancelAttempt();
            var offConfirmed = false;
            try
            {
                offConfirmed = CommandEpbOffHighPriority(
                                   channel,
                                   "PeriodOverrunHardLimit") ||
                               !IsChannelEnergized(channel);
            }
            catch
            {
                offConfirmed = !IsChannelEnergized(channel);
            }
            if (!offConfirmed)
                _log?.Error(
                    $"EPB[{channel}] 周期硬截止后的高优先级 OFF 未确认，" +
                    "不得把运行位图伪装为已断电；将升级整批安全停止。",
                    "周期屏障");
            var isolated = await LatchPermanentPeriodAlarmAsync(
                    channel,
                    timer,
                    "PeriodOverrunHardLimit",
                    $"卡钳自身动作在硬截止 {PeriodMs * 2L}ms 时仍未安全终止；" +
                    $"Cycle={cycleNumber}")
                .ConfigureAwait(false);

            // Runner 的取消路径必须有机会执行 finally/OFF；迟到任务即使仍返回，
            // 执行许可与代次栅栏也已撤销，不能再次上电。
            try
            {
                var grace = Task.Delay(Math.Max(1000, Math.Min(5000, PeriodMs / 3)));
                var completed = await Task.WhenAny(runnerTask, grace).ConfigureAwait(false);
                if (completed == runnerTask)
                    await runnerTask.ConfigureAwait(false);
                else
                    ObserveBackgroundTask(runnerTask, "PeriodHardLimitLateRunner", channel);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log?.Warn(
                    $"EPB[{channel}] 硬截止取消后的迟到Runner退出异常：{ex.Message}",
                    "周期屏障");
            }
            return false;
        }

        private async Task<bool> LatchPermanentPeriodAlarmAsync(
            int channel,
            HighPrecisionTimer timer,
            string code,
            string reason)
        {
            var correlationId = Guid.NewGuid();
            _nonRecoverableChannelFaultLatch[channel] = 0;
            _nonRecoverableChannelFaultReasons[channel] = reason;
            _alarmStopLatch.TryRequestStop(channel);
            timer?.Stop();
            var safe = RevokeChannelExecutionBeforeTerminalState(channel, code);
            var persisted = PersistentlyDisableChannels(
                new[] { channel },
                code,
                reason,
                correlationId);

            PublishChannelRuntimeState(
                channel,
                safe && persisted
                    ? ChannelRuntimeState.AlarmStopped
                    : ChannelRuntimeState.SystemFault,
                safe && persisted ? code : code + "SafetyOrPersistenceFailed",
                safe && persisted
                    ? "永久报警已锁存；重新勾选或右键人工重置前保持禁用"
                    : "永久报警隔离后的 OFF、安全释放或项目持久化未确认；整批停止",
                channel,
                new[] { channel },
                correlationId,
                allowTerminalReset: false,
                allowSystemFaultReset: false);
            if (!safe || !persisted)
            {
                await EscalatePeriodAlarmPersistenceFailureAsync(
                        channel,
                        !safe
                            ? "PeriodAlarmSafetyClosureFailed"
                            : "PeriodAlarmPersistenceFailed")
                    .ConfigureAwait(false);
            }
            return true;
        }

        private bool TryPersistPeriodOverrunState(
            int channel,
            int count,
            DateTime? lastOverrunUtc,
            out string error)
        {
            error = string.Empty;
            try
            {
                var test = _cfg.Test;
                EpbTestRecord record;
                lock (test)
                {
                    record = test.GetEpbRecord(channel);
                    record.ConsecutivePeriodOverrunCount = Math.Max(0, count);
                    record.LastPeriodOverrunUtc = lastOverrunUtc;
                    _periodOverrunStreaks[channel] = record.ConsecutivePeriodOverrunCount;
                }

                var projectPath = ConfigLoader.GetProjectTestConfigPath(
                    test.StoreDir,
                    test.TestName);
                if (string.IsNullOrWhiteSpace(projectPath) || !System.IO.File.Exists(projectPath))
                    throw new System.IO.FileNotFoundException(
                        "当前项目专用 TestConfig.xml 不存在，禁止回退修改默认模板。",
                        projectPath);
                ConfigLoader.UpdateTestEpbAlarmState(
                    projectPath,
                    new[]
                    {
                        new EpbAlarmPersistenceUpdate
                        {
                            Channel = channel,
                            Enabled = null,
                            PermanentAlarmLatched = record.PermanentAlarmLatched,
                            PermanentAlarmCode = record.PermanentAlarmCode,
                            PermanentAlarmReason = record.PermanentAlarmReason,
                            PermanentAlarmUtc = record.PermanentAlarmUtc,
                            PermanentAlarmCorrelationId = record.PermanentAlarmCorrelationId,
                            ConsecutivePeriodOverrunCount = record.ConsecutivePeriodOverrunCount,
                            LastPeriodOverrunUtc = record.LastPeriodOverrunUtc
                        }
                    });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _log?.Error(
                    $"EPB[{channel}] 周期超限状态持久化失败：{ex.Message}",
                    "周期屏障",
                    ex);
                return false;
            }
        }

        private async Task EscalatePeriodAlarmPersistenceFailureAsync(
            int channel,
            string reason)
        {
            try { CommandEpbOffHighPriority(channel, reason); } catch { }
            try
            {
                await StopAllAsync(
                        new StopContext
                        {
                            Source = StopSource.SystemFault,
                            Reason = reason,
                            Initiator = nameof(EvaluateCommittedPeriodOverrunAsync),
                            CorrelationId = Guid.NewGuid().ToString("N"),
                            RequestedUtc = DateTime.UtcNow
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Error("周期报警持久化失败后的整批安全停止异常。", "周期屏障", ex);
            }
        }

        public PermanentAlarmResetResult ResetPermanentAlarm(
            int channel,
            bool enableAfterReset)
        {
            if (channel < 1 || channel > 12)
                return new PermanentAlarmResetResult { Error = "卡钳通道超出范围。" };
            if (IsChannelEnergized(channel) ||
                _timers.ContainsKey(channel) ||
                _timerCache.ContainsKey(channel) ||
                _runners.ContainsKey(channel) ||
                _runnerCache.ContainsKey(channel) ||
                _cycleAttempts.TryGetCurrent(channel, out _))
                return new PermanentAlarmResetResult
                {
                    Error = "卡钳仍在运行或安全收口未完成，不能重置永久报警。"
                };

            try
            {
                var test = _cfg.Test;
                var projectPath = ConfigLoader.GetProjectTestConfigPath(
                    test.StoreDir,
                    test.TestName);
                if (string.IsNullOrWhiteSpace(projectPath) || !System.IO.File.Exists(projectPath))
                    throw new System.IO.FileNotFoundException(
                        "当前项目专用 TestConfig.xml 不存在。",
                        projectPath);

                ConfigLoader.UpdateTestEpbAlarmState(
                    projectPath,
                    new[]
                    {
                        new EpbAlarmPersistenceUpdate
                        {
                            Channel = channel,
                            Enabled = enableAfterReset,
                            PermanentAlarmLatched = false,
                            PermanentAlarmCode = string.Empty,
                            PermanentAlarmReason = string.Empty,
                            PermanentAlarmUtc = null,
                            PermanentAlarmCorrelationId = Guid.Empty,
                            ConsecutivePeriodOverrunCount = 0,
                            LastPeriodOverrunUtc = null
                        }
                    });

                lock (test)
                {
                    var record = test.GetEpbRecord(channel);
                    record.ClearPermanentAlarm();
                    record.Enabled = enableAfterReset;
                }
                _periodOverrunStreaks[channel] = 0;
                _nonRecoverableChannelFaultLatch.TryRemove(channel, out _);
                _nonRecoverableChannelFaultReasons.TryRemove(channel, out _);
                _disablePersistenceFailureReasons.TryRemove(channel, out _);
                _alarmStopLatch.BeginRun(channel);
                PublishChannelRuntimeState(
                    channel,
                    enableAfterReset
                        ? ChannelRuntimeState.ManualStopped
                        : ChannelRuntimeState.NotEnabled,
                    "PermanentAlarmReset",
                    enableAfterReset
                        ? "永久报警已清除并启用；等待完整预检后人工开始"
                        : "永久报警已清除；通道保持未勾选",
                    allowTerminalReset: true,
                    allowSystemFaultReset: true);
                return new PermanentAlarmResetResult
                {
                    Succeeded = true,
                    Enabled = enableAfterReset
                };
            }
            catch (Exception ex)
            {
                return new PermanentAlarmResetResult { Error = ex.Message };
            }
        }
    }
}
