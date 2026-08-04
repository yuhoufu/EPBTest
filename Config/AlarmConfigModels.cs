using System;
using System.Collections.Generic;
using System.IO.Ports;

namespace Config
{
    public sealed class AlarmConfig
    {
        public AlarmSerialConfig Serial { get; set; } = new();
        public AlarmMappings Mappings { get; set; } = new();
        public AlarmCommands Commands { get; set; } = new();
        public AlarmBehavior Behavior { get; set; } = new();
        public WarningSnapshotConfig WarningSnapshots { get; set; } = new();
    }

    public sealed class WarningSnapshotConfig
    {
        public bool Enabled { get; set; } = true;
        public string RootDirectory { get; set; } = "WarningSnapshots";
        public bool SaveCsv { get; set; } = true;
        public bool SaveBin { get; set; } = true;
        public int HardAlarmLastNCycles { get; set; } = 10;
        public long SoftWarningQuotaMb { get; set; }
        public long DiskFreeWarningMb { get; set; } = 10240;
    }

    public sealed class AlarmSerialConfig
    {
        public string Port { get; set; } = "COM1";
        public int Baud { get; set; } = 115200;
        public int DataBits { get; set; } = 8;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One;
    }

    public sealed class AlarmMappings
    {
        public List<AlarmEpbMapping> Epb { get; } = new();
        public AlarmBuzzerMapping Buzzer { get; set; }
    }

    public sealed class AlarmEpbMapping
    {
        public int Channel { get; set; }
        public int DeviceId { get; set; }
        public int Line { get; set; }

        /// <summary>
        /// 正向峰值超过 (阈值 + OvershootAlarmDeltaA) 时触发报警。
        /// 若为 null，则使用 AlarmBehavior.OvershootAlarmDeltaA。
        /// </summary>
        public double? OvershootAlarmDeltaA { get; set; }
    }

    public sealed class AlarmBuzzerMapping
    {
        public int DeviceId { get; set; }
        public int Line { get; set; }
    }

    public sealed class AlarmCommands
    {
        public List<AlarmAllOffCommand> AllOff { get; } = new();
        public List<AlarmSingleCoilCommand> SingleCoil { get; } = new();
    }

    public sealed class AlarmAllOffCommand
    {
        public int DeviceId { get; set; }
        public string Hex { get; set; }
        public bool ExpectResponse { get; set; }
    }

    public sealed class AlarmSingleCoilCommand
    {
        public int DeviceId { get; set; }
        public int Line { get; set; }
        public string OnHex { get; set; }
        public string OffHex { get; set; }
    }

    public sealed class AlarmBehavior
    {
        public bool BuzzerEnabled { get; set; } = true;
        public bool BuzzerOnAnyAlarm { get; set; } = true;
        public int BuzzerDebounceMs { get; set; } = 200;
        public int RearmDelayMs { get; set; } = 1000;
        public int TimeoutMs { get; set; } = 200;
        public int Retry { get; set; } = 2;

        /// <summary>
        /// 正向峰值超过阈值的报警增量（A）。
        /// 例如 3.0 表示 Imax >= Thr + 3.0A 触发报警。
        /// </summary>
        public double OvershootAlarmDeltaA { get; set; } = 3.0;

        /// <summary>
        /// 自适应控制的峰值平衡带（A）。超出后先软预警并继续释放，
        /// 达到连续确认圈数后才升级为硬故障。
        /// </summary>
        public double AdaptiveOvershootWarningDeltaA { get; set; } = 0.8;

        /// <summary>自适应峰值连续超出平衡带多少圈后升级为硬故障。</summary>
        public int AdaptiveOvershootConfirmCycles { get; set; } = 5;

        /// <summary>
        /// 快速峰值与完整峰值在证据有效且时效合格时，连续偏差多少圈后升级为硬故障。
        /// 回到容差内立即清零；捕获无效或证据滞后不计入该次数。
        /// </summary>
        public int PeakEvidenceMismatchConfirmCycles { get; set; } = 3;

        /// <summary>
        /// 自适应正向峰值低于合格下限且低斜率平台连续出现多少圈后升级为硬故障。
        /// 单圈只立即断正向电、软预警并继续完成反向释放。
        /// </summary>
        public int AdaptiveForwardStallConfirmCycles { get; set; } = 5;

        /// <summary>
        /// 报警触发时导出最近 N 圈（含当前 running 圈）。
        /// </summary>
        public int SnapshotLastNCycles { get; set; } = 10;

        /// <summary>
        /// 同一报警源在该时间窗内只触发一次快照（ms）。
        /// </summary>
        public int SnapshotCooldownMs { get; set; } = 2000;

        /// <summary>立即断电后继续保留的报警电流/压力尾部（ms）。</summary>
        public int SnapshotPostOffTailMs { get; set; } = 1000;
    }
}
