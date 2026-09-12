using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Config;
using DataOperation;
using MTEmbTest;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    internal sealed class WinFormsWatchdogUiBindingReceipt
    {
        internal bool Accepted { get; }
        internal bool Ready { get; }
        internal string Reason { get; }
        internal RuntimeTransportSessionContext Context { get; }
        internal RuntimeSafetyTargetLease TargetLease { get; }
        internal WatchdogRuntime.RuntimeStopAllHandlerLease HandlerLease { get; }
        internal long PipelineGeneration { get; }
        internal string SessionId { get; }
        internal long SessionGeneration { get; }
        internal long SessionLease { get; }
        internal long AuthorityGeneration { get; }
        internal long AttachedConnectionGeneration { get; }
        internal long AttachEpoch { get; }
        internal int AuthorityProcessId { get; }
        internal long AuthorityProcessStartUtcTicks { get; }
        internal string AuthorityInstanceNonceHash { get; }
        internal string ExactKey { get; }

        private WinFormsWatchdogUiBindingReceipt(
            bool accepted,
            bool ready,
            string reason,
            RuntimeTransportSessionContext context,
            RuntimeSafetyTargetLease targetLease,
            WatchdogRuntime.RuntimeStopAllHandlerLease handlerLease,
            long pipelineGeneration,
            RuntimeValidatedAttachIdentity identity)
        {
            Accepted = accepted;
            Ready = ready;
            Reason = reason ?? string.Empty;
            Context = context;
            TargetLease = targetLease;
            HandlerLease = handlerLease;
            PipelineGeneration = pipelineGeneration;
            SessionId = identity?.SessionId ?? context?.SessionId ?? string.Empty;
            SessionGeneration = identity?.SessionGeneration ?? context?.SessionGeneration ?? 0;
            SessionLease = identity?.SessionLease ?? context?.SessionLease ?? 0;
            AuthorityGeneration = identity?.AuthorityGeneration ?? 0;
            AttachedConnectionGeneration = identity?.AttachedConnectionGeneration ?? 0;
            AttachEpoch = identity?.AttachEpoch ?? 0;
            AuthorityProcessId = identity?.AuthorityProcessId ?? 0;
            AuthorityProcessStartUtcTicks = identity?.AuthorityProcessStartUtcTicks ?? 0;
            AuthorityInstanceNonceHash = identity?.AuthorityInstanceNonceHash ?? string.Empty;
            ExactKey = identity?.ExactKey ?? string.Empty;
        }

        internal static WinFormsWatchdogUiBindingReceipt Rejected(string reason)
        {
            return new WinFormsWatchdogUiBindingReceipt(
                false, false, reason, null, null, null, 0, null);
        }

        internal static WinFormsWatchdogUiBindingReceipt AcceptedReady(
            RuntimeTransportSessionContext context,
            RuntimeSafetyTargetLease targetLease,
            WatchdogRuntime.RuntimeStopAllHandlerLease handlerLease,
            long pipelineGeneration,
            RuntimeValidatedAttachIdentity identity)
        {
            return new WinFormsWatchdogUiBindingReceipt(
                true, true, string.Empty, context, targetLease, handlerLease,
                pipelineGeneration, identity);
        }
    }

    public partial class Main_Frm
    {
        internal sealed class WatchdogTakeoverExitReceipt
        {
            internal WatchdogTakeoverExitReceipt(
                RuntimeShutdownReceipt shutdownReceipt,
                string reason)
            {
                ShutdownReceipt = shutdownReceipt;
                Reason = reason ?? string.Empty;
            }

            internal RuntimeShutdownReceipt ShutdownReceipt { get; }
            internal string Reason { get; }
            internal bool IsTerminal => ShutdownReceipt?.IsTerminal == true;
        }

        // All watchdog UI ownership, binding, retention, and terminal release
        // state lives in this one production adapter.  The form only provides
        // the stable WinForms owner and delegates to it.
        private readonly MainWatchdogUiLifecycleAdapter _watchdogUiAdapter;
        private readonly object _watchdogExitGate = new object();
        private int _watchdogOwnedExitRequested;
        private int _watchdogAllowClose;
        private int _watchdogExitIntent = (int)RuntimeShutdownIntent.ApplicationExit;
        private Task _watchdogCloseTask;
        private Task<RuntimeShutdownReceipt> _watchdogShutdownTask;
        private ApplicationCloseReceipt _applicationCloseReceipt;
        private WatchdogApplicationExitReceipt _applicationExitDeadlineReceipt;
        private int _applicationExitDeadlineArmed;
        private const int ApplicationExitHardDeadlineSeconds = 30;
        private const int ApplicationExitDiagnosticDeadlineSeconds = 25;
        private const int IdleApplicationExitHardDeadlineSeconds = 3;
        private const int ActiveApplicationExitTargetSeconds = 10;

        internal WinFormsWatchdogPostTarget WatchdogPostTarget =>
            _watchdogUiAdapter?.PostTarget;

        internal RuntimeSafetyTargetLease WatchdogTargetLease =>
            _watchdogUiAdapter?.TargetLease;

        internal bool WatchdogUiReadyNotified =>
            _watchdogUiAdapter?.UiReadyNotified == true;

        internal int WatchdogUiResourceReleaseCount =>
            _watchdogUiAdapter?.ResourceReleaseCount ?? 0;

        internal bool WatchdogUiHasResources =>
            _watchdogUiAdapter?.HasResources == true;

        internal bool IsWatchdogMainCloseAuthorized =>
            Volatile.Read(ref _watchdogAllowClose) != 0;

        internal Task<WinFormsWatchdogUiBindingReceipt>
            BindWatchdogUiProductionAsync(FrmEpbMainMonitor monitor)
        {
            // 新精确会话不得继承上一会话的应用退出授权。
            Interlocked.Exchange(ref _watchdogAllowClose, 0);
            _applicationCloseReceipt = null;
            return _watchdogUiAdapter.BindExactAsync(monitor);
        }

        internal Task<WinFormsWatchdogUiBindingReceipt>
            BindWatchdogUiProductionAsync(
                string targetId,
                string subscriberId,
                Func<WatchdogStopAllOfferEnvelope, Task> handler)
        {
            Interlocked.Exchange(ref _watchdogAllowClose, 0);
            _applicationCloseReceipt = null;
            return _watchdogUiAdapter.BindExactAsync(targetId, subscriberId, handler);
        }

        internal void CacheApplicationCloseReceipt(ApplicationCloseReceipt receipt)
        {
            if (receipt?.CanExit != true) return;
            var active = WatchdogRuntime.CaptureTransportSnapshot()?.Context;
            if (!receipt.Matches(active)) return;
            if (_applicationExitDeadlineReceipt != null)
            {
                receipt.RequestedUtc = new DateTime(
                    _applicationExitDeadlineReceipt.RequestedUtcTicks,
                    DateTimeKind.Utc);
                receipt.HardDeadlineUtc = new DateTime(
                    _applicationExitDeadlineReceipt.HardDeadlineUtcTicks,
                    DateTimeKind.Utc);
                receipt.DiagnosticDetail = _applicationExitDeadlineReceipt.Detail ?? string.Empty;
            }
            _applicationCloseReceipt = receipt;
            Interlocked.Exchange(ref _watchdogAllowClose, 1);
        }

        internal bool HasApplicationCloseReceipt =>
            _applicationCloseReceipt?.CanExit == true &&
            _applicationCloseReceipt.Matches(
                WatchdogRuntime.CaptureTransportSnapshot()?.Context);

        internal void NotifyMainUiReadyOnce(string reason)
        {
            _watchdogUiAdapter.NotifyMainUiReadyOnce(reason);
        }

        internal bool HandleWatchdogMainFormClosing(FormClosingEventArgs e)
        {
            if (HasApplicationCloseReceipt)
            {
                Interlocked.Exchange(ref _watchdogAllowClose, 1);
                return false;
            }
            if (Volatile.Read(ref _watchdogAllowClose) != 0) return false;
            var composite = WatchdogRuntime.CaptureTransportSnapshot();
            var retained = WatchdogRuntime.CaptureRetainedShutdown();
            var decision = WinFormsWatchdogUiCloseCoordinator.Evaluate(
                composite, retained, _watchdogUiAdapter.HasResources);
            if (!WinFormsWatchdogUiCloseCoordinator.ShouldCoordinateMainClose(
                    decision.HasActiveEvidence,
                    MdiChildren.Length))
                return false;
            e.Cancel = true;
            var reason = decision.HasActiveEvidence
                ? decision.Reason
                : "MdiChildrenPendingClose";
            RequestWatchdogOwnedExit(
                "MainFormClosing:" + reason,
                RuntimeShutdownIntent.ApplicationExit);
            return true;
        }

        internal void RequestWatchdogOwnedExit(
            string reason,
            RuntimeShutdownIntent shutdownIntent,
            string takeoverTransactionId = null)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((Action)(() => RequestWatchdogOwnedExit(
                        reason,
                        shutdownIntent,
                        takeoverTransactionId)));
                }
                catch { }
                return;
            }
            var preservePermit =
                shutdownIntent == RuntimeShutdownIntent.WatchdogTakeoverExit ||
                shutdownIntent == RuntimeShutdownIntent.WatchdogRecoveryExit;
            if (preservePermit &&
                !Guid.TryParseExact(
                    takeoverTransactionId ?? string.Empty,
                    "N",
                    out _))
                takeoverTransactionId = Guid.NewGuid().ToString("N");
            Interlocked.Exchange(ref _watchdogExitIntent, (int)shutdownIntent);
            if (!ArmApplicationExitDeadlineOnce(
                    reason ?? "WatchdogOwnedExit",
                    shutdownIntent,
                    takeoverTransactionId))
            {
                Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                UseWaitCursor = false;
                Text = BuildWindowTitle() + " - 自动替换许可未形成，已阻止旧进程退出";
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "TakeoverExitBlockedWithoutApprovedPermit " +
                    $"Intent={shutdownIntent};Transaction={takeoverTransactionId};Reason={reason}",
                    "独立看门狗");
                return;
            }
            Interlocked.Exchange(ref _watchdogOwnedExitRequested, 1);
            if (shutdownIntent == RuntimeShutdownIntent.WatchdogTakeoverExit ||
                shutdownIntent == RuntimeShutdownIntent.WatchdogRecoveryExit)
            {
                foreach (var monitor in MdiChildren.OfType<FrmEpbMainMonitor>())
                    monitor.PrepareForWatchdogRetryExit();
            }
            BeginWatchdogClose(reason ?? "WatchdogOwnedExit");
        }

        private bool ArmApplicationExitDeadlineOnce(
            string reason,
            RuntimeShutdownIntent shutdownIntent,
            string takeoverTransactionId)
        {
            if (Interlocked.CompareExchange(
                    ref _applicationExitDeadlineArmed,
                    1,
                    0) != 0)
                return _applicationExitDeadlineReceipt != null ||
                       shutdownIntent == RuntimeShutdownIntent.ApplicationExit;
            var requestedUtc = DateTime.UtcNow;
            var hardDeadlineSeconds = ResolveApplicationExitHardDeadlineSeconds();
            _applicationExitDeadlineReceipt = WatchdogRuntime.ArmApplicationExitDeadline(
                reason,
                TimeSpan.FromSeconds(hardDeadlineSeconds),
                shutdownIntent,
                takeoverTransactionId);
            var preservePermit =
                shutdownIntent == RuntimeShutdownIntent.WatchdogTakeoverExit ||
                shutdownIntent == RuntimeShutdownIntent.WatchdogRecoveryExit;
            if (preservePermit && _applicationExitDeadlineReceipt == null)
            {
                Interlocked.Exchange(ref _applicationExitDeadlineArmed, 0);
                return false;
            }
            var deadlineUtc = _applicationExitDeadlineReceipt?.HardDeadlineUtcTicks > 0
                ? new DateTime(
                    _applicationExitDeadlineReceipt.HardDeadlineUtcTicks,
                    DateTimeKind.Utc)
                : requestedUtc.AddSeconds(hardDeadlineSeconds);
            _ = Task.Run(() => RunLocalApplicationExitDeadlineAsync(
                requestedUtc,
                deadlineUtc,
                reason));
            return true;
        }

        private async Task RunLocalApplicationExitDeadlineAsync(
            DateTime requestedUtc,
            DateTime deadlineUtc,
            string reason)
        {
            var diagnosticsLogged = false;
            var activeTargetLogged = false;
            var totalBudgetSeconds = Math.Max(1, (deadlineUtc - requestedUtc).TotalSeconds);
            var diagnosticSeconds = Math.Min(
                ApplicationExitDiagnosticDeadlineSeconds,
                Math.Max(1, totalBudgetSeconds - 1));
            while (DateTime.UtcNow < deadlineUtc)
            {
                if (HasApplicationCloseReceipt) return;
                var elapsed = DateTime.UtcNow - requestedUtc;
                var remainingSeconds = Math.Max(
                    0,
                    (int)Math.Ceiling((deadlineUtc - DateTime.UtcNow).TotalSeconds));
                if (!diagnosticsLogged &&
                    elapsed >= TimeSpan.FromSeconds(diagnosticSeconds))
                {
                    diagnosticsLogged = true;
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        $"ApplicationExitDiagnosticsDeadline Reason={reason};" +
                        $"RemainingSeconds={remainingSeconds}",
                        "独立看门狗");
                }
                if (!activeTargetLogged &&
                    totalBudgetSeconds > IdleApplicationExitHardDeadlineSeconds &&
                    elapsed >= TimeSpan.FromSeconds(ActiveApplicationExitTargetSeconds))
                {
                    activeTargetLogged = true;
                    ProjectLogHub.Write(
                        ProjectLogLevel.Warning,
                        $"ApplicationExitTargetExceeded Reason={reason};" +
                        $"TargetSeconds={ActiveApplicationExitTargetSeconds};" +
                        "继续等待仅用于完成数据落盘或既有安全交接。",
                        "独立看门狗");
                }
                TryPostApplicationExitCountdown(remainingSeconds);
                var delay = deadlineUtc - DateTime.UtcNow;
                if (delay <= TimeSpan.Zero) break;
                await Task.Delay(delay > TimeSpan.FromSeconds(1)
                        ? TimeSpan.FromSeconds(1)
                        : delay)
                    .ConfigureAwait(false);
            }

            if (HasApplicationCloseReceipt) return;
            WatchdogRuntime.MarkApplicationExitDeadlineForced(
                "LocalMainProcess30SecondDeadline:" + reason);
            ProjectLogHub.Write(
                ProjectLogLevel.Error,
                $"ApplicationExitForcedDeadlineExit Reason={reason};" +
                $"RequestedUtc={requestedUtc:O};DeadlineUtc={deadlineUtc:O}",
                "独立看门狗");
            try
            {
                using (var process = Process.GetCurrentProcess())
                    process.Kill();
            }
            catch
            {
                Environment.Exit(0);
            }
        }

        private int ResolveApplicationExitHardDeadlineSeconds()
        {
            var monitors = MdiChildren.OfType<FrmEpbMainMonitor>().ToArray();
            if (monitors.Length == 0 ||
                monitors.All(monitor => monitor.CanUseIdleFastCloseForApplicationExit))
                return IdleApplicationExitHardDeadlineSeconds;
            return ApplicationExitHardDeadlineSeconds;
        }

        private void TryPostApplicationExitCountdown(int remainingSeconds)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || Disposing) return;
                    UseWaitCursor = true;
                    Text = BuildWindowTitle() +
                           $" - 正在安全退出，最迟{remainingSeconds}秒后结束程序";
                }));
            }
            catch { }
        }

        internal Task<RuntimeShutdownReceipt> ShutdownWatchdogForMonitorCloseAsync(
            string reason,
            RuntimeShutdownIntent shutdownIntent,
            bool idle)
        {
            // 子窗体授权属于同一个主进程退出事务，不能先以 SessionClose
            // 终止 sidecar，再由主窗体补发已经失去接管所有者的替换退出。
            return GetOrCreateWatchdogShutdownTask(
                reason, shutdownIntent, idle ? TimeSpan.FromSeconds(2) : (TimeSpan?)null);
        }

        internal Task<RuntimeShutdownReceipt> ShutdownWatchdogSessionAndReleaseUiAsync(
            string reason)
        {
            return GetOrCreateWatchdogShutdownTask(
                reason,
                RuntimeShutdownIntent.SessionClose);
        }

        internal Task<RuntimeShutdownReceipt> ShutdownIdleWatchdogSessionAndReleaseUiAsync(
            string reason)
        {
            return GetOrCreateWatchdogShutdownTask(
                reason,
                RuntimeShutdownIntent.SessionClose,
                TimeSpan.FromSeconds(2));
        }

        internal Task<RuntimeShutdownReceipt> ShutdownWatchdogForApplicationExitAndReleaseUiAsync(
            string reason)
        {
            return GetOrCreateWatchdogShutdownTask(
                reason,
                RuntimeShutdownIntent.ApplicationExit);
        }

        internal async Task<WatchdogTakeoverExitReceipt>
            ShutdownWatchdogForTakeoverExitAndReleaseUiAsync(string reason)
        {
            var receipt = await GetOrCreateWatchdogShutdownTask(
                    reason,
                    RuntimeShutdownIntent.WatchdogTakeoverExit)
                .ConfigureAwait(true);
            return new WatchdogTakeoverExitReceipt(receipt, reason);
        }

        private Task<RuntimeShutdownReceipt> GetOrCreateWatchdogShutdownTask(
            string reason,
            RuntimeShutdownIntent shutdownIntent,
            TimeSpan? retryWindow = null)
        {
            lock (_watchdogExitGate)
            {
                shutdownIntent = ResolveSharedShutdownIntent(
                    shutdownIntent,
                    Volatile.Read(ref _watchdogOwnedExitRequested) != 0,
                    (RuntimeShutdownIntent)Volatile.Read(ref _watchdogExitIntent));
                if (_watchdogShutdownTask != null)
                {
                    if (!_watchdogShutdownTask.IsCompleted)
                        return _watchdogShutdownTask;
                    if (_watchdogShutdownTask.Status == TaskStatus.RanToCompletion &&
                        _watchdogShutdownTask.Result?.IsTerminal == true)
                    {
                        var active = WatchdogRuntime.CaptureTransportSnapshot()?.Context;
                        var previousReceipt = _watchdogShutdownTask.Result;
                        if (active == null ||
                            (string.Equals(
                                 active.SessionId,
                                 previousReceipt.SessionId,
                                 StringComparison.Ordinal) &&
                             active.SessionLease == previousReceipt.SessionLease))
                            return _watchdogShutdownTask;
                        // 同一个Main_Frm已经绑定到新的精确会话；旧终态回执只能
                        // 服务旧会话的并发调用，不能吞掉新会话的真正Shutdown。
                        _watchdogShutdownTask = null;
                    }
                }

                _watchdogShutdownTask = CompleteSharedWatchdogShutdownAsync(
                    reason,
                    shutdownIntent,
                    retryWindow);
                return _watchdogShutdownTask;
            }
        }

        internal static RuntimeShutdownIntent ResolveSharedShutdownIntent(
            RuntimeShutdownIntent requested, bool ownedExitRequested,
            RuntimeShutdownIntent ownedExitIntent)
        {
            return requested == RuntimeShutdownIntent.SessionClose && ownedExitRequested
                ? ownedExitIntent
                : requested;
        }

        private async Task<RuntimeShutdownReceipt> CompleteSharedWatchdogShutdownAsync(
            string reason,
            RuntimeShutdownIntent shutdownIntent,
            TimeSpan? retryWindow)
        {
            var receipt = await _watchdogUiAdapter.ShutdownAndReleaseAsync(
                    reason,
                    shutdownIntent,
                    retryWindow)
                .ConfigureAwait(false);
            // A transport terminal receipt is the sole close authorization,
            // regardless of whether it was obtained by manual stop, monitor
            // close, unattended completion, or the main-form close path.
            if (receipt?.IsCloseAuthorized == true)
                Interlocked.Exchange(ref _watchdogAllowClose, 1);
            return receipt;
        }

        private void BeginWatchdogClose(string reason)
        {
            if (IsDisposed || Disposing)
            {
                Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                return;
            }
            lock (_watchdogExitGate)
            {
                if (!WinFormsWatchdogUiCloseCoordinator.TryStartCloseAttempt(
                        ref _watchdogCloseTask,
                        () =>
                        {
                            UseWaitCursor = true;
                            Text = BuildWindowTitle() + " - 正在安全退出…";
                            return CompleteWatchdogCloseOnUiThreadAsync(reason);
                        }))
                {
                    UseWaitCursor = true;
                    Text = BuildWindowTitle() + " - 已加入同一安全退出事务";
                    ProjectLogHub.Write(
                        ProjectLogLevel.Info,
                        $"MainProcessExitJoinedExistingTask Reason={reason}",
                        "独立看门狗");
                    return;
                }
            }
        }

        private async Task CompleteWatchdogCloseOnUiThreadAsync(string reason)
        {
            if (IsDisposed || Disposing) return;
            var children = MdiChildren
                .Where(child => child != null && !child.IsDisposed)
                .ToArray();
            // Safety preparation is separated from visual destruction.  The
            // monitor stays visible while StopAll/persistence/DAQ release is
            // running, so a non-terminal Watchdog receipt can never strand a
            // blank MDI shell.
            var monitors = children.OfType<FrmEpbMainMonitor>().ToArray();
            var prepareTasks = monitors
                .Select(monitor => monitor.PrepareForMainApplicationExitAsync())
                .ToArray();
            if (prepareTasks.Length > 0)
            {
                var prepared = Task.WhenAll(prepareTasks);
                var completed = await Task.WhenAny(prepared, Task.Delay(15000));
                if (!ReferenceEquals(completed, prepared))
                {
                    Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                    UseWaitCursor = false;
                    Text = BuildWindowTitle() + " - 安全收口未完成，等待退出监督器";
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        $"MainProcessExitStalled MonitorSafetyPreparationTimeout Reason={reason}",
                        "独立看门狗");
                    return;
                }
                bool[] preparedResults;
                try { preparedResults = await prepared; }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                    UseWaitCursor = false;
                    Text = BuildWindowTitle() + " - 安全收口失败，等待退出监督器";
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "MainProcessExitStalled MonitorSafetyPreparationFailed: " +
                        ex.GetBaseException().Message,
                        "独立看门狗");
                    return;
                }
                if (preparedResults.Any(result => !result))
                {
                    Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                    UseWaitCursor = false;
                    Text = BuildWindowTitle() + " - 安全条件未满足，等待退出监督器";
                    return;
                }
            }

            var applicationReceipts = monitors.Length == 0
                ? Array.Empty<ApplicationCloseReceipt>()
                : await Task.WhenAll(monitors.Select(
                        monitor => monitor.AuthorizeApplicationExitAfterPreparationAsync(
                            (RuntimeShutdownIntent)Volatile.Read(ref _watchdogExitIntent))))
                    .ConfigureAwait(true);
            if (applicationReceipts.Any(item => item?.CanExit != true))
            {
                Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                UseWaitCursor = false;
                Text = BuildWindowTitle() + " - 安全交接尚未完成，等待退出监督器";
                return;
            }
            foreach (var applicationReceipt in applicationReceipts)
                CacheApplicationCloseReceipt(applicationReceipt);

            // Keep the sidecar and its authenticated PID identity alive while
            // child windows execute their bounded safety/DAQ cleanup.  Only at
            // the final main-process boundary publish the explicit typed exit
            // intent. Operator exit uses ShutdownExpected; watchdog-owned
            // recovery/takeover exits keep the sidecar authority alive.
            RuntimeShutdownReceipt receipt = null;
            var exitIntent = (RuntimeShutdownIntent)Volatile.Read(
                ref _watchdogExitIntent);
            if (applicationReceipts.Any(item => item?.SafetyHandoffAccepted == true))
            {
                // The UI has released its hardware callbacks and the exact
                // sidecar durably accepted responsibility.  Shutting down the
                // Runtime here would publish a competing terminal and race the
                // headless worker.
            }
            else if (exitIntent == RuntimeShutdownIntent.WatchdogTakeoverExit ||
                exitIntent == RuntimeShutdownIntent.WatchdogRecoveryExit)
            {
                receipt = await GetOrCreateWatchdogShutdownTask(
                        reason,
                        exitIntent)
                    .ConfigureAwait(true);
            }
            else
            {
                receipt = await ShutdownWatchdogForApplicationExitAndReleaseUiAsync(
                    reason);
            }
            if (!HasApplicationCloseReceipt &&
                (receipt == null || !receipt.IsCloseAuthorized))
            {
                Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                UseWaitCursor = false;
                Text = BuildWindowTitle() + BuildWatchdogCloseFailureTitle(receipt);
                ProjectLogHub.Write(ProjectLogLevel.Warning,
                    "Watchdog主窗体退出保留资源未完成；窗口继续保持可见。" +
                    " Reason=" + (receipt?.TerminalReason ?? "NoReceipt") +
                    "; 再次点击将加入同一安全收口协议，不会绕过身份门禁。",
                    "独立看门狗");
                return;
            }

            var closed = new List<Task>(children.Length);
            foreach (var child in children)
            {
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                FormClosedEventHandler handler = null;
                handler = (_, __) =>
                {
                    try { child.FormClosed -= handler; } catch { }
                    completion.TrySetResult(true);
                };
                child.FormClosed += handler;
                closed.Add(completion.Task);
                try
                {
                    if (child is FrmEpbMainMonitor monitor)
                        monitor.CloseAfterMainExitAuthorized();
                    else
                        child.Close();
                }
                catch (Exception ex)
                {
                    try { child.FormClosed -= handler; } catch { }
                    completion.TrySetException(ex);
                }
            }
            var allClosed = Task.WhenAll(closed);
            var childrenClosed = await Task.WhenAny(allClosed, Task.Delay(5000));
            if (!ReferenceEquals(childrenClosed, allClosed))
            {
                Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                UseWaitCursor = false;
                Text = BuildWindowTitle() + " - 子窗口释放超时，等待退出监督器";
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    $"MainProcessExitStalled ChildWindowReleaseTimeout Reason={reason}; " +
                    $"Remaining={string.Join(",", MdiChildren.Where(child => !child.IsDisposed).Select(child => child.Name))}",
                    "独立看门狗");
                return;
            }
            try { await allClosed; }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                UseWaitCursor = false;
                Text = BuildWindowTitle() + " - 子窗口释放失败，等待退出监督器";
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "MainProcessExitStalled ChildWindowReleaseFailed: " +
                    ex.GetBaseException().Message,
                    "独立看门狗");
                return;
            }

            Interlocked.Exchange(ref _watchdogAllowClose, 1);
            WatchdogRuntime.MarkApplicationExitGraceful(
                "MainAndChildWindowsReleased");
            Close();
        }

        private static string BuildWatchdogCloseFailureTitle(
            RuntimeShutdownReceipt receipt)
        {
            if (receipt?.IsStickyBlockingFailure == true)
                return " - 退出受阻：Watchdog会话身份冲突，请导出诊断包";
            if (receipt == null)
                return " - 退出受阻：Watchdog未返回关闭回执，可重试关闭";
            return " - 退出受阻：" +
                   (string.IsNullOrWhiteSpace(receipt.TerminalReason)
                       ? "Watchdog安全终态未完成，可重试关闭"
                       : receipt.TerminalReason + "，可重试关闭");
        }

        internal bool ReleaseWatchdogUiResources(RuntimeShutdownReceipt receipt)
        {
            return _watchdogUiAdapter.ReleaseAfterTerminal(receipt);
        }

        internal bool PublishWatchdogUiResourcesForProduction(
            RuntimeTransportSessionContext context,
            WinFormsWatchdogPostTarget target,
            RuntimeSafetyTargetLease targetLease)
        {
            return _watchdogUiAdapter.PublishForProduction(context, target, targetLease);
        }

    }
}
