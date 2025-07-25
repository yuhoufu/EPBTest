using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataOperation
{
    public class AutoLearnDetector
    {
        private readonly int _highThreshold;  // 连续高值的阈值（默认 3）
        private readonly int _zeroThreshold;  // 连续 0 的阈值（默认 10）

        private int _zeroCount = 0;          // 记录连续 0 的周期数
        private int _nonZeroCount = 0;       // 记录连续 >0 的周期数
        private bool _hadValidHighValue = false; // 是否已经出现过有效高值

        // 默认构造函数（高值阈值=3，0 阈值=10）
        public AutoLearnDetector() : this(3, 10) { }

        // 自定义阈值构造函数
        public AutoLearnDetector(int highThreshold, int zeroThreshold)
        {
            if (highThreshold <= 0)
                throw new ArgumentException("高值阈值必须大于 0", nameof(highThreshold));
            if (zeroThreshold <= 0)
                throw new ArgumentException("0 值阈值必须大于 0", nameof(zeroThreshold));

            _highThreshold = highThreshold;
            _zeroThreshold = zeroThreshold;
        }

        public string ProcessForceValue(double currentValue)
        {
            if (currentValue > 0.001)
            {
                _nonZeroCount++;         // 非零计数 +1
                _zeroCount = 0;           // 重置 0 计数器

                // 如果连续 _highThreshold 个周期 >0，则标记为有效高值
                if (_nonZeroCount >= _highThreshold)
                {
                    _hadValidHighValue = true;
                }
            }
            else if (currentValue < 0.001)
            {
                _nonZeroCount = 0;        // 重置非零计数
                if (_hadValidHighValue)   // 如果之前有有效高值，则开始 0 计数
                {
                    _zeroCount++;
                }
            }

            // 如果之前有有效高值，并且连续 _zeroThreshold 个 0，则返回 "OK"
            if (_hadValidHighValue && _zeroCount >= _zeroThreshold)
            {
                return "OK";
            }

            return "WAITING";  // 否则返回等待状态
        }

        // 可选：重置检测器状态（如果需要复用）
        public void Reset()
        {
            _zeroCount = 0;
            _nonZeroCount = 0;
            _hadValidHighValue = false;
        }
    }
}
