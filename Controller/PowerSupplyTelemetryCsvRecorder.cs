using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// 将程控电源遥测从 100ms 监控线程解耦后持续写盘。
    /// 监控线程只做无阻塞入队；磁盘变慢不会拖慢保护判定。
    /// </summary>
    internal sealed class PowerSupplyTelemetryCsvRecorder : IDisposable
    {
        private readonly BlockingCollection<Row> _queue =
            new BlockingCollection<Row>(new ConcurrentQueue<Row>(), 50000);
        private readonly Task _writerTask;
        private readonly Config.IAppLogger _log;
        private int _dropReported;
        private int _disposed;

        public PowerSupplyTelemetryCsvRecorder(string path, Config.IAppLogger log)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("遥测文件路径不能为空。", nameof(path));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ??
                                      throw new InvalidOperationException("遥测目录无效。"));
            FilePath = path;
            _log = log ?? Config.NullLogger.Instance;
            _writerTask = Task.Run(WriteLoop);
        }

        public string FilePath { get; }

        public void Enqueue(PowerSupplyTelemetry telemetry, int cycleNumber, string stage)
        {
            if (telemetry == null || Volatile.Read(ref _disposed) != 0) return;
            if (_queue.TryAdd(new Row(telemetry, cycleNumber, stage ?? string.Empty))) return;
            if (Interlocked.Exchange(ref _dropReported, 1) == 0)
                _log.Error("程控电源遥测写盘队列已满；控制与保护继续运行，但部分遥测未落盘。", "程控电源");
        }

        private void WriteLoop()
        {
            try
            {
                using (var writer = new StreamWriter(FilePath, false, new UTF8Encoding(true), 64 * 1024))
                {
                    writer.WriteLine(
                        "Utc,MonotonicTicks,SupplyId,ElectricalGroup,Cycle,Stage,Connected,Output," +
                        "VSet,ISet,OVP,OCP,VOut,IOut,POut,CV,CC,VoltageLimited,CurrentLimited," +
                        "PowerLimited,ProtectionTripped,OperationStatus,QuestionableStatus,Error");
                    var pending = 0;
                    var lastFlushUtc = DateTime.UtcNow;
                    foreach (var row in _queue.GetConsumingEnumerable())
                    {
                        WriteRow(writer, row);
                        pending++;
                        if (pending >= 20 || (DateTime.UtcNow - lastFlushUtc).TotalSeconds >= 1)
                        {
                            writer.Flush();
                            pending = 0;
                            lastFlushUtc = DateTime.UtcNow;
                        }
                    }
                    writer.Flush();
                }
            }
            catch (Exception ex)
            {
                _log.Error($"程控电源遥测写盘失败：{ex.Message}", "程控电源", ex);
            }
        }

        private static void WriteRow(TextWriter writer, Row row)
        {
            var item = row.Telemetry;
            var s = item.Snapshot;
            writer.Write(item.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(item.MonotonicTicks);
            writer.Write(',');
            writer.Write(item.SupplyId);
            writer.Write(',');
            writer.Write(item.ElectricalGroupId);
            writer.Write(',');
            writer.Write(row.CycleNumber);
            writer.Write(',');
            writer.Write(Csv(row.Stage));
            writer.Write(',');
            writer.Write(s != null && s.IsConnected);
            writer.Write(',');
            writer.Write(s != null && s.OutputEnabled);
            writer.Write(',');
            writer.Write(Num(s?.SetVoltage));
            writer.Write(',');
            writer.Write(Num(s?.SetCurrent));
            writer.Write(',');
            writer.Write(Num(s?.Ovp));
            writer.Write(',');
            writer.Write(Num(s?.Ocp));
            writer.Write(',');
            writer.Write(Num(s?.MeasuredVoltage));
            writer.Write(',');
            writer.Write(Num(s?.MeasuredCurrent));
            writer.Write(',');
            writer.Write(Num(s?.MeasuredPower));
            writer.Write(',');
            writer.Write(s != null && s.IsConstantVoltage);
            writer.Write(',');
            writer.Write(s != null && s.IsConstantCurrent);
            writer.Write(',');
            writer.Write(s != null && s.IsVoltageLimited);
            writer.Write(',');
            writer.Write(s != null && s.IsCurrentLimited);
            writer.Write(',');
            writer.Write(s != null && s.IsPowerLimited);
            writer.Write(',');
            writer.Write(s != null && s.ProtectionTripped);
            writer.Write(',');
            writer.Write(s?.OperationStatus ?? 0);
            writer.Write(',');
            writer.Write(s?.QuestionableStatus ?? 0);
            writer.Write(',');
            writer.WriteLine(Csv(item.Error));
        }

        private static string Num(double? value) =>
            value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _queue.CompleteAdding();
            if (!_writerTask.Wait(TimeSpan.FromSeconds(5)))
                _log.Warn($"程控电源遥测文件未能在退出前完成刷新：{FilePath}", "程控电源");
            _queue.Dispose();
        }

        private sealed class Row
        {
            public Row(PowerSupplyTelemetry telemetry, int cycleNumber, string stage)
            {
                Telemetry = telemetry;
                CycleNumber = cycleNumber;
                Stage = stage;
            }

            public PowerSupplyTelemetry Telemetry { get; }
            public int CycleNumber { get; }
            public string Stage { get; }
        }
    }
}
