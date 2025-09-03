using System;

// 例如在 FrmEpbMainMonitor_Load 完成 twoDeviceAiAcquirer/_do 初始化之后：
// var guard = new CurrentGuard(_do, limitA: 12.0, marginA: 1.0, tripSamples: 3);
// guard.Wire(twoDeviceAiAcquirer);

namespace IO.NI
{
    /// <summary>
    ///     简单的电流超限保护器：订阅 TwoDeviceAiAcquirer.OnFastEpbCurrent，
    ///     若某通道电流连续超过阈值（含容差）指定样本数，则立刻断电。
    /// </summary>
    public sealed class CurrentGuard
    {
        private readonly DoController _do;
        private readonly double _limitA; // 目标限值（如 12A）
        private readonly double _marginA; // 容差（如 1A）
        private readonly int[] _overCount = new int[13]; // 1..12
        private readonly int _tripSamples; // 连续多少样本触发（如 3）

        public CurrentGuard(DoController doCtrl, double limitA = 12.0, double marginA = 1.0, int tripSamples = 3)
        {
            _do = doCtrl;
            _limitA = limitA;
            _marginA = marginA;
            _tripSamples = Math.Max(1, tripSamples);
        }

        /// <summary>
        ///     绑定到采集器的快速电流事件。
        /// </summary>
        public void Wire(TwoDeviceAiAcquirer acq)
        {
            acq.OnFastEpbCurrent += OnFastSample;
        }

        /// <summary>
        ///     快速样本处理（低时延线程回调）。请保持轻量：简单判断 + 置位/断电即可。
        /// </summary>
        private void OnFastSample(int epbChannel, double amps, DateTime ts)
        {
            var a = Math.Abs(amps);
            if (a > _limitA + _marginA)
            {
                if (++_overCount[epbChannel] >= _tripSamples)
                    try
                    {
                        // 这里按你的 DO 控制接口来：示例给出两种可能
                        // 1) 断开该通道的继电器/电源
                        //_do.PowerOffChannel(epbChannel);
                        // 2) 或者直接清除该 EPB 的正/反向 DO
                        _do.SetEpbOff(epbChannel); // 示例：释放/停机
                        //_do.AllOff(); // 简化示例：一键全断（你替换为精确到通道的实现）
                    }
                    finally
                    {
                        _overCount[epbChannel] = 0; // 复位计数器
                    }
            }
            else
            {
                _overCount[epbChannel] = 0;
            }
        }
    }
}