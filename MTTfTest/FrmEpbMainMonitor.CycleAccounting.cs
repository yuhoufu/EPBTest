using System;
using System.Threading;
using System.Threading.Tasks;
using DataOperation;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private readonly CycleAccountingSummary[] _cycleAccounting = new CycleAccountingSummary[13];
        private int _cycleAccountingReadBusy;
        private long _cycleAccountingLastReadTicks;
        private int _cycleAccountingDisplayChannel;

        private void UpdateCycleAccountingDisplay(int channel)
        {
            _cycleAccountingDisplayChannel = channel;
            var summary = Volatile.Read(ref _cycleAccounting[channel]);
            uiLabel57.Text = summary == null ? "机械完成次数\r\n圈统计读取中" :
                $"机械完成次数\r\n有效正式 {summary.ValidFormal}\r\n学习/资格 {summary.LearningOrQualification}  作废 {summary.Invalid}";
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _cycleAccountingLastReadTicks) < TimeSpan.FromSeconds(3).Ticks ||
                Interlocked.CompareExchange(ref _cycleAccountingReadBusy, 1, 0) != 0) return;
            var writer = _diskWriter;
            _ = Task.Run(() =>
            {
                try
                {
                    if (writer == null) return;
                    var current = writer.GetCycleAccounting(channel);
                    Volatile.Write(ref _cycleAccounting[channel], current);
                    Interlocked.Exchange(ref _cycleAccountingLastReadTicks, DateTime.UtcNow.Ticks);
                    if (!IsDisposed && !Disposing && IsHandleCreated)
                        BeginInvoke((Action)(() =>
                        {
                            if (!IsDisposed && _cycleAccountingDisplayChannel == channel)
                                uiLabel57.Text = $"机械完成次数\r\n有效正式 {current.ValidFormal}\r\n学习/资格 {current.LearningOrQualification}  作废 {current.Invalid}";
                        }));
                }
                catch (Exception) { /* Statistics are observational and must not block control/close. */ }
                finally { Volatile.Write(ref _cycleAccountingReadBusy, 0); }
            });
        }
    }
}
