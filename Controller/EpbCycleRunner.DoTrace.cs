using System;
using System.Runtime.CompilerServices;
using Controller.Alarm;
using IO.NI;

namespace Controller
{
    internal sealed class EpbOutputCommandException : InvalidOperationException
    {
        public EpbOutputCommandException(int channel, string command)
            : base($"EpbOutputCommandFailed Channel={channel} Command={command}") { }
    }

    public sealed partial class EpbCycleRunner
    {
        private bool CommandForward([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbForward(_channel, stage)
                : _do.SetEpbForward(_channel);
        }

        private bool CommandReverse([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbReverse(_channel, stage)
                : _do.SetEpbReverse(_channel);
        }

        private bool CommandOff([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbOff(_channel, stage)
                : _do.SetEpbOff(_channel);
        }

        private bool CommandOffHighPriority([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbOffHighPriority(_channel, stage)
                : _do.SetEpbOffHighPriority(_channel);
        }

        /// <summary>
        /// DAQ/快速自适应判定专用。返回值仅表示 worker 已接纳，不表示物理 OFF 成功。
        /// </summary>
        private bool TrySubmitCommandOffHighPriority(
            Action<HighPriorityDoTelemetry> completion,
            out Guid commandId)
        {
            return _manager != null
                ? _manager.TrySubmitEpbOffHighPriority(_channel, completion, out commandId)
                : _do.TrySubmitEpbOffHighPriority(_channel, completion, out commandId);
        }

        private void RequireMotorCommandSucceeded(bool succeeded, string command)
        {
            if (succeeded) return;
            var exception = new EpbOutputCommandException(_channel, command);
            NotifyAlarmSafely("AdaptiveHardFault " + exception.Message);
            throw exception;
        }

        private void NotifyAlarmSafely(string reason)
        {
            NonCriticalObserver.Invoke(
                AlarmRaised,
                _channel,
                reason ?? string.Empty,
                ex => _log?.Warn(
                    $"EPB[{_channel}] 报警观察者异常已隔离：{ex.Message}",
                    "EPB"));
        }

        private void NotifyWarningSafely(string reason)
        {
            NonCriticalObserver.Invoke(
                WarningRaised,
                _channel,
                reason ?? string.Empty,
                ex => _log?.Warn(
                    $"EPB[{_channel}] 预警观察者异常已隔离：{ex.Message}",
                    "EPB"));
        }

        private void NotifyRecoverableFaultSafely(string reason)
        {
            NonCriticalObserver.Invoke(
                RecoverableFaultRaised,
                _channel,
                reason ?? string.Empty,
                ex => _log?.Warn(
                    $"EPB[{_channel}] 自愈观察者异常已隔离：{ex.Message}",
                    "EPB"));
        }

        private void NotifyWarningEvidenceSafely(AdaptiveWarningEvent warning)
        {
            NonCriticalObserver.Invoke(
                WarningEvidenceRaised,
                warning,
                ex => _log?.Warn(
                    $"EPB[{_channel}] 预警证据观察者异常已隔离：{ex.Message}",
                    "EPB"));
        }
    }
}
