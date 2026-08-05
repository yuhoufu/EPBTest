using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Config;
using PowerSupply.Core;

namespace Controller
{
    public sealed class PowerSupplyFault
    {
        public DateTime TimestampUtc { get; set; }
        public int SupplyId { get; set; }
        public int ElectricalGroupId { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public int[] AffectedChannels { get; set; } = Array.Empty<int>();
        public PswSnapshot Snapshot { get; set; }
        public FaultClassification Classification { get; set; } = FaultClassification.HardwareConfirmed;
    }

    public sealed class PowerSupplyTelemetry
    {
        public DateTime TimestampUtc { get; set; }
        public long MonotonicTicks { get; set; }
        public int SupplyId { get; set; }
        public int ElectricalGroupId { get; set; }
        public PswSnapshot Snapshot { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    public sealed class PowerSupplyRuntimeState
    {
        public int ElectricalGroupId { get; set; }
        public long OperationEpoch { get; set; }
        public bool ExpectedOutputEnabled { get; set; }
        public bool PlannedTransition { get; set; }
        public bool Active { get; set; }
        public DateTime TelemetryUtc { get; set; }
        public bool TelemetryOutputEnabled { get; set; }
        public bool ProtectionTripped { get; set; }
    }

    public interface IPowerSupplyCoordinator : IDisposable
    {
        event Action<PowerSupplyTelemetry> TelemetryUpdated;
        event Action<PowerSupplyFault> FaultRaised;
        Task PrepareAndEnableAsync(IEnumerable<int> selectedChannels, CancellationToken token);
        Task RevalidateEnabledAsync(IEnumerable<int> selectedChannels, CancellationToken token);
        Task DisableGroupAsync(int electricalGroupId, string reason, CancellationToken token);
        Task DisableAllAsync(string reason, CancellationToken token);
        Task ResetFaultAsync(int electricalGroupId, CancellationToken token);
        bool HasFreshPowerFaultEvidence(int electricalGroupId);
        PswSnapshot GetLatestSnapshot(int electricalGroupId);
        PowerSupplyRuntimeState GetRuntimeState(int electricalGroupId);
        IReadOnlyList<PowerSupplyTelemetry> GetRecentTelemetry(int electricalGroupId, TimeSpan window);
        IReadOnlyCollection<int> ActiveGroups { get; }
    }

    public sealed class PowerSupplyCoordinator : IPowerSupplyCoordinator
    {
        private const int TelemetryCapacity = 10000;
        private static readonly TimeSpan TelemetryRetention = TimeSpan.FromSeconds(60);

        private readonly PowerSupplyFleetConfig _config;
        private readonly IReadOnlyList<ElectricalGroup> _groups;
        private readonly Config.IAppLogger _log;
        private readonly Func<PowerSupplyDeviceConfig, IPswClient> _clientFactory;
        private readonly ConcurrentDictionary<int, IPswClient> _clients = new ConcurrentDictionary<int, IPswClient>();
        private readonly ConcurrentDictionary<int, PswSnapshot> _latest = new ConcurrentDictionary<int, PswSnapshot>();
        private readonly ConcurrentDictionary<int, byte> _activeGroups = new ConcurrentDictionary<int, byte>();
        private readonly ConcurrentDictionary<int, byte> _faultedGroups = new ConcurrentDictionary<int, byte>();
        private readonly ConcurrentDictionary<int, int[]> _selectedByGroup = new ConcurrentDictionary<int, int[]>();
        private readonly ConcurrentQueue<PowerSupplyTelemetry> _telemetry = new ConcurrentQueue<PowerSupplyTelemetry>();
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _monitorCts =
            new ConcurrentDictionary<int, CancellationTokenSource>();
        private readonly ConcurrentDictionary<int, Task> _monitorTasks = new ConcurrentDictionary<int, Task>();
        private readonly ConcurrentDictionary<int, GroupOperationState> _operations =
            new ConcurrentDictionary<int, GroupOperationState>();
        private int _disposed;

        private sealed class GroupOperationState
        {
            internal readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            internal readonly object Sync = new object();
            internal CancellationTokenSource ActiveOperation;
            internal long Epoch;
            internal bool ExpectedOutputEnabled;
            internal int PlannedTransition;
        }

        public PowerSupplyCoordinator(
            PowerSupplyFleetConfig config,
            IEnumerable<ElectricalGroup> groups,
            Config.IAppLogger log = null,
            Func<PowerSupplyDeviceConfig, IPswClient> clientFactory = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            PowerSupplyConfigLoader.Validate(config);
            _groups = (groups ?? throw new ArgumentNullException(nameof(groups))).ToArray();
            _log = log ?? NullLogger.Instance;
            _clientFactory = clientFactory ?? CreateClient;
            ValidateGroupMapping();
        }

        public event Action<PowerSupplyTelemetry> TelemetryUpdated;
        public event Action<PowerSupplyFault> FaultRaised;

        public IReadOnlyCollection<int> ActiveGroups => _activeGroups.Keys.OrderBy(x => x).ToArray();

        public async Task PrepareAndEnableAsync(IEnumerable<int> selectedChannels, CancellationToken token)
        {
            ThrowIfDisposed();
            var channels = (selectedChannels ?? Enumerable.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
            if (channels.Length == 0) throw new ArgumentException("至少选择一个 EPB 通道。", nameof(selectedChannels));

            var requiredGroups = ResolveGroups(channels);
            foreach (var group in requiredGroups)
            {
                if (_faultedGroups.ContainsKey(group.Id))
                    throw new InvalidOperationException($"电源组 {group.Id} 故障已锁存，必须人工复位并重新预检。");
                _selectedByGroup[group.Id] = channels.Where(group.Members.Contains).ToArray();
            }

            var enabledThisAttempt = new ConcurrentBag<int>();
            try
            {
                await Task.WhenAll(requiredGroups.Select(group =>
                        PrepareGroupAsync(group, enabledThisAttempt, token)))
                    .ConfigureAwait(false);
            }
            catch
            {
                await RollbackGroupsAsync(enabledThisAttempt, "启动预检失败回滚").ConfigureAwait(false);
                throw;
            }
        }

        private async Task PrepareGroupAsync(
            ElectricalGroup group,
            ConcurrentBag<int> enabledThisAttempt,
            CancellationToken token)
        {
            await RunPlannedGroupOperationAsync(group.Id, expectedOutputAfter: true, token, async operationToken =>
            {
                // 计划 OFF 前先停监控，避免监控把本程序自己的切换误判为意外掉电。
                await StopMonitorAsync(group.Id).ConfigureAwait(false);
                var supply = RequiredSupply(group.Id);
                var client = _clients.GetOrAdd(group.Id, _ => _clientFactory(supply));
                var snapshot = client.IsConnected
                    ? await client.ReadSnapshotAsync(operationToken).ConfigureAwait(false)
                    : await client.ConnectAsync(operationToken).ConfigureAwait(false);

                ValidateIdentity(supply, snapshot);
                if (snapshot.OutputEnabled)
                {
                    _log.Warn(
                        $"{supply.DisplayName} 启动前已经 OUTP ON；将先关闭输出并回读确认，再执行安全设定。",
                        "程控电源");
                    await client.SetOutputAsync(false, operationToken).ConfigureAwait(false);
                    snapshot = await client.ReadSnapshotAsync(operationToken).ConfigureAwait(false);
                    _latest[group.Id] = snapshot;
                    AppendTelemetry(group.Id, snapshot, "PlannedOutputOff");
                    if (snapshot.OutputEnabled)
                        throw new InvalidOperationException(
                            $"{supply.DisplayName} 启动前 OUTP ON，发送 OUTP OFF 后回读仍为 ON；已阻止带载改参。");
                }
                if (snapshot.ProtectionTripped)
                    throw new InvalidOperationException($"{supply.DisplayName} 保护已触发，禁止启动。");

                await ApplyAndVerifySetpointsAsync(client, supply, operationToken).ConfigureAwait(false);
                var errors = await client.ReadErrorQueueAsync(operationToken).ConfigureAwait(false);
                if (errors.Any(x => !IsNoError(x)))
                    throw new InvalidOperationException($"{supply.DisplayName} 错误队列非空：{string.Join(" | ", errors)}");

                enabledThisAttempt.Add(group.Id);
                var outputOnStarted = Stopwatch.GetTimestamp();
                await client.SetOutputAsync(true, operationToken).ConfigureAwait(false);
                var enabled = await client.ReadSnapshotAsync(operationToken).ConfigureAwait(false);
                if (!enabled.OutputEnabled)
                    throw new InvalidOperationException($"{supply.DisplayName} OUTP ON 回读失败。");
                if (enabled.MeasuredVoltage < supply.MinimumOutputVoltageV)
                    throw new InvalidOperationException(
                        $"{supply.DisplayName} 开启后电压过低：{enabled.MeasuredVoltage:F3}V < {supply.MinimumOutputVoltageV:F3}V。");

                enabled = await WaitForStartupCurrentZeroAsync(
                        client, supply, enabled, outputOnStarted, operationToken)
                    .ConfigureAwait(false);
                _latest[group.Id] = enabled;
                _activeGroups[group.Id] = 0;
                AppendTelemetry(group.Id, enabled, null);
                await StartMonitorAsync(group.Id).ConfigureAwait(false);
                _log.Info(
                    $"{supply.DisplayName} 已接管：Group={group.Id} {supply.Host}:{supply.Port} " +
                    $"VSET={enabled.SetVoltage:F3}V ISET={enabled.SetCurrent:F3}A。",
                    "程控电源");
            }).ConfigureAwait(false);
        }

        public async Task RevalidateEnabledAsync(IEnumerable<int> selectedChannels, CancellationToken token)
        {
            ThrowIfDisposed();
            var channels = (selectedChannels ?? Enumerable.Empty<int>()).Distinct().ToArray();
            foreach (var group in ResolveGroups(channels))
            {
                await RunPlannedGroupOperationAsync(group.Id, expectedOutputAfter: true, token, async operationToken =>
                {
                    await StopMonitorAsync(group.Id).ConfigureAwait(false);
                    var supply = RequiredSupply(group.Id);
                    var client = _clients.GetOrAdd(group.Id, _ => _clientFactory(supply));
                    var snapshot = client.IsConnected
                        ? await client.ReadSnapshotAsync(operationToken).ConfigureAwait(false)
                        : await client.ConnectAsync(operationToken).ConfigureAwait(false);
                    ValidateIdentity(supply, snapshot);
                    _latest[group.Id] = snapshot;
                    AppendTelemetry(group.Id, snapshot, "RecoveryRevalidation");
                    if (snapshot.ProtectionTripped)
                        throw new InvalidOperationException($"{supply.DisplayName} 保护已触发，禁止恢复。");
                    if (!snapshot.OutputEnabled)
                        throw new InvalidOperationException($"{supply.DisplayName} 恢复复核时输出为 OFF。");
                    if (snapshot.MeasuredVoltage < supply.MinimumOutputVoltageV)
                        throw new InvalidOperationException(
                            $"{supply.DisplayName} 恢复复核电压过低：{snapshot.MeasuredVoltage:F3}V。");
                    _activeGroups[group.Id] = 0;
                    await StartMonitorAsync(group.Id).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
        }

        public async Task DisableGroupAsync(int electricalGroupId, string reason, CancellationToken token)
        {
            CancelActiveGroupOperation(electricalGroupId);
            var operation = Operation(electricalGroupId);
            await operation.Gate.WaitAsync(token).ConfigureAwait(false);
            CancellationTokenSource linked = null;
            long epoch;
            lock (operation.Sync)
            {
                epoch = ++operation.Epoch;
                operation.PlannedTransition = 1;
                operation.ExpectedOutputEnabled = false;
                linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                operation.ActiveOperation = linked;
            }
            try
            {
            // 必须等正在执行的遥测事务完全退出后才能发送 OUTP OFF。仅取消而不等待会让
            // 未完成的 StreamReader.ReadLineAsync 与关电回读并发，造成响应串线和误报。
            await StopMonitorAsync(electricalGroupId).ConfigureAwait(false);
            if (_clients.TryGetValue(electricalGroupId, out var client) && client.IsConnected)
            {
                try
                {
                    await client.SetOutputAsync(false, linked.Token).ConfigureAwait(false);
                    var snapshot = await client.ReadSnapshotAsync(linked.Token).ConfigureAwait(false);
                    _latest[electricalGroupId] = snapshot;
                    AppendTelemetry(electricalGroupId, snapshot, null);
                    if (snapshot.OutputEnabled)
                        throw new InvalidOperationException($"电源组 {electricalGroupId} OUTP OFF 回读仍为 ON。");
                }
                catch (Exception ex)
                {
                    _log.Error($"电源组 {electricalGroupId} 关闭失败：{ex.Message}", "程控电源");
                    RaiseFault(electricalGroupId, "OutputOffUnverified",
                        $"关闭输出未得到可靠确认：{ex.Message}", GetLatestSnapshot(electricalGroupId));
                    throw;
                }
            }
            _activeGroups.TryRemove(electricalGroupId, out _);
            _log.Info($"电源组 {electricalGroupId} 已关闭。Reason={reason}", "程控电源");
            }
            finally
            {
                lock (operation.Sync)
                {
                    if (operation.Epoch == epoch)
                    {
                        operation.ActiveOperation = null;
                        operation.PlannedTransition = 0;
                        operation.ExpectedOutputEnabled = false;
                    }
                }
                linked?.Dispose();
                operation.Gate.Release();
            }
        }

        public async Task DisableAllAsync(string reason, CancellationToken token)
        {
            var groups = _activeGroups.Keys.Concat(_clients.Keys).Distinct().OrderBy(x => x).ToArray();
            var errors = new ConcurrentQueue<Exception>();
            await Task.WhenAll(groups.Select(async group =>
            {
                try { await DisableGroupAsync(group, reason, token).ConfigureAwait(false); }
                catch (Exception ex) { errors.Enqueue(ex); }
            })).ConfigureAwait(false);
            if (errors.Count > 0) throw new AggregateException("一个或多个程控电源未确认关闭。", errors);
        }

        public async Task ResetFaultAsync(int electricalGroupId, CancellationToken token)
        {
            if (_activeGroups.ContainsKey(electricalGroupId))
                throw new InvalidOperationException("电源仍在运行，不能复位故障。");
            if (_clients.TryGetValue(electricalGroupId, out var client))
            {
                if (!client.IsConnected) await client.ConnectAsync(token).ConfigureAwait(false);
                var snapshot = await client.ReadSnapshotAsync(token).ConfigureAwait(false);
                if (snapshot.OutputEnabled || snapshot.ProtectionTripped)
                    throw new InvalidOperationException("输出未关闭或保护仍处于触发状态，不能复位。");
                ValidateIdentity(RequiredSupply(electricalGroupId), snapshot);
                _latest[electricalGroupId] = snapshot;
            }
            _faultedGroups.TryRemove(electricalGroupId, out _);
            _log.Info($"电源组 {electricalGroupId} 故障锁存已人工复位；下次启动仍会执行完整预检。", "程控电源");
        }

        public bool HasFreshPowerFaultEvidence(int electricalGroupId)
        {
            var snapshot = GetLatestSnapshot(electricalGroupId);
            if (snapshot == null) return false;
            if ((DateTime.UtcNow - snapshot.TimestampUtc.ToUniversalTime()).TotalMilliseconds >
                _config.TelemetryStaleMs) return false;
            var supply = RequiredSupply(electricalGroupId);
            return snapshot.ProtectionTripped ||
                   snapshot.IsConstantCurrent ||
                   snapshot.IsCurrentLimited ||
                   snapshot.IsPowerLimited ||
                   (snapshot.OutputEnabled && snapshot.MeasuredVoltage < supply.MinimumOutputVoltageV) ||
                   (supply.CurrentA.HasValue &&
                    snapshot.MeasuredCurrent >= supply.CurrentA.Value * _config.NearLimitWarnRatio);
        }

        public PswSnapshot GetLatestSnapshot(int electricalGroupId)
        {
            _latest.TryGetValue(electricalGroupId, out var snapshot);
            return snapshot;
        }

        public PowerSupplyRuntimeState GetRuntimeState(int electricalGroupId)
        {
            var operation = Operation(electricalGroupId);
            long epoch;
            bool expected;
            bool planned;
            lock (operation.Sync)
            {
                epoch = operation.Epoch;
                expected = operation.ExpectedOutputEnabled;
                planned = operation.PlannedTransition != 0;
            }
            var telemetry = GetLatestSnapshot(electricalGroupId);
            return new PowerSupplyRuntimeState
            {
                ElectricalGroupId = electricalGroupId,
                OperationEpoch = epoch,
                ExpectedOutputEnabled = expected,
                PlannedTransition = planned,
                Active = _activeGroups.ContainsKey(electricalGroupId),
                TelemetryUtc = telemetry?.TimestampUtc ?? default,
                TelemetryOutputEnabled = telemetry?.OutputEnabled ?? false,
                ProtectionTripped = telemetry?.ProtectionTripped ?? false
            };
        }

        public IReadOnlyList<PowerSupplyTelemetry> GetRecentTelemetry(int electricalGroupId, TimeSpan window)
        {
            var from = DateTime.UtcNow - window;
            return _telemetry
                .Where(x => x.ElectricalGroupId == electricalGroupId && x.TimestampUtc >= from)
                .OrderBy(x => x.TimestampUtc)
                .ThenBy(x => x.MonotonicTicks)
                .ToArray();
        }

        public static void ExportTelemetryCsv(
            string path,
            IEnumerable<PowerSupplyTelemetry> telemetry,
            int cycleNumber = 0,
            string stage = "")
        {
            var sb = new StringBuilder();
            sb.AppendLine("Utc,MonotonicTicks,SupplyId,ElectricalGroup,Cycle,Stage,Connected,Output,VSet,ISet,VOut,IOut,POut,CV,CC,VoltageLimited,CurrentLimited,PowerLimited,ProtectionTripped,OperationStatus,QuestionableStatus,Error");
            foreach (var item in telemetry ?? Enumerable.Empty<PowerSupplyTelemetry>())
            {
                var s = item.Snapshot;
                sb.Append(item.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                    .Append(item.MonotonicTicks).Append(',')
                    .Append(item.SupplyId).Append(',')
                    .Append(item.ElectricalGroupId).Append(',')
                    .Append(cycleNumber).Append(',')
                    .Append(Csv(stage)).Append(',')
                    .Append(s != null && s.IsConnected).Append(',')
                    .Append(s != null && s.OutputEnabled).Append(',')
                    .Append(Num(s?.SetVoltage)).Append(',')
                    .Append(Num(s?.SetCurrent)).Append(',')
                    .Append(Num(s?.MeasuredVoltage)).Append(',')
                    .Append(Num(s?.MeasuredCurrent)).Append(',')
                    .Append(Num(s?.MeasuredPower)).Append(',')
                    .Append(s != null && s.IsConstantVoltage).Append(',')
                    .Append(s != null && s.IsConstantCurrent).Append(',')
                    .Append(s != null && s.IsVoltageLimited).Append(',')
                    .Append(s != null && s.IsCurrentLimited).Append(',')
                    .Append(s != null && s.IsPowerLimited).Append(',')
                    .Append(s != null && s.ProtectionTripped).Append(',')
                    .Append(s?.OperationStatus ?? 0).Append(',')
                    .Append(s?.QuestionableStatus ?? 0).Append(',')
                    .Append(Csv(item.Error))
                    .AppendLine();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private async Task ApplyAndVerifySetpointsAsync(
            IPswClient client,
            PowerSupplyDeviceConfig supply,
            CancellationToken token)
        {
            await client.SetVoltageAsync(supply.VoltageV.Value, token).ConfigureAwait(false);
            await client.SetCurrentAsync(supply.CurrentA.Value, token).ConfigureAwait(false);
            await client.SetOvpAsync(supply.OvpV.Value, token).ConfigureAwait(false);
            await client.SetOcpAsync(supply.OcpA.Value, token).ConfigureAwait(false);
            var snapshot = await client.ReadSnapshotAsync(token).ConfigureAwait(false);
            VerifyNear(supply.DisplayName, "VSET", supply.VoltageV.Value, snapshot.SetVoltage, supply.SetpointTolerance);
            VerifyNear(supply.DisplayName, "ISET", supply.CurrentA.Value, snapshot.SetCurrent, supply.SetpointTolerance);
            VerifyNear(supply.DisplayName, "OVP", supply.OvpV.Value, snapshot.Ovp, supply.SetpointTolerance);
            VerifyNear(supply.DisplayName, "OCP", supply.OcpA.Value, snapshot.Ocp, supply.SetpointTolerance);
        }

        private async Task<PswSnapshot> WaitForStartupCurrentZeroAsync(
            IPswClient client,
            PowerSupplyDeviceConfig supply,
            PswSnapshot initialSnapshot,
            long outputOnStarted,
            CancellationToken token)
        {
            long zeroSince = 0;
            var snapshot = initialSnapshot;
            _log.Info(
                $"{supply.DisplayName} OUTP ON 已确认，等待空载电流回零：" +
                $"|Iout|≤{_config.StartupZeroCurrentA:F3}A，连续 {_config.StartupZeroStableMs}ms。",
                "程控电源");

            while (true)
            {
                token.ThrowIfCancellationRequested();
                var now = Stopwatch.GetTimestamp();
                var elapsedMs = ElapsedMilliseconds(outputOnStarted, now);

                if (!snapshot.OutputEnabled)
                    throw new InvalidOperationException($"{supply.DisplayName} 等待空载电流回零时 OUTP 意外关闭。");
                if (snapshot.ProtectionTripped)
                    throw new InvalidOperationException($"{supply.DisplayName} 等待空载电流回零时保护触发。");
                if (snapshot.MeasuredVoltage < supply.MinimumOutputVoltageV)
                    throw new InvalidOperationException(
                        $"{supply.DisplayName} 等待空载电流回零时电压过低：" +
                        $"{snapshot.MeasuredVoltage:F3}V < {supply.MinimumOutputVoltageV:F3}V。");

                if (Math.Abs(snapshot.MeasuredCurrent) <= _config.StartupZeroCurrentA)
                {
                    if (zeroSince == 0) zeroSince = now;
                    if (ElapsedMilliseconds(zeroSince, now) >= _config.StartupZeroStableMs)
                    {
                        _log.Info(
                            $"{supply.DisplayName} 空载电流已稳定回零：" +
                            $"Iout={snapshot.MeasuredCurrent:F3}A，允许启动所属卡钳继电器。",
                            "程控电源");
                        return snapshot;
                    }
                }
                else
                {
                    zeroSince = 0;
                }

                if (elapsedMs >= _config.StartupZeroTimeoutMs)
                    throw new InvalidOperationException(
                        $"{supply.DisplayName} OUTP ON 后空载电流未在 {_config.StartupZeroTimeoutMs}ms 内稳定回零：" +
                        $"Iout={snapshot.MeasuredCurrent:F3}A，要求 |Iout|≤{_config.StartupZeroCurrentA:F3}A " +
                        $"连续 {_config.StartupZeroStableMs}ms；已禁止启动卡钳。");

                await Task.Delay(_config.PollIntervalMs, token).ConfigureAwait(false);
                snapshot = await client.ReadSnapshotAsync(token).ConfigureAwait(false);
                _latest[supply.ElectricalGroupId] = snapshot;
                AppendTelemetry(supply.ElectricalGroupId, snapshot, null);
            }
        }

        private static double ElapsedMilliseconds(long start, long end) =>
            (end - start) * 1000.0 / Stopwatch.Frequency;

        private async Task StartMonitorAsync(int groupId)
        {
            await StopMonitorAsync(groupId).ConfigureAwait(false);
            var cts = new CancellationTokenSource();
            _monitorCts[groupId] = cts;
            _monitorTasks[groupId] = Task.Run(() => MonitorLoopAsync(groupId, cts.Token));
        }

        private async Task MonitorLoopAsync(int groupId, CancellationToken token)
        {
            DateTime? ccSince = null;
            DateTime? lowVoltageSince = null;
            DateTime lastSuccess = DateTime.UtcNow;
            while (!token.IsCancellationRequested && _activeGroups.ContainsKey(groupId))
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var snapshot = await _clients[groupId].ReadSnapshotAsync(token).ConfigureAwait(false);
                    var telemetryFresh = snapshot != null &&
                                         (DateTime.UtcNow - snapshot.TimestampUtc.ToUniversalTime())
                                         .TotalMilliseconds <= _config.TelemetryStaleMs;
                    if (telemetryFresh) lastSuccess = DateTime.UtcNow;
                    _latest[groupId] = snapshot;
                    var supply = RequiredSupply(groupId);
                    var nearLimit = supply.CurrentA.HasValue &&
                                    snapshot.MeasuredCurrent >=
                                    supply.CurrentA.Value * _config.NearLimitWarnRatio;
                    AppendTelemetry(
                        groupId,
                        snapshot,
                        nearLimit
                            ? $"NearCurrentLimit I={snapshot.MeasuredCurrent:F3}A " +
                              $"Warn={supply.CurrentA.Value * _config.NearLimitWarnRatio:F3}A"
                            : null);

                    var operation = Operation(groupId);
                    bool expectedOn;
                    bool planned;
                    lock (operation.Sync)
                    {
                        expectedOn = operation.ExpectedOutputEnabled;
                        planned = operation.PlannedTransition != 0;
                    }
                    if (!telemetryFresh)
                    {
                        RaiseFault(groupId, "TelemetryStale",
                            $"程控电源遥测时间戳超过 {_config.TelemetryStaleMs}ms。", snapshot);
                        break;
                    }
                    if (!snapshot.OutputEnabled && expectedOn && !planned)
                    {
                        RaiseFault(groupId, "UnexpectedOutputOff", "试验运行中电源输出意外关闭。", snapshot);
                        break;
                    }
                    if (snapshot.ProtectionTripped)
                    {
                        RaiseFault(groupId, "ProtectionTrip", "程控电源保护跳闸。", snapshot);
                        break;
                    }

                    var limited = snapshot.IsConstantCurrent || snapshot.IsCurrentLimited || snapshot.IsPowerLimited;
                    ccSince = limited ? ccSince ?? DateTime.UtcNow : null;
                    if (ccSince.HasValue && (DateTime.UtcNow - ccSince.Value).TotalMilliseconds >= _config.CcTripMs)
                    {
                        RaiseFault(groupId, "SustainedCurrentLimit",
                            $"程控电源持续限流超过 {_config.CcTripMs}ms。", snapshot);
                        break;
                    }

                    var low = snapshot.MeasuredVoltage < supply.MinimumOutputVoltageV;
                    lowVoltageSince = low ? lowVoltageSince ?? DateTime.UtcNow : null;
                    if (lowVoltageSince.HasValue &&
                        (DateTime.UtcNow - lowVoltageSince.Value).TotalMilliseconds >= _config.LowVoltageTripMs)
                    {
                        RaiseFault(groupId, "SustainedLowVoltage",
                            $"输出低压持续超过 {_config.LowVoltageTripMs}ms：" +
                            $"{snapshot.MeasuredVoltage:F3}V < {supply.MinimumOutputVoltageV:F3}V。", snapshot);
                        break;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppendTelemetry(groupId, null, ex.Message);
                    if ((DateTime.UtcNow - lastSuccess).TotalMilliseconds >= _config.TelemetryStaleMs)
                    {
                        RaiseFault(groupId, "TelemetryStale",
                            $"程控电源通信/遥测中断超过 {_config.TelemetryStaleMs}ms：{ex.Message}",
                            GetLatestSnapshot(groupId));
                        break;
                    }
                }

                var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                var delay = Math.Max(1, _config.PollIntervalMs - (int)elapsedMs);
                try { await Task.Delay(delay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void RaiseFault(int groupId, string code, string reason, PswSnapshot snapshot)
        {
            if (!_faultedGroups.TryAdd(groupId, 0)) return;
            var supply = RequiredSupply(groupId);
            var fault = new PowerSupplyFault
            {
                TimestampUtc = DateTime.UtcNow,
                SupplyId = supply.Id,
                ElectricalGroupId = groupId,
                Code = code,
                Reason = reason,
                AffectedChannels = _selectedByGroup.TryGetValue(groupId, out var channels)
                    ? channels
                    : Group(groupId).Members.ToArray(),
                Snapshot = snapshot,
                Classification = IsConfirmedPowerHardwareFault(code, snapshot)
                    ? FaultClassification.HardwareConfirmed
                    : FaultClassification.SystemFault
            };
            _log.Error(
                $"电源组 {groupId} {(fault.Classification == FaultClassification.HardwareConfirmed ? "硬件已确认" : "系统故障")} " +
                $"[{code}]：{reason}",
                "程控电源");
            try { FaultRaised?.Invoke(fault); } catch { }
        }

        private static bool IsConfirmedPowerHardwareFault(string code, PswSnapshot snapshot)
        {
            // 意外 OFF、通信陈旧和低压可能由本进程调度/通信引起，不能直接归为硬件报警。
            return string.Equals(code, "ProtectionTrip", StringComparison.OrdinalIgnoreCase) &&
                   snapshot != null && snapshot.ProtectionTripped;
        }

        private GroupOperationState Operation(int groupId) =>
            _operations.GetOrAdd(groupId, _ => new GroupOperationState());

        private void CancelActiveGroupOperation(int groupId)
        {
            var operation = Operation(groupId);
            lock (operation.Sync)
            {
                try { operation.ActiveOperation?.Cancel(); } catch { }
                operation.Epoch++;
                operation.ExpectedOutputEnabled = false;
            }
        }

        private async Task RunPlannedGroupOperationAsync(
            int groupId,
            bool expectedOutputAfter,
            CancellationToken token,
            Func<CancellationToken, Task> action)
        {
            var operation = Operation(groupId);
            await operation.Gate.WaitAsync(token).ConfigureAwait(false);
            CancellationTokenSource linked = null;
            long epoch;
            lock (operation.Sync)
            {
                epoch = ++operation.Epoch;
                operation.PlannedTransition = 1;
                linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                operation.ActiveOperation = linked;
            }
            try
            {
                await action(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                lock (operation.Sync)
                {
                    if (operation.Epoch != epoch)
                        throw new OperationCanceledException("电源操作已被更高优先级安全动作撤销。", linked.Token);
                    operation.ExpectedOutputEnabled = expectedOutputAfter;
                }
            }
            finally
            {
                lock (operation.Sync)
                {
                    if (operation.Epoch == epoch)
                    {
                        operation.ActiveOperation = null;
                        operation.PlannedTransition = 0;
                    }
                }
                linked?.Dispose();
                operation.Gate.Release();
            }
        }

        private void AppendTelemetry(int groupId, PswSnapshot snapshot, string error)
        {
            var item = new PowerSupplyTelemetry
            {
                TimestampUtc = DateTime.UtcNow,
                MonotonicTicks = Stopwatch.GetTimestamp(),
                SupplyId = RequiredSupply(groupId).Id,
                ElectricalGroupId = groupId,
                Snapshot = snapshot,
                Error = error ?? string.Empty
            };
            _telemetry.Enqueue(item);
            TrimTelemetry();
            try { TelemetryUpdated?.Invoke(item); } catch { }
        }

        private void TrimTelemetry()
        {
            var cutoff = DateTime.UtcNow - TelemetryRetention;
            while (_telemetry.Count > TelemetryCapacity && _telemetry.TryDequeue(out _)) { }
            while (_telemetry.TryPeek(out var first) && first.TimestampUtc < cutoff &&
                   _telemetry.TryDequeue(out _)) { }
        }

        private async Task RollbackGroupsAsync(IEnumerable<int> groups, string reason)
        {
            foreach (var group in groups.Distinct())
            {
                try { await DisableGroupAsync(group, reason, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
        }

        private async Task StopMonitorAsync(int groupId)
        {
            _monitorCts.TryRemove(groupId, out var cts);
            _monitorTasks.TryRemove(groupId, out var task);
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }

            if (task != null)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.Warn($"电源组 {groupId} 监控任务停止时异常：{ex.Message}", "程控电源");
                }
            }

            cts?.Dispose();
        }

        private IReadOnlyList<ElectricalGroup> ResolveGroups(IEnumerable<int> channels)
        {
            var result = new List<ElectricalGroup>();
            foreach (var channel in channels)
            {
                var matches = _groups.Where(x => x.Members.Contains(channel)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException($"EPB{channel} 必须且只能属于一个电气组。");
                if (!result.Any(x => x.Id == matches[0].Id)) result.Add(matches[0]);
            }
            return result;
        }

        private void ValidateGroupMapping()
        {
            var errors = new List<string>();
            for (var id = 1; id <= 4; id++)
            {
                var groupCount = _groups.Count(x => x.Id == id);
                var supplyCount = _config.Supplies.Count(x => x.ElectricalGroupId == id);
                if (groupCount != 1) errors.Add($"电气组 {id} 配置数量为 {groupCount}。");
                if (supplyCount != 1) errors.Add($"电气组 {id} 对应电源数量为 {supplyCount}。");
            }
            if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        private void ValidateIdentity(PowerSupplyDeviceConfig config, PswSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsVerifiedPsw)
                throw new InvalidOperationException($"{config.DisplayName} 未识别为可信 GW Instek PSW。");
            if (!string.IsNullOrWhiteSpace(config.ExpectedModel) &&
                snapshot.Identity.IndexOf(config.ExpectedModel, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException($"{config.DisplayName} 型号与配置不符：{snapshot.Identity}");
            if (!string.IsNullOrWhiteSpace(config.ExpectedSerial) &&
                snapshot.Identity.IndexOf(config.ExpectedSerial, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException($"{config.DisplayName} 序列号与配置不符：{snapshot.Identity}");
        }

        private static void VerifyNear(string supply, string name, double expected, double? actual, double tolerance)
        {
            if (!actual.HasValue || Math.Abs(expected - actual.Value) > tolerance)
                throw new InvalidOperationException(
                    $"{supply} {name} 回读不一致：期望 {expected:F3}，实际 " +
                    (actual.HasValue ? actual.Value.ToString("F3", CultureInfo.InvariantCulture) : "N/A"));
        }

        private static bool IsNoError(string value)
        {
            var normalized = (value ?? string.Empty).TrimStart();
            return normalized == "0" || normalized.StartsWith("0,") || normalized.StartsWith("+0,");
        }

        private IPswClient CreateClient(PowerSupplyDeviceConfig supply)
        {
            return new PswTcpClient(
                new PswEndpoint
                {
                    Id = supply.Id,
                    DisplayName = supply.DisplayName,
                    Host = supply.Host,
                    Port = supply.Port,
                    Terminator = supply.Terminator
                },
                new AppPswLog(_log));
        }

        private PowerSupplyDeviceConfig RequiredSupply(int groupId) =>
            _config.GetByGroup(groupId) ??
            throw new InvalidOperationException($"未配置电气组 {groupId} 对应的程控电源。");

        private ElectricalGroup Group(int groupId) =>
            _groups.Single(x => x.Id == groupId);

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PowerSupplyCoordinator));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var stopTasks = _monitorCts.Keys.ToArray()
                .Select(StopMonitorAsync)
                .ToArray();
            try { Task.WaitAll(stopTasks, TimeSpan.FromSeconds(3)); } catch { }
            foreach (var client in _clients.Values)
            {
                try { client.Dispose(); } catch { }
            }
            _clients.Clear();
            foreach (var operation in _operations.Values)
            {
                try { operation.ActiveOperation?.Cancel(); } catch { }
                try { operation.ActiveOperation?.Dispose(); } catch { }
                operation.Gate.Dispose();
            }
            _operations.Clear();
        }

        private sealed class AppPswLog : IPswLog
        {
            private readonly Config.IAppLogger _log;
            public AppPswLog(Config.IAppLogger log) { _log = log; }
            public void Write(PswLogEntry entry)
            {
                var message = $"PSU{entry.SupplyId} {entry.Direction}: {entry.Message}";
                if (entry.Direction == PswLogDirection.Error) _log.Error(message, "程控电源");
                else if (entry.Direction == PswLogDirection.Information) _log.Info(message, "程控电源");
            }
        }

        private static string Num(double? value) =>
            value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }
    }
}
