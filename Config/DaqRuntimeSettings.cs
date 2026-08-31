using System;
using System.Collections.Specialized;
using System.Globalization;

namespace Config
{
    /// <summary>经验证后在一个运行会话内保持不变的 DAQ 参数。</summary>
    public sealed class DaqRuntimeSettings
    {
        public const double DefaultSampleRateHz = 2000;
        public const int DefaultSamplesPerChannel = 20;

        public DaqRuntimeSettings(double sampleRateHz, int samplesPerChannel)
        {
            if (double.IsNaN(sampleRateHz) || double.IsInfinity(sampleRateHz) ||
                sampleRateHz <= 0 || sampleRateHz > 1000000)
                throw new ArgumentOutOfRangeException(nameof(sampleRateHz),
                    "DaqRuntimeConfigInvalid: sampleRateHz must be finite and within (0, 1000000].");
            if (samplesPerChannel < 1 || samplesPerChannel > 1000000)
                throw new ArgumentOutOfRangeException(nameof(samplesPerChannel),
                    "DaqRuntimeConfigInvalid: samplesPerChannel must be within [1, 1000000].");
            var batchPeriodMs = samplesPerChannel * 1000.0 / sampleRateHz;
            if (batchPeriodMs < 0.1 || batchPeriodMs > 1000)
                throw new ArgumentOutOfRangeException(nameof(samplesPerChannel),
                    "DaqRuntimeConfigInvalid: DAQ batch period must be within [0.1, 1000] ms.");
            SampleRateHz = sampleRateHz;
            SamplesPerChannel = samplesPerChannel;
        }

        public double SampleRateHz { get; }
        public int SamplesPerChannel { get; }
        public double BatchPeriodMs => SamplesPerChannel * 1000.0 / SampleRateHz;

        public static DaqRuntimeSettings Load(NameValueCollection values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            double sampleRate;
            int samples;
            if (!double.TryParse(values["DaqFrequency"], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out sampleRate))
                throw new InvalidOperationException("DaqRuntimeConfigInvalid: DaqFrequency missing or invalid.");
            if (!int.TryParse(values["SamplesPerChannel"], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out samples))
                throw new InvalidOperationException("DaqRuntimeConfigInvalid: SamplesPerChannel missing or invalid.");
            return new DaqRuntimeSettings(sampleRate, samples);
        }
    }
}
