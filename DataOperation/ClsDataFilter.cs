using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataOperation
{
    /// <summary>
    /// 中值平滑取点方式（与 UI 选项一致）。
    /// </summary>
    public enum MedianSelectPointsMode
    {
        /// <summary>最大可用点数（边界不对称也没关系）。</summary>
        MaximumNumber = 0,
        /// <summary>严格对称分布（两侧一样多，不足则缩小窗口）。需要延迟，见 <see cref="MedianStreamSymmetric"/>。</summary>
        SymmetricDistribution = 1,
        /// <summary>仅使用“之前”的点（因果窗）+当前点，不引入未来样本。</summary>
        OnlyPrevious = 2
    }

    public class ClsDataFilter
    {
        public static double[] MakeMedianFilterKeepPoint_V1(ref double[] inputArray, int MedianLens)
        {
            // 参数校验保持不变
            if (inputArray == null)
                throw new ArgumentNullException(nameof(inputArray));
            if (MedianLens <= 0 || MedianLens % 2 == 0)
                throw new ArgumentException("MedianLens must be a positive odd number.");
            if (inputArray.Length == 0)
                return new double[0];

            int length = inputArray.Length;
            double[] output = new double[length];
            int k = (MedianLens - 1) / 2;

            for (int i = 0; i < length; i++)
            {
                double[] window = new double[MedianLens];
                for (int j = 0; j < MedianLens; j++)
                {
                    // 修改点：用 Min/Max 代替 Clamp
                    int rawPos = i - k + j;
                    int clampedPos = Math.Max(Math.Min(rawPos, length - 1), 0); // 等价于 Clamp
                    window[j] = inputArray[clampedPos];
                }

                Array.Sort(window);
                output[i] = window[k];
            }

            return output;
        }


        public static double[] MakeMedianFilterKeepPoint_V2(ref double[] inputArray, int MedianLens)
        {
            if (inputArray == null)
                throw new ArgumentNullException(nameof(inputArray));
            if (MedianLens <= 0 || MedianLens % 2 == 0)
                throw new ArgumentException("MedianLens must be a positive odd number.");
            if (inputArray.Length == 0)
                return new double[0];

            int length = inputArray.Length;
            double[] output = new double[length];
            int k = (MedianLens - 1) / 2;

            for (int i = 0; i < length; i++)
            {
                double[] window = new double[MedianLens];

                // 计算有效数据区间
                int srcStart = Math.Max(i - k, 0);
                int srcEnd = Math.Min(i + k, length - 1);
                int copyLength = srcEnd - srcStart + 1;

                // 计算边界填充量
                int leftPad = k - (i - srcStart);
                int rightPad = k - (srcEnd - i);

                // 手动填充左侧 (代替Array.Fill)
                for (int p = 0; p < leftPad; p++)
                {
                    window[p] = inputArray[0];
                }

                // 复制核心数据
                if (copyLength > 0)
                {
                    Array.Copy(
                        sourceArray: inputArray,
                        sourceIndex: srcStart,
                        destinationArray: window,
                        destinationIndex: leftPad,
                        length: copyLength
                    );
                }

                // 手动填充右侧 (代替Array.Fill)
                int rightStart = leftPad + copyLength;
                for (int p = 0; p < rightPad; p++)
                {
                    window[rightStart + p] = inputArray[length - 1];
                }

                // 计算中值
                Array.Sort(window);
                output[i] = window[k];
            }

            return output;
        }


        public static double[] MakeMedianFilterReducePoint(ref double[] inputArray, int MedianLens)
        {
            // 参数校验
            if (inputArray == null)
                throw new ArgumentNullException(nameof(inputArray));
            //if (MedianLens <= 0 || MedianLens % 2 == 0)
            //    throw new ArgumentException("MedianLens must be a positive odd number.");
            if (inputArray.Length == 0)
                return new double[0];
            if (MedianLens > inputArray.Length)
                throw new ArgumentException("Window size cannot be larger than array length.");

            int length = inputArray.Length;
            int outputLength = (length + MedianLens - 1) / MedianLens; // 向上取整计算输出数组长度
            double[] output = new double[outputLength];
            //  int k = (MedianLens - 1) / 2; // 中值位置
            int k = MedianLens / 2; // 中值位置

            for (int i = 0; i < length; i += MedianLens) // 步长改为 MedianLens
            {
                double[] window = new double[MedianLens];
                int srcStart = i;
                int srcEnd = Math.Min(i + MedianLens - 1, length - 1);
                int copyLength = srcEnd - srcStart + 1;
                int rightPad = MedianLens - copyLength;

                // 复制有效数据到窗口
                Array.Copy(
                    inputArray,
                    srcStart,
                    window,
                    0,
                    copyLength
                );

                // 右侧越界时填充最后一个元素
                for (int p = 0; p < rightPad; p++)
                {
                    window[copyLength + p] = inputArray[length - 1];
                }

                // 计算中值
                Array.Sort(window);
                int outputIndex = i / MedianLens; // 输出数组索引
                output[outputIndex] = window[k];
            }

            return output;
        }

        /// <summary>
        /// 流式中值平滑（因果窗：<see cref="MedianSelectPointsMode.OnlyPrevious"/> 或
        /// “最大可用”近似因果模式：<see cref="MedianSelectPointsMode.MaximumNumber"/>）。
        /// <para>特点：零额外延迟，支持按批次连续处理。</para>
        /// </summary>
        public sealed class MedianStreamCausal
        {
            private readonly int _channels;
            private readonly int _halfWidth;
            private readonly MedianSelectPointsMode _mode;

            // 每通道保存“历史尾巴”最多 halfWidth 个样本，用于与下一批拼接
            private readonly List<double>[] _tails;

            /// <summary>
            /// 构造一个流式中值平滑器（因果）。
            /// </summary>
            /// <param name="channels">通道数（必须与批次矩阵的第 0 维一致）。</param>
            /// <param name="halfWidth">
            /// 单侧最大点数（等同于 UI 的 “Max. smoothing width on one side”）。
            /// 实际窗口长度 ≤ <c>halfWidth + 1</c>（因果模式）。
            /// </param>
            /// <param name="mode">
            /// 取点方式：推荐 <see cref="MedianSelectPointsMode.OnlyPrevious"/>；
            /// 若使用 <see cref="MedianSelectPointsMode.MaximumNumber"/>，在边界处尽量扩到右侧（等价因果）。
            /// </param>
            public MedianStreamCausal(int channels, int halfWidth,
                MedianSelectPointsMode mode = MedianSelectPointsMode.OnlyPrevious)
            {
                if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
                if (halfWidth < 0) throw new ArgumentOutOfRangeException(nameof(halfWidth));
                if (mode == MedianSelectPointsMode.SymmetricDistribution)
                    throw new ArgumentException("对称模式需要使用 MedianStreamSymmetric。", nameof(mode));

                _channels = channels;
                _halfWidth = halfWidth;
                _mode = mode;

                _tails = new List<double>[channels];
                for (int c = 0; c < channels; c++)
                    _tails[c] = new List<double>(halfWidth);
            }

            /// <summary>
            /// 处理一批数据（维度：channels × samples），返回同尺寸的中值平滑结果。
            /// 本方法保留并更新每个通道的历史尾部，使批次间无缝接续。
            /// </summary>
            /// <param name="batch">输入批次（不会被修改）。</param>
            /// <returns>平滑后的批次矩阵（与输入同维度）。</returns>
            public double[,] Process(double[,] batch)
            {
                if (batch == null) throw new ArgumentNullException(nameof(batch));
                int ch = batch.GetLength(0);
                int n = batch.GetLength(1);
                if (ch != _channels)
                    throw new ArgumentException("channels 与构造时不一致。", nameof(batch));

                var dst = new double[ch, n];

                // 临时窗口缓存，长度上限：halfWidth + 当前点（因果）
                var window = new double[Math.Max(1, _halfWidth + 1)];

                for (int c = 0; c < ch; c++)
                {
                    var tail = _tails[c];
                    int tailLen = tail.Count;

                    // —— 将 tail 与 batch 的该通道拼接视作一个连续流 —— //
                    // 我们不创建完整拼接数组，按需拷贝窗口到 window，然后排序取中位数。
                    for (int i = 0; i < n; i++)
                    {
                        // 当前样本在“连续流”中的索引：tail 后面第 i 个
                        int idxInStream = tailLen + i;

                        // 取点策略（因果）
                        // start = max(0, idx - halfWidth)；end = idx
                        int start = Math.Max(0, idxInStream - _halfWidth);
                        int end = idxInStream;

                        // “最大可用”在因果实现下等价：允许左侧尽量多取；右侧不取未来样本。
                        // 两种模式在这里是相同的，保留 _mode 只是用于语义清晰。

                        int len = end - start + 1;

                        // 将连续流[start..end]复制到 window：
                        // 1) 先从 tail 拷贝重叠段
                        int copied = 0;
                        if (start < tailLen)
                        {
                            int copyFromTail = Math.Min(tailLen - start, len);
                            for (int k = 0; k < copyFromTail; k++)
                                window[copied++] = tail[start + k];
                        }
                        // 2) 再从本批拷贝剩余段
                        int startInBatch = Math.Max(0, start - tailLen);
                        int endInBatch = end - tailLen;
                        for (int t = startInBatch; t <= endInBatch; t++)
                            window[copied++] = batch[c, t];

                        // 求中位
                        Array.Sort(window, 0, len);
                        dst[c, i] = window[(len - 1) / 2];
                    }

                    // —— 更新尾部：保留“连续流”最后 halfWidth 个样本作为下一批的历史 —— //
                    // 即：从 (tailLen + n - halfWidth) 开始的 halfWidth 个样本。
                    var newTail = new List<double>(_halfWidth);
                    int streamLen = tailLen + n;
                    int tailStart = Math.Max(0, streamLen - _halfWidth);

                    // 先从旧 tail 中取
                    for (int s = tailStart; s < Math.Min(streamLen, tailLen); s++)
                        newTail.Add(tail[s]);

                    // 再从当前批中取
                    int startFromBatch = Math.Max(0, tailStart - tailLen);
                    for (int s = startFromBatch; s < n; s++)
                        newTail.Add(batch[c, s]);

                    if (newTail.Count > _halfWidth)
                        newTail.RemoveRange(0, newTail.Count - _halfWidth);

                    _tails[c] = newTail;
                }

                return dst;
            }

            /// <summary>
            /// 清空所有通道的历史尾部（例如切换数据源或重置滤波器时调用）。
            /// </summary>
            public void Reset()
            {
                for (int c = 0; c < _channels; c++)
                    _tails[c].Clear();
            }
        }

        /// <summary>
        /// 流式中值平滑（严格对称窗：<see cref="MedianSelectPointsMode.SymmetricDistribution"/>）。
        /// <para>特点：需要引入 <c>halfWidth</c> 个样本的固定延迟；适合用于显示/报表。</para>
        /// </summary>
        public sealed class MedianStreamSymmetric
        {
            private readonly int _channels;
            private readonly int _halfWidth;

            // 每通道一个原始样本队列，长度保持在 [0 .. 2*halfWidth] 的基础上增长/滑动
            private readonly List<double>[] _buffers;

            public MedianStreamSymmetric(int channels, int halfWidth)
            {
                if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
                if (halfWidth < 0) throw new ArgumentOutOfRangeException(nameof(halfWidth));
                _channels = channels;
                _halfWidth = halfWidth;
                _buffers = new List<double>[channels];
                for (int c = 0; c < channels; c++)
                    _buffers[c] = new List<double>(2 * halfWidth + 1);
            }

            /// <summary>
            /// 处理一批数据并输出“当前可产出的”平滑结果。
            /// 由于对称窗需要“未来”样本，本次调用可能产出少于输入的样本数（有固定延迟）。
            /// </summary>
            /// <param name="batch">输入批次（channels × samples）。</param>
            /// <returns>
            /// 输出矩阵（channels × producedSamples）。<br/>
            /// <b>注意：</b> producedSamples 可能小于 batch 的样本数；时间上相当于整体滞后 halfWidth。
            /// </returns>
            public double[,] Process(double[,] batch)
            {
                if (batch == null) throw new ArgumentNullException(nameof(batch));
                int ch = batch.GetLength(0);
                int n = batch.GetLength(1);
                if (ch != _channels)
                    throw new ArgumentException("channels 与构造时不一致。", nameof(batch));

                // 第一遍先把可产出数量计算出来（对每通道相同）
                // 滑动窗口长度 L = 2*halfWidth + 1，当 buffer.Count >= L 时，每前进 1 个可产出 1 个
                int produced = 0; // 本批最多可产出的样本数
                // 对称模式下，各通道 produced 相同；用第 0 通道估算
                {
                    int count = _buffers[0].Count + n;
                    int L = 2 * _halfWidth + 1;
                    produced = Math.Max(0, count - L + 1);
                }

                var dst = new double[ch, produced];
                var window = new double[Math.Max(1, 2 * _halfWidth + 1)];

                for (int c = 0; c < ch; c++)
                {
                    var buf = _buffers[c];

                    // 先将本批样本追加到缓冲
                    for (int i = 0; i < n; i++)
                        buf.Add(batch[c, i]);

                    // 只要缓冲长度 >= L，就可以产出一个（窗口中心是 halfWidth 处）
                    int L = 2 * _halfWidth + 1;
                    int outIdx = 0;
                    while (buf.Count >= L)
                    {
                        // 拷贝窗口 [0..L-1] 求中位
                        for (int k = 0; k < L; k++) window[k] = buf[k];
                        Array.Sort(window, 0, L);
                        dst[c, outIdx++] = window[(L - 1) / 2];

                        // 滑动 1 个：弹出缓冲第一个样本
                        buf.RemoveAt(0);
                    }
                }
                return dst;
            }

            /// <summary>
            /// 刷新尾部：当数据流结束时调用，输出最后不足 <c>2*halfWidth</c> 的剩余样本，
            /// 边界处窗口自动缩小（对称不再严格）。
            /// </summary>
            public double[,] Flush()
            {
                int ch = _channels;
                // 估算最多还能输出的样本数 = 当前缓冲长度
                int remain = _buffers[0].Count;
                var dst = new double[ch, remain];

                var win = new List<double>(2 * _halfWidth + 1);

                for (int c = 0; c < ch; c++)
                {
                    var buf = _buffers[c];
                    int outIdx = 0;

                    // 逐位置做“可用窗口”的中值（边界缩小）
                    for (int i = 0; i < buf.Count; i++)
                    {
                        win.Clear();
                        int left = Math.Max(0, i - _halfWidth);
                        int right = Math.Min(buf.Count - 1, i + _halfWidth);
                        for (int k = left; k <= right; k++) win.Add(buf[k]);
                        win.Sort();
                        dst[c, outIdx++] = win[(win.Count - 1) / 2];
                    }

                    buf.Clear();
                }
                return dst;
            }

            /// <summary>重置内部缓冲。</summary>
            public void Reset()
            {
                for (int c = 0; c < _channels; c++)
                    _buffers[c].Clear();
            }
        }

        /// <summary>按参数名维护独立的在线滤波状态（线程安全）。</summary>
        public sealed class FastFilter
        {
            private readonly int _k;
            private double _alpha;
            private double _maxSlewAperSec;

            // 每个参数名一份状态
            private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

            private sealed class State
            {
                // 固定小窗环形缓冲（K ≤ 7 时用插入排序求中值）
                public double[] Win;
                public int Count;
                public int Head;

                // EWMA 状态
                public bool HasY;
                public double Y;

                // Slew limiter
                public DateTime LastTs;
            }

            /// <param name="medianK">因果中值窗长（奇数，建议 3 或 5）。</param>
            /// <param name="ewmaAlpha">EWMA 系数 α（0..1，越大越贴近实时）。</param>
            /// <param name="maxSlewAperSec">最大允许电流变化速率（A/s）。</param>
            public FastFilter(int medianK, double ewmaAlpha, double maxSlewAperSec)
            {
                if (medianK < 1 || medianK % 2 == 0) medianK = 3;
                _k = medianK;
                _alpha = Math.Min(0.95, Math.Max(0.05, ewmaAlpha));
                _maxSlewAperSec = Math.Max(0.0, maxSlewAperSec);
            }


            /// <summary>把 FastFilter 里的 EWMA α 动态调整到新值（0..1）。</summary>
            /// <param name="alpha">新的 α（建议 0.05..0.95）。</param>
            public void SetAlpha(double alpha)
            {
                _alpha = Math.Max(0.05, Math.Min(0.95, alpha));
            }

            /// <summary>设置最大电流变化速率（A/s）。设 0 表示关闭限速。</summary>
            public void SetMaxSlewAperSec(double maxSlew)
            {
                _maxSlewAperSec = Math.Max(0.0, maxSlew);
            }


            /// <summary>
            /// 输入单点工程值，返回经“因果中值→EWMA→限速/钳制”后的快照。
            /// 仅依赖历史与当前样本，不引入半窗延迟。
            /// </summary>
            /// <param name="key">参数名（如 EPB1_current）。</param>
            /// <param name="x">当前工程值（已做零点/比例换算）。</param>
            /// <param name="ts">当前样本的时间戳。</param>
            public double Update(string key, double x, DateTime ts)
            {
                var st = _states.GetOrAdd(key, _ => new State { Win = new double[_k], LastTs = ts });

                // ① 因果中值：把 x 放进环形缓冲，复制已用长度到 temp，插排取中值
                st.Win[st.Head] = x;
                st.Head = (st.Head + 1) % _k;
                if (st.Count < _k) st.Count++;

                double med;
                if (st.Count == 1) med = x;
                else
                {
                    var n = st.Count;
                    var temp = new double[n];
                    // 展开为线性顺序
                    for (int i = 0; i < n; i++)
                    {
                        int idx = (st.Head - i - 1 + _k) % _k;
                        temp[i] = st.Win[idx];
                    }
                    // 小 n 插入排序
                    for (int i = 1; i < n; i++)
                    {
                        double v = temp[i];
                        int j = i - 1;
                        while (j >= 0 && temp[j] > v) { temp[j + 1] = temp[j]; j--; }
                        temp[j + 1] = v;
                    }
                    med = temp[n / 2];
                }

                // ② EWMA
                double y = st.HasY ? (_alpha * med + (1 - _alpha) * st.Y) : med;

                // ③ Slew 限速/离群钳制
                if (_maxSlewAperSec > 1e-6 && st.HasY)
                {
                    var dt = Math.Max(1e-6, (ts - st.LastTs).TotalSeconds);
                    var maxStep = _maxSlewAperSec * dt;
                    var dy = y - st.Y;
                    if (dy > maxStep) y = st.Y + maxStep;
                    else if (dy < -maxStep) y = st.Y - maxStep;
                }

                st.Y = y;
                st.HasY = true;
                st.LastTs = ts;
                return y;
            }
        }



    }
}
