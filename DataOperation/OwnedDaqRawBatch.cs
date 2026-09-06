using System;
using System.Buffers;
using System.Threading;

namespace DataOperation
{
    /// <summary>标定前原始 DAQ 数据的池化独占缓冲；消费者完成后必须 Dispose。</summary>
    public sealed class OwnedDaqRawBatch : IDisposable
    {
        private double[] _values;

        private OwnedDaqRawBatch() { }

        public string Device { get; private set; } = string.Empty;
        public int ChannelCount { get; private set; }
        public int SampleCount { get; private set; }
        public DateTime Current { get; private set; }
        public DateTime Last { get; private set; }
        public long Sequence { get; private set; }
        public double[] Values => _values ?? throw new ObjectDisposedException(nameof(OwnedDaqRawBatch));

        public double this[int channel, int sample] => Values[channel * SampleCount + sample];

        public static OwnedDaqRawBatch CopyFrom(
            string device,
            double[,] source,
            DateTime current,
            DateTime last,
            long sequence = 0)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var channels = source.GetLength(0);
            var samples = source.GetLength(1);
            var length = checked(channels * samples);
            var values = ArrayPool<double>.Shared.Rent(Math.Max(1, length));
            Buffer.BlockCopy(source, 0, values, 0, length * sizeof(double));
            return new OwnedDaqRawBatch
            {
                Device = device ?? string.Empty,
                ChannelCount = channels,
                SampleCount = samples,
                Current = current,
                Last = last,
                Sequence = sequence,
                _values = values
            };
        }

        public void Dispose()
        {
            var values = Interlocked.Exchange(ref _values, null);
            if (values != null) ArrayPool<double>.Shared.Return(values, clearArray: false);
        }
    }
}
