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
        private ChannelExecutionPermit _executionPermit;

        internal void BindExecutionPermit(ChannelExecutionPermit permit)
        {
            if (!permit.Authorized)
                throw new InvalidOperationException(
                    $"EPB[{_channel}] Runner不能绑定未授权的执行许可。");
            if (_executionPermit.Authorized &&
                (_executionPermit.Generation != permit.Generation ||
                 _executionPermit.RunEpoch != permit.RunEpoch))
                throw new InvalidOperationException(
                    $"EPB[{_channel}] 禁止把旧Runner重新绑定到新执行代次。");
            _executionPermit = permit;
        }

        internal bool IsBoundToExecutionPermit(ChannelExecutionPermit permit)
        {
            return _executionPermit.Authorized && permit.Authorized &&
                   _executionPermit.Channel == permit.Channel &&
                   _executionPermit.RunEpoch == permit.RunEpoch &&
                   _executionPermit.Generation == permit.Generation;
        }

        private bool CommandForward([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbForward(_channel, stage, _executionPermit)
                : _do.SetEpbForward(_channel);
        }

        private bool CommandReverse([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbReverse(_channel, stage, _executionPermit)
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
            var warning = new AdaptiveWarningEvent
            {
                Channel = _channel,
                Code = AdaptiveWarningCode.GenericAdaptiveWarning,
                OccurredUtc = DateTime.UtcNow,
                Reason = reason ?? string.Empty
            };
            NotifyWarningOverlaySafely(warning);
            NotifyWarningMessageSafely(reason);
        }

        private void NotifyWarningMessageSafely(string reason)
        {
            NonCriticalObserver.Invoke(
                WarningRaised,
                _channel,
                reason ?? string.Empty,
                ex => _log?.Warn(
                    $"EPB[{_channel}] 预警观察者异常已隔离：{ex.Message}",
                    "EPB"));
        }

        private void NotifyWarningOverlaySafely(AdaptiveWarningEvent warning)
        {
            if (warning == null) return;
            NonCriticalObserver.Invoke(
                WarningOverlayRaised,
                warning,
                ex => _log?.Warn(
                    $"EPB[{_channel}] 预警状态观察者异常已隔离：{ex.Message}",
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
