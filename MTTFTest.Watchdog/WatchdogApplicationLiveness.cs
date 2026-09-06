using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal enum WatchdogUiProbeStatus
    {
        NotRequired = 0,
        StartupGrace = 1,
        Throttled = 2,
        Responsive = 3,
        Unresponsive = 4,
        WindowMissing = 5,
        ProcessExited = 6,
        IdentityMismatch = 7,
        ProbeError = 8
    }

    internal sealed class WatchdogUiProbeResult
    {
        internal WatchdogUiProbeStatus Status { get; set; }
        internal IntPtr WindowHandle { get; set; }
        internal string Detail { get; set; }
    }

    internal interface IWatchdogUiProbe
    {
        WatchdogUiProbeResult Probe(int processId, long processStartUtcTicks, int timeoutMs);
    }

    internal sealed class WindowsWatchdogUiProbe : IWatchdogUiProbe
    {
        private const uint WmNull = 0x0000;
        private const uint SmtoBlock = 0x0001;
        private const uint SmtoAbortIfHung = 0x0002;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        public WatchdogUiProbeResult Probe(
            int processId,
            long processStartUtcTicks,
            int timeoutMs)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                        return Result(WatchdogUiProbeStatus.ProcessExited, IntPtr.Zero, "ProcessExited");
                    if (process.StartTime.ToUniversalTime().Ticks != processStartUtcTicks)
                        return Result(WatchdogUiProbeStatus.IdentityMismatch, IntPtr.Zero, "ProcessStartUtcTicksMismatch");
                }

                var handle = FindVisibleTopLevelWindow(processId);
                if (handle == IntPtr.Zero)
                    return Result(WatchdogUiProbeStatus.WindowMissing, handle, "VisibleTopLevelWindowMissing");

                IntPtr messageResult;
                var sent = SendMessageTimeout(
                    handle,
                    WmNull,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    SmtoBlock | SmtoAbortIfHung,
                    (uint)Math.Max(1, timeoutMs),
                    out messageResult);
                return sent != IntPtr.Zero
                    ? Result(WatchdogUiProbeStatus.Responsive, handle, "WmNullAcknowledged")
                    : Result(
                        WatchdogUiProbeStatus.Unresponsive,
                        handle,
                        "SendMessageTimeoutError=" + Marshal.GetLastWin32Error());
            }
            catch (ArgumentException)
            {
                return Result(WatchdogUiProbeStatus.ProcessExited, IntPtr.Zero, "ProcessMissing");
            }
            catch (InvalidOperationException)
            {
                return Result(WatchdogUiProbeStatus.ProcessExited, IntPtr.Zero, "ProcessExitedDuringProbe");
            }
            catch (Exception ex)
            {
                return Result(WatchdogUiProbeStatus.ProbeError, IntPtr.Zero, ex.GetBaseException().Message);
            }
        }

        private static IntPtr FindVisibleTopLevelWindow(int processId)
        {
            var found = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != (uint)processId || !IsWindowVisible(window)) return true;
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static WatchdogUiProbeResult Result(
            WatchdogUiProbeStatus status,
            IntPtr handle,
            string detail)
        {
            return new WatchdogUiProbeResult
            {
                Status = status,
                WindowHandle = handle,
                Detail = detail ?? string.Empty
            };
        }
    }

    internal sealed class WatchdogApplicationLivenessDecision
    {
        internal WatchdogUiProbeStatus ProbeStatus { get; set; }
        internal bool SuppressHeartbeatTakeover { get; set; }
        internal bool HeartbeatUnresponsiveConfirmed { get; set; }
        internal bool RetireConnection { get; set; }
        internal bool ReportEvent { get; set; }
        internal int ConsecutiveFailures { get; set; }
        internal string Detail { get; set; }
    }

    internal sealed class WatchdogApplicationLivenessSupervisor
    {
        internal const int HeartbeatTakeoverSeconds =
            WatchdogTransportPolicy.HeartbeatTimeoutMs / 1000;
        internal const int StartupGraceSeconds = 15;
        internal const int ProbeIntervalMs = 1000;
        internal const int ProbeTimeoutMs = WatchdogTransportPolicy.UiProbeTimeoutMs;
        internal const int FailuresBeforeTakeover = 3;

        private readonly IWatchdogUiProbe _probe;
        private long _attachEpoch;
        private long _attachedTimestamp;
        private long _mainUiReadyEpoch;
        private long _lastProbeTimestamp;
        private long _lastRetiredConnectionGeneration;
        private int _consecutiveFailures;
        private bool _confirmedFailureReported;

        internal WatchdogApplicationLivenessSupervisor(IWatchdogUiProbe probe)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        internal void ResetForNewProcess(long attachEpoch, long nowTimestamp)
        {
            _attachEpoch = attachEpoch;
            _attachedTimestamp = nowTimestamp;
            _mainUiReadyEpoch = 0;
            _lastProbeTimestamp = 0;
            _lastRetiredConnectionGeneration = 0;
            _consecutiveFailures = 0;
            _confirmedFailureReported = false;
        }

        internal void ObserveMainUiReady(long attachEpoch)
        {
            if (attachEpoch > 0 && attachEpoch == _attachEpoch)
                _mainUiReadyEpoch = attachEpoch;
        }

        internal void ObserveHeartbeat()
        {
            _consecutiveFailures = 0;
            _lastProbeTimestamp = 0;
            _confirmedFailureReported = false;
        }

        internal WatchdogApplicationLivenessDecision Evaluate(
            double heartbeatAgeSeconds,
            bool processAlive,
            int processId,
            long processStartUtcTicks,
            long attachEpoch,
            long connectionGeneration,
            long nowTimestamp,
            long timestampFrequency)
        {
            if (!processAlive)
                return Confirmed(WatchdogUiProbeStatus.ProcessExited, "ProcessExited");
            if (heartbeatAgeSeconds < HeartbeatTakeoverSeconds)
                return new WatchdogApplicationLivenessDecision
                {
                    ProbeStatus = WatchdogUiProbeStatus.NotRequired
                };

            var ready = attachEpoch > 0 && _mainUiReadyEpoch == attachEpoch;
            var attachedAgeSeconds = _attachedTimestamp <= 0 || timestampFrequency <= 0
                ? 0
                : Math.Max(0, (nowTimestamp - _attachedTimestamp) / (double)timestampFrequency);
            if (!ready && attachedAgeSeconds < StartupGraceSeconds)
                return new WatchdogApplicationLivenessDecision
                {
                    ProbeStatus = WatchdogUiProbeStatus.StartupGrace,
                    SuppressHeartbeatTakeover = true,
                    ConsecutiveFailures = _consecutiveFailures,
                    Detail = "StartupGraceSeconds=" + attachedAgeSeconds.ToString("F3")
                };

            if (_lastProbeTimestamp > 0 && timestampFrequency > 0 &&
                (nowTimestamp - _lastProbeTimestamp) * 1000.0 / timestampFrequency < ProbeIntervalMs)
                return new WatchdogApplicationLivenessDecision
                {
                    ProbeStatus = WatchdogUiProbeStatus.Throttled,
                    SuppressHeartbeatTakeover = _consecutiveFailures < FailuresBeforeTakeover,
                    HeartbeatUnresponsiveConfirmed = _consecutiveFailures >= FailuresBeforeTakeover,
                    ConsecutiveFailures = _consecutiveFailures,
                    Detail = "ProbeInterval"
                };

            _lastProbeTimestamp = nowTimestamp;
            var result = _probe.Probe(processId, processStartUtcTicks, ProbeTimeoutMs) ??
                         new WatchdogUiProbeResult
                         {
                             Status = WatchdogUiProbeStatus.ProbeError,
                             Detail = "ProbeReturnedNull"
                         };
            if (result.Status == WatchdogUiProbeStatus.Responsive)
            {
                _consecutiveFailures = 0;
                var retire = connectionGeneration > 0 &&
                             _lastRetiredConnectionGeneration != connectionGeneration;
                if (retire) _lastRetiredConnectionGeneration = connectionGeneration;
                return new WatchdogApplicationLivenessDecision
                {
                    ProbeStatus = result.Status,
                    SuppressHeartbeatTakeover = true,
                    RetireConnection = retire,
                    ReportEvent = retire,
                    Detail = result.Detail
                };
            }
            if (result.Status == WatchdogUiProbeStatus.ProcessExited ||
                result.Status == WatchdogUiProbeStatus.IdentityMismatch)
                return Confirmed(result.Status, result.Detail);

            _consecutiveFailures = Math.Min(
                FailuresBeforeTakeover,
                _consecutiveFailures + 1);
            var confirmed = _consecutiveFailures >= FailuresBeforeTakeover;
            var reportEvent = !confirmed || !_confirmedFailureReported;
            if (confirmed) _confirmedFailureReported = true;
            return new WatchdogApplicationLivenessDecision
            {
                ProbeStatus = result.Status,
                SuppressHeartbeatTakeover = !confirmed,
                HeartbeatUnresponsiveConfirmed = confirmed,
                ReportEvent = reportEvent,
                ConsecutiveFailures = _consecutiveFailures,
                Detail = result.Detail
            };
        }

        private WatchdogApplicationLivenessDecision Confirmed(
            WatchdogUiProbeStatus status,
            string detail)
        {
            var reportEvent = !_confirmedFailureReported;
            _confirmedFailureReported = true;
            _consecutiveFailures = FailuresBeforeTakeover;
            return new WatchdogApplicationLivenessDecision
            {
                ProbeStatus = status,
                HeartbeatUnresponsiveConfirmed = true,
                ReportEvent = reportEvent,
                ConsecutiveFailures = _consecutiveFailures,
                Detail = detail
            };
        }
    }
}
