using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;

namespace Controller.Alarm
{
    public sealed class AlarmManager : IDisposable
    {
        private readonly AlarmConfig _cfg;
        private readonly IAppLogger _log;
        private readonly TaskSupervisor _tasks;

        private readonly SemaphoreSlim _ioGate = new(1, 1);
        private readonly Dictionary<(int deviceId, int line), AlarmSingleCoilCommand> _singleCoil;
        private readonly Dictionary<int, AlarmEpbMapping> _epbMap;

        private readonly M7055dSerialClient _client;

        private readonly HashSet<int> _active = new();
        private volatile bool _buzzerEnabled;
        private DateTime _cooldownUntilUtc;
        private CancellationTokenSource _buzzerDebounceCts;

        public AlarmManager(AlarmConfig cfg, IAppLogger log = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _log = log ?? NullLogger.Instance;
            _tasks = new TaskSupervisor(_log);

            _buzzerEnabled = _cfg.Behavior.BuzzerEnabled;

            _singleCoil = _cfg.Commands.SingleCoil
                .Where(x => x != null)
                .GroupBy(x => (x.DeviceId, x.Line))
                .ToDictionary(g => g.Key, g => g.First());

            _epbMap = _cfg.Mappings.Epb
                .Where(x => x != null)
                .GroupBy(x => x.Channel)
                .ToDictionary(g => g.Key, g => g.First());

            _client = new M7055dSerialClient(
                _cfg.Serial.Port,
                _cfg.Serial.Baud,
                _cfg.Serial.DataBits,
                _cfg.Serial.Parity,
                _cfg.Serial.StopBits,
                _cfg.Behavior.TimeoutMs);

            foreach (var mapping in _epbMap.OrderBy(pair => pair.Key))
                _log.Info(
                    $"AlarmMappingLoaded Channel={mapping.Key} " +
                    $"Device={mapping.Value.DeviceId} Line={mapping.Value.Line}",
                    "报警");
            if (_cfg.Mappings.Buzzer != null)
                _log.Info(
                    $"AlarmMappingLoaded Output=Buzzer " +
                    $"Device={_cfg.Mappings.Buzzer.DeviceId} Line={_cfg.Mappings.Buzzer.Line}",
                    "报警");

            TryOpen();
        }

        public event Action<int, bool, string> AlarmStateChanged;

        public bool BuzzerEnabled => _buzzerEnabled;

        public void SetBuzzerEnabled(bool enabled)
        {
            _buzzerEnabled = enabled;
            _tasks.Observe(RefreshBuzzerAsync(), "AlarmRefreshBuzzer", Guid.Empty);
        }

        public bool IsAnyAlarmActive()
        {
            lock (_active) return _active.Count > 0;
        }

        /// <summary>
        /// 直接控制指定 EPB 通道对应的“面板指示灯”输出（用于联调/测试界面）。
        /// <para>
        /// 注意：该方法<strong>不会</strong>修改内部报警集合 <see cref="_active"/>，也不会触发 <see cref="AlarmStateChanged"/>。
        /// </para>
        /// </summary>
        public Task SetIndicatorOutputAsync(int epbId, bool on, CancellationToken token = default)
        {
            if (!_epbMap.TryGetValue(epbId, out var map))
                return Task.CompletedTask;

            return SetSingleCoilAsync(
                map.DeviceId,
                map.Line,
                on,
                token,
                $"Channel={epbId}");
        }

        /// <summary>
        /// 直接控制蜂鸣器输出（用于联调/测试界面）。
        /// <para>
        /// 注意：该方法不依赖是否有报警激活，且不会改变 <see cref="BuzzerEnabled"/> 或内部报警集合。
        /// </para>
        /// </summary>
        public Task SetBuzzerOutputAsync(bool on, CancellationToken token = default)
        {
            var buz = _cfg.Mappings.Buzzer;
            if (buz == null)
                return Task.CompletedTask;

            return SetSingleCoilAsync(
                buz.DeviceId,
                buz.Line,
                on,
                token,
                "Output=Buzzer");
        }

        public async Task SetAlarmAsync(int epbId, bool active, string reason = null, CancellationToken token = default)
        {
            reason ??= string.Empty;

            bool changed;
            lock (_active)
            {
                if (active)
                    changed = _active.Add(epbId);
                else
                    changed = _active.Remove(epbId);
            }

            NonCriticalObserver.Invoke(
                AlarmStateChanged,
                epbId,
                active,
                reason,
                ex => _log?.Warn(
                    $"EPB[{epbId}] 报警状态观察者异常已隔离：{ex.Message}",
                    "报警"));

            // 指示灯：尽量实时
            if (changed)
            {
                if (DateTime.UtcNow >= _cooldownUntilUtc)
                    await SetEpbIndicatorAsync(epbId, active, token).ConfigureAwait(false);
            }

            // 蜂鸣器：去抖
            _tasks.Observe(RefreshBuzzerAsync(), "AlarmRefreshBuzzer", Guid.Empty, epbId);
        }

        public async Task ClearAllAsync(CancellationToken token = default)
        {
            await _ioGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _cooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, _cfg.Behavior.RearmDelayMs));

                // 发送 AllOff
                foreach (var cmd in _cfg.Commands.AllOff.OrderBy(x => x.DeviceId))
                {
                    if (cmd == null) continue;
                    await SendHexNoGateAsync(cmd.Hex, cmd.ExpectResponse, token).ConfigureAwait(false);
                }

                lock (_active) _active.Clear();

                // 取消蜂鸣器去抖任务
                try
                {
                    _buzzerDebounceCts?.Cancel();
                }
                catch
                {
                    // ignore
                }

                // 冷却结束后，如果期间又产生报警，则刷新一次输出
                _tasks.Observe(Task.Run(async () =>
                {
                    try
                    {
                        var delay = (int)Math.Max(0, (_cooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds);
                        if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
                        if (IsAnyAlarmActive())
                        {
                            await RefreshAllIndicatorsAsync().ConfigureAwait(false);
                            await RefreshBuzzerAsync().ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }), "AlarmCooldownRefresh", Guid.Empty);
            }
            finally
            {
                _ioGate.Release();
            }
        }

        /// <summary>
        /// 在 <see cref="_ioGate"/> 已经被调用方持有的前提下发送串口帧。
        /// <para>
        /// 用于避免像 <see cref="ClearAllAsync"/> 这类“批量发送”场景下，外层持锁又进入 <see cref="SendHexAsync"/>
        /// 造成二次 WaitAsync 的死锁。
        /// </para>
        /// </summary>
        private async Task SendHexNoGateAsync(string hex, bool expectResponse, CancellationToken token)
        {
            var frame = HexToBytes(hex);

            var retry = Math.Max(0, _cfg.Behavior.Retry);
            for (var attempt = 0; attempt <= retry; attempt++)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    _client.Send(frame, expectResponse);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt >= retry)
                        _log.Warn($"报警串口发送失败：{ex.Message}（Hex={hex}）", "报警");

                    await Task.Delay(30, token).ConfigureAwait(false);
                }
            }
        }

        private void TryOpen()
        {
            try
            {
                _client.Open();
                _log.Info($"报警串口已打开：{_cfg.Serial.Port} {_cfg.Serial.Baud}bps", "报警");
            }
            catch (Exception ex)
            {
                _log.Warn($"报警串口打开失败：{ex.Message}（Port={_cfg.Serial.Port}）", "报警");
            }
        }

        private async Task RefreshAllIndicatorsAsync()
        {
            var now = DateTime.UtcNow;
            if (now < _cooldownUntilUtc) return;

            List<int> active;
            lock (_active) active = _active.ToList();

            foreach (var kv in _epbMap)
            {
                var epbId = kv.Key;
                var isOn = active.Contains(epbId);
                await SetEpbIndicatorAsync(epbId, isOn, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task SetEpbIndicatorAsync(int epbId, bool on, CancellationToken token)
        {
            if (!_epbMap.TryGetValue(epbId, out var map)) return;
            await SetSingleCoilAsync(
                    map.DeviceId,
                    map.Line,
                    on,
                    token,
                    $"Channel={epbId}")
                .ConfigureAwait(false);
        }

        private async Task RefreshBuzzerAsync()
        {
            if (!_cfg.Behavior.BuzzerOnAnyAlarm) return;

            var buz = _cfg.Mappings.Buzzer;
            if (buz == null) return;

            var now = DateTime.UtcNow;
            if (now < _cooldownUntilUtc)
            {
                await SetSingleCoilAsync(buz.DeviceId, buz.Line, false, CancellationToken.None, "Output=Buzzer").ConfigureAwait(false);
                return;
            }

            var any = IsAnyAlarmActive();
            if (!_buzzerEnabled || !any)
            {
                await SetSingleCoilAsync(buz.DeviceId, buz.Line, false, CancellationToken.None, "Output=Buzzer").ConfigureAwait(false);
                return;
            }

            // 去抖：延迟 BuzzerDebounceMs 后仍有报警才拉响
            var debounce = Math.Max(0, _cfg.Behavior.BuzzerDebounceMs);
            if (debounce <= 0)
            {
                await SetSingleCoilAsync(buz.DeviceId, buz.Line, true, CancellationToken.None, "Output=Buzzer").ConfigureAwait(false);
                return;
            }

            try
            {
                _buzzerDebounceCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            var cts = new CancellationTokenSource();
            _buzzerDebounceCts = cts;

            _tasks.Observe(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(debounce, cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested) return;
                    if (!_buzzerEnabled) return;
                    if (!IsAnyAlarmActive()) return;
                    if (DateTime.UtcNow < _cooldownUntilUtc) return;

                    await SetSingleCoilAsync(buz.DeviceId, buz.Line, true, CancellationToken.None, "Output=Buzzer").ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }), "AlarmBuzzerDebounce", Guid.Empty);
        }

        private async Task SetSingleCoilAsync(
            int deviceId,
            int line,
            bool on,
            CancellationToken token,
            string identity = null)
        {
            if (!_singleCoil.TryGetValue((deviceId, line), out var cmd))
            {
                _log.Warn(
                    $"AlarmOutputMappingMissing {identity ?? "Output=Unknown"} " +
                    $"Device={deviceId} Line={line} State={(on ? "On" : "Off")}",
                    "报警");
                return;
            }

            var hex = on ? cmd.OnHex : cmd.OffHex;
            if (string.IsNullOrWhiteSpace(hex))
            {
                _log.Warn(
                    $"AlarmOutputCommandMissing {identity ?? "Output=Unknown"} " +
                    $"Device={deviceId} Line={line} State={(on ? "On" : "Off")}",
                    "报警");
                return;
            }

            _log.Info(
                $"AlarmOutputRequested {identity ?? "Output=Unknown"} " +
                $"Device={deviceId} Line={line} State={(on ? "On" : "Off")}",
                "报警");
            var result = await SendHexAsync(hex, expectResponse: true, token).ConfigureAwait(false);
            if (result.Succeeded)
                _log.Info(
                    $"AlarmOutputAcked {identity ?? "Output=Unknown"} " +
                    $"Device={deviceId} Line={line} State={(on ? "On" : "Off")} " +
                    $"Attempt={result.Attempts} LatencyMs={result.ElapsedMilliseconds:F1} " +
                    "Confirmation=SerialResponse",
                    "报警");
            else
                _log.Warn(
                    $"AlarmOutputFailed {identity ?? "Output=Unknown"} " +
                    $"Device={deviceId} Line={line} State={(on ? "On" : "Off")} " +
                    $"Attempts={result.Attempts} LatencyMs={result.ElapsedMilliseconds:F1} " +
                    $"Error={result.Error}",
                    "报警");
        }

        private sealed class AlarmSendResult
        {
            internal bool Succeeded;
            internal int Attempts;
            internal double ElapsedMilliseconds;
            internal string Error = string.Empty;
        }

        private async Task<AlarmSendResult> SendHexAsync(
            string hex,
            bool expectResponse,
            CancellationToken token)
        {
            var frame = HexToBytes(hex);
            var clock = Stopwatch.StartNew();
            var retry = Math.Max(0, _cfg.Behavior.Retry);
            var lastError = string.Empty;
            for (var attempt = 0; attempt <= retry; attempt++)
            {
                token.ThrowIfCancellationRequested();

                await _ioGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    _client.Send(frame, expectResponse);
                    return new AlarmSendResult
                    {
                        Succeeded = true,
                        Attempts = attempt + 1,
                        ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds
                    };
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    await Task.Delay(30, token).ConfigureAwait(false);
                }
                finally
                {
                    _ioGate.Release();
                }
            }
            return new AlarmSendResult
            {
                Succeeded = false,
                Attempts = retry + 1,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                Error = lastError
            };
        }

        public static byte[] HexToBytes(string hex)
        {
            if (hex == null) return Array.Empty<byte>();
            var parts = hex.Split(new[] { ' ', '\t', '\r', '\n', '-' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                bytes[i] = byte.Parse(parts[i], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            return bytes;
        }

        public void Dispose()
        {
            try
            {
                _buzzerDebounceCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            try { _tasks.DrainAsync(1000).GetAwaiter().GetResult(); } catch { }
            _client.Dispose();
            _ioGate.Dispose();
        }
    }
}
