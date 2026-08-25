using System;
using Controller.Adaptive;
using Controller.Alarm;

namespace Controller
{
    internal enum EpbManagerAdaptiveLifecycleEventKind
    {
        Warning = 0,
        Alarm = 1,
        RecoverableFault = 2,
        WarningEvidence = 3,
        Decision = 4,
        Diagnostic = 5,
        DiagnosticSummary = 6,
        ChannelPaused = 7,
        RuntimeStateChanged = 8,
        TimerPause = 9,
        SafeIdle = 10,
        Stop = 11,
        SystemFault = 12
    }

    internal readonly struct EpbManagerAdaptiveLifecycleEventIdentity
    {
        internal EpbManagerAdaptiveLifecycleEventIdentity(Guid runId, long runEpoch)
        {
            RunId = runId;
            RunEpoch = runEpoch;
        }

        internal Guid RunId { get; }
        internal long RunEpoch { get; }
    }

    /// <summary>
    /// A single, non-blocking seam for the adaptive Runner lifecycle.  The
    /// production manager supplies its control/diagnostic handlers here and
    /// the exact same binding can be supplied with a recording observer in a
    /// deterministic replay.  A diagnostic event only reaches the diagnostic
    /// callbacks; it has no implicit path to pause/stop/fault actions.
    /// </summary>
    internal sealed class EpbManagerAdaptiveLifecycleEvent
    {
        internal EpbManagerAdaptiveLifecycleEvent(
            EpbManagerAdaptiveLifecycleEventKind kind,
            int channel,
            string reason,
            Guid runId,
            long runEpoch,
            AdaptiveDiagnosticOverlaySnapshot diagnostic = default,
            ChannelRuntimeStateChangedEvent runtimeState = null,
            ControlFault controlFault = null)
        {
            Kind = kind;
            Channel = channel;
            Reason = reason ?? string.Empty;
            RunId = runId;
            RunEpoch = runEpoch;
            Diagnostic = diagnostic;
            RuntimeState = runtimeState;
            ControlFault = controlFault;
        }

        internal EpbManagerAdaptiveLifecycleEventKind Kind { get; }
        internal int Channel { get; }
        internal string Reason { get; }
        internal Guid RunId { get; }
        internal long RunEpoch { get; }
        internal AdaptiveDiagnosticOverlaySnapshot Diagnostic { get; }
        internal ChannelRuntimeStateChangedEvent RuntimeState { get; }
        internal ControlFault ControlFault { get; }
    }

    /// <summary>
    /// Minimal production seam used by EpbManager's Runner attach path.  The
    /// direct Runner handlers are deliberately kept separate from lifecycle
    /// output handlers so a DiagnosticOnly observation cannot accidentally
    /// call a safety side effect.
    /// </summary>
    internal sealed class EpbManagerAdaptiveLifecyclePort
    {
        private readonly Action<int, string> _alarmRaised;
        private readonly Action<int, string> _warningRaised;
        private readonly Action<AdaptiveWarningEvent> _warningEvidenceRaised;
        private readonly Action<int, string> _recoverableFaultRaised;
        private readonly Action<AdaptiveDecisionTraceSample> _decisionObserved;
        private readonly Action<AdaptiveDiagnosticOverlaySnapshot> _diagnosticObserved;
        private readonly Action<AdaptiveDiagnosticOverlaySnapshot> _diagnosticSummaryObserved;
        private readonly Action<EpbManagerAdaptiveLifecycleEvent> _channelPaused;
        private readonly Action<EpbManagerAdaptiveLifecycleEvent> _runtimeStateChanged;
        private readonly Action<EpbManagerAdaptiveLifecycleEvent> _timerPause;
        private readonly Action<EpbManagerAdaptiveLifecycleEvent> _safeIdle;
        private readonly Action<EpbManagerAdaptiveLifecycleEvent> _stop;
        private readonly Action<EpbManagerAdaptiveLifecycleEvent> _systemFault;
        private readonly Action<EpbManagerAdaptiveLifecycleEvent> _observer;
        private readonly Func<int, Guid> _runIdForChannel;
        private readonly Func<int, long> _runEpochForChannel;

        internal EpbManagerAdaptiveLifecyclePort(
            Action<int, string> alarmRaised = null,
            Action<int, string> warningRaised = null,
            Action<AdaptiveWarningEvent> warningEvidenceRaised = null,
            Action<int, string> recoverableFaultRaised = null,
            Action<AdaptiveDecisionTraceSample> decisionObserved = null,
            Action<AdaptiveDiagnosticOverlaySnapshot> diagnosticObserved = null,
            Action<AdaptiveDiagnosticOverlaySnapshot> diagnosticSummaryObserved = null,
            Action<EpbManagerAdaptiveLifecycleEvent> channelPaused = null,
            Action<EpbManagerAdaptiveLifecycleEvent> runtimeStateChanged = null,
            Action<EpbManagerAdaptiveLifecycleEvent> timerPause = null,
            Action<EpbManagerAdaptiveLifecycleEvent> safeIdle = null,
            Action<EpbManagerAdaptiveLifecycleEvent> stop = null,
            Action<EpbManagerAdaptiveLifecycleEvent> systemFault = null,
            Action<EpbManagerAdaptiveLifecycleEvent> observer = null,
            Func<int, Guid> runIdForChannel = null,
            Func<int, long> runEpochForChannel = null)
        {
            _alarmRaised = alarmRaised;
            _warningRaised = warningRaised;
            _warningEvidenceRaised = warningEvidenceRaised;
            _recoverableFaultRaised = recoverableFaultRaised;
            _decisionObserved = decisionObserved;
            _diagnosticObserved = diagnosticObserved;
            _diagnosticSummaryObserved = diagnosticSummaryObserved;
            _channelPaused = channelPaused;
            _runtimeStateChanged = runtimeStateChanged;
            _timerPause = timerPause;
            _safeIdle = safeIdle;
            _stop = stop;
            _systemFault = systemFault;
            _observer = observer;
            _runIdForChannel = runIdForChannel;
            _runEpochForChannel = runEpochForChannel;
        }

        private EpbManagerAdaptiveLifecycleEventIdentity CaptureIdentity(int channel)
        {
            try
            {
                return new EpbManagerAdaptiveLifecycleEventIdentity(
                    _runIdForChannel?.Invoke(channel) ?? Guid.Empty,
                    _runEpochForChannel?.Invoke(channel) ?? 0);
            }
            catch
            {
                return default;
            }
        }

        internal void OnRunnerAlarmRaised(int channel, string reason)
        {
            _alarmRaised?.Invoke(channel, reason);
            var identity = CaptureIdentity(channel);
            Observe(new EpbManagerAdaptiveLifecycleEvent(
                EpbManagerAdaptiveLifecycleEventKind.Alarm,
                channel,
                reason,
                identity.RunId,
                identity.RunEpoch));
        }

        internal void OnRunnerWarningRaised(int channel, string reason)
        {
            _warningRaised?.Invoke(channel, reason);
            var identity = CaptureIdentity(channel);
            Observe(new EpbManagerAdaptiveLifecycleEvent(
                EpbManagerAdaptiveLifecycleEventKind.Warning,
                channel,
                reason,
                identity.RunId,
                identity.RunEpoch));
        }

        internal void OnRunnerWarningEvidenceRaised(AdaptiveWarningEvent warning)
        {
            _warningEvidenceRaised?.Invoke(warning);
            var identity = CaptureIdentity(warning?.Channel ?? 0);
            Observe(new EpbManagerAdaptiveLifecycleEvent(
                EpbManagerAdaptiveLifecycleEventKind.WarningEvidence,
                warning?.Channel ?? 0,
                warning?.Reason,
                identity.RunId,
                identity.RunEpoch));
        }

        internal void OnRunnerRecoverableFaultRaised(int channel, string reason)
        {
            _recoverableFaultRaised?.Invoke(channel, reason);
            var identity = CaptureIdentity(channel);
            Observe(new EpbManagerAdaptiveLifecycleEvent(
                EpbManagerAdaptiveLifecycleEventKind.RecoverableFault,
                channel,
                reason,
                identity.RunId,
                identity.RunEpoch));
        }

        internal void OnRunnerDecisionObserved(AdaptiveDecisionTraceSample decision)
        {
            _decisionObserved?.Invoke(decision);
            var identity = CaptureIdentity(decision.Channel);
            Observe(new EpbManagerAdaptiveLifecycleEvent(
                EpbManagerAdaptiveLifecycleEventKind.Decision,
                decision.Channel,
                decision.Reason,
                identity.RunId == Guid.Empty ? decision.RunId : identity.RunId,
                identity.RunEpoch));
        }

        internal void OnRunnerDiagnosticObserved(AdaptiveDiagnosticOverlaySnapshot snapshot)
        {
            _diagnosticObserved?.Invoke(snapshot);
            Observe(new EpbManagerAdaptiveLifecycleEvent(
                EpbManagerAdaptiveLifecycleEventKind.Diagnostic,
                snapshot.Key.Channel,
                snapshot.Key.DiagnosticCode,
                snapshot.Key.RunId,
                snapshot.Key.RunEpoch,
                diagnostic: snapshot));
        }

        internal void OnRunnerDiagnosticSummaryObserved(AdaptiveDiagnosticOverlaySnapshot snapshot)
        {
            _diagnosticSummaryObserved?.Invoke(snapshot);
            Observe(new EpbManagerAdaptiveLifecycleEvent(
                EpbManagerAdaptiveLifecycleEventKind.DiagnosticSummary,
                snapshot.Key.Channel,
                snapshot.Key.DiagnosticCode,
                snapshot.Key.RunId,
                snapshot.Key.RunEpoch,
                diagnostic: snapshot));
        }

        internal void PublishChannelPaused(int channel, Guid runId = default, long runEpoch = 0)
        {
            PublishLifecycle(
                _channelPaused,
                new EpbManagerAdaptiveLifecycleEvent(
                    EpbManagerAdaptiveLifecycleEventKind.ChannelPaused,
                    channel,
                    "ChannelPaused",
                    runId,
                    runEpoch));
        }

        internal void PublishRuntimeState(ChannelRuntimeStateChangedEvent state)
        {
            if (state == null) return;
            PublishLifecycle(
                _runtimeStateChanged,
                new EpbManagerAdaptiveLifecycleEvent(
                    EpbManagerAdaptiveLifecycleEventKind.RuntimeStateChanged,
                    state.Channel,
                    state.ReasonCode,
                    state.RunId,
                    state.RunEpoch,
                    runtimeState: state));
        }

        internal void PublishTimerPause(
            int channel,
            string reason,
            Guid runId = default,
            long runEpoch = 0)
        {
            PublishLifecycle(
                _timerPause,
                new EpbManagerAdaptiveLifecycleEvent(
                    EpbManagerAdaptiveLifecycleEventKind.TimerPause,
                    channel,
                    reason,
                    runId,
                    runEpoch));
        }

        internal void PublishSafeIdle(
            int channel,
            string reason,
            Guid runId = default,
            long runEpoch = 0)
        {
            PublishLifecycle(
                _safeIdle,
                new EpbManagerAdaptiveLifecycleEvent(
                    EpbManagerAdaptiveLifecycleEventKind.SafeIdle,
                    channel,
                    reason,
                    runId,
                    runEpoch));
        }

        internal void PublishStop(
            int channel,
            string reason,
            Guid runId = default,
            long runEpoch = 0)
        {
            PublishLifecycle(
                _stop,
                new EpbManagerAdaptiveLifecycleEvent(
                    EpbManagerAdaptiveLifecycleEventKind.Stop,
                    channel,
                    reason,
                    runId,
                    runEpoch));
        }

        internal void PublishSystemFault(
            ControlFault fault,
            Guid runId = default,
            long runEpoch = 0)
        {
            var evt = new EpbManagerAdaptiveLifecycleEvent(
                EpbManagerAdaptiveLifecycleEventKind.SystemFault,
                fault?.AffectedChannels != null && fault.AffectedChannels.Length > 0
                    ? fault.AffectedChannels[0]
                    : 0,
                fault?.Reason,
                runId,
                runEpoch,
                controlFault: fault);
            PublishLifecycle(_systemFault, evt);
        }

        private void PublishLifecycle(
            Action<EpbManagerAdaptiveLifecycleEvent> handler,
            EpbManagerAdaptiveLifecycleEvent evt)
        {
            handler?.Invoke(evt);
            Observe(evt);
        }

        private void Observe(EpbManagerAdaptiveLifecycleEvent evt)
        {
            _observer?.Invoke(evt);
        }
    }

    /// <summary>
    /// The single production Runner-to-Manager event binding component.  It is
    /// deliberately stateless so test seams can use the exact same attach and
    /// detach code without constructing the hardware-backed manager.
    /// </summary>
    internal static class AdaptiveRunnerEventBinding
    {
        internal static void Attach(
            EpbCycleRunner runner,
            EpbManagerAdaptiveLifecyclePort port)
        {
            if (runner == null || port == null) return;
            runner.AlarmRaised -= port.OnRunnerAlarmRaised;
            runner.AlarmRaised += port.OnRunnerAlarmRaised;
            runner.WarningRaised -= port.OnRunnerWarningRaised;
            runner.WarningRaised += port.OnRunnerWarningRaised;
            runner.WarningEvidenceRaised -= port.OnRunnerWarningEvidenceRaised;
            runner.WarningEvidenceRaised += port.OnRunnerWarningEvidenceRaised;
            runner.RecoverableFaultRaised -= port.OnRunnerRecoverableFaultRaised;
            runner.RecoverableFaultRaised += port.OnRunnerRecoverableFaultRaised;
            runner.AdaptiveDecisionObserved -= port.OnRunnerDecisionObserved;
            runner.AdaptiveDecisionObserved += port.OnRunnerDecisionObserved;
            runner.AdaptiveDiagnosticObserved -= port.OnRunnerDiagnosticObserved;
            runner.AdaptiveDiagnosticObserved += port.OnRunnerDiagnosticObserved;
            runner.AdaptiveDiagnosticSummaryObserved -= port.OnRunnerDiagnosticSummaryObserved;
            runner.AdaptiveDiagnosticSummaryObserved += port.OnRunnerDiagnosticSummaryObserved;
        }

        internal static void Attach(
            EpbCycleRunner runner,
            Action<int, string> alarm,
            Action<int, string> warning,
            Action<AdaptiveWarningEvent> warningEvidence,
            Action<int, string> recoverableFault,
            Action<AdaptiveDecisionTraceSample> decision,
            Action<AdaptiveDiagnosticOverlaySnapshot> diagnostic,
            Action<AdaptiveDiagnosticOverlaySnapshot> diagnosticSummary)
        {
            if (runner == null) return;
            runner.AlarmRaised -= alarm;
            runner.AlarmRaised += alarm;
            runner.WarningRaised -= warning;
            runner.WarningRaised += warning;
            runner.WarningEvidenceRaised -= warningEvidence;
            runner.WarningEvidenceRaised += warningEvidence;
            runner.RecoverableFaultRaised -= recoverableFault;
            runner.RecoverableFaultRaised += recoverableFault;
            runner.AdaptiveDecisionObserved -= decision;
            runner.AdaptiveDecisionObserved += decision;
            runner.AdaptiveDiagnosticObserved -= diagnostic;
            runner.AdaptiveDiagnosticObserved += diagnostic;
            runner.AdaptiveDiagnosticSummaryObserved -= diagnosticSummary;
            runner.AdaptiveDiagnosticSummaryObserved += diagnosticSummary;
        }

        internal static void Detach(
            EpbCycleRunner runner,
            EpbManagerAdaptiveLifecyclePort port,
            bool flushDiagnosticOverlay)
        {
            if (runner == null || port == null) return;
            if (flushDiagnosticOverlay)
            {
                try { runner.FlushAdaptiveDiagnosticOverlay(); }
                catch { /* diagnostic finalization is non-critical */ }
            }
            runner.AlarmRaised -= port.OnRunnerAlarmRaised;
            runner.WarningRaised -= port.OnRunnerWarningRaised;
            runner.WarningEvidenceRaised -= port.OnRunnerWarningEvidenceRaised;
            runner.RecoverableFaultRaised -= port.OnRunnerRecoverableFaultRaised;
            runner.AdaptiveDecisionObserved -= port.OnRunnerDecisionObserved;
            runner.AdaptiveDiagnosticObserved -= port.OnRunnerDiagnosticObserved;
            runner.AdaptiveDiagnosticSummaryObserved -= port.OnRunnerDiagnosticSummaryObserved;
        }

        internal static void Detach(
            EpbCycleRunner runner,
            Action<int, string> alarm,
            Action<int, string> warning,
            Action<AdaptiveWarningEvent> warningEvidence,
            Action<int, string> recoverableFault,
            Action<AdaptiveDecisionTraceSample> decision,
            Action<AdaptiveDiagnosticOverlaySnapshot> diagnostic,
            Action<AdaptiveDiagnosticOverlaySnapshot> diagnosticSummary,
            bool flushDiagnosticOverlay)
        {
            if (runner == null) return;
            if (flushDiagnosticOverlay)
            {
                try { runner.FlushAdaptiveDiagnosticOverlay(); }
                catch { /* diagnostic finalization is non-critical */ }
            }
            runner.AlarmRaised -= alarm;
            runner.WarningRaised -= warning;
            runner.WarningEvidenceRaised -= warningEvidence;
            runner.RecoverableFaultRaised -= recoverableFault;
            runner.AdaptiveDecisionObserved -= decision;
            runner.AdaptiveDiagnosticObserved -= diagnostic;
            runner.AdaptiveDiagnosticSummaryObserved -= diagnosticSummary;
        }
    }

    public sealed partial class EpbManager
    {
        /// <summary>
        /// The exact port used by production AttachRunnerEvents.  It is
        /// internal so deterministic tests can subscribe to the same seam
        /// without constructing a second binding implementation.
        /// </summary>
        internal EpbManagerAdaptiveLifecyclePort AdaptiveLifecyclePort =>
            _adaptiveLifecyclePort;

        private readonly AdaptiveDecisionTraceBuffer _adaptiveDecisionTrace =
            new AdaptiveDecisionTraceBuffer();

        /// <summary>
        /// Runner 的 DiagnosticOnly 事件实际经过 Manager 绑定链；它只进入
        /// 观察/审计通道，不调用 ChannelWarningRaised、暂停、停机或故障连续数。
        /// </summary>
        internal event Action<AdaptiveDiagnosticOverlaySnapshot> AdaptiveDiagnosticObserved;
        internal event Action<AdaptiveDiagnosticOverlaySnapshot> AdaptiveDiagnosticSummaryObserved;

        private void OnRunnerAdaptiveDecisionObserved(AdaptiveDecisionTraceSample item)
        {
            _runIdByChannel.TryGetValue(item.Channel, out var runId);
            _currentCycleNumberByChannel.TryGetValue(item.Channel, out var cycleNumber);
            item.RunId = runId;
            item.CycleNumber = cycleNumber;
            _adaptiveDecisionTrace.Append(item);
        }

        private void OnRunnerAdaptiveDiagnosticObserved(AdaptiveDiagnosticOverlaySnapshot snapshot)
        {
            NonCriticalObserver.Invoke(
                AdaptiveDiagnosticObserved,
                snapshot,
                ex => _log?.Warn(
                    $"DiagnosticOnly明细观察者异常，已隔离：{ex.Message}",
                    "EPB-DIAGNOSTIC"));
        }

        private void OnRunnerAdaptiveDiagnosticSummaryObserved(AdaptiveDiagnosticOverlaySnapshot snapshot)
        {
            NonCriticalObserver.Invoke(
                AdaptiveDiagnosticSummaryObserved,
                snapshot,
                ex => _log?.Warn(
                    $"DiagnosticOnly汇总观察者异常，已隔离：{ex.Message}",
                    "EPB-DIAGNOSTIC"));
        }
    }
}
