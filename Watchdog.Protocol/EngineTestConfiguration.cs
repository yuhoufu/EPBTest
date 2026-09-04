using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    // Editable metadata only. Counts, run times, isolation and permits are intentionally absent.
    public sealed class EngineTestConfiguration
    {
        public string TestName { get; set; } = string.Empty;
        public string StoreDir { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public double TestPeriod { get; set; }
        public int TestTarget { get; set; }
        public bool IsSameCycleForAllEpb { get; set; }
        public EngineRunnerConfiguration[] Channels { get; set; } = Array.Empty<EngineRunnerConfiguration>();
        public EngineHydraulicSetting[] Hydraulics { get; set; } = Array.Empty<EngineHydraulicSetting>();

        public bool IsStructurallyValid() => !string.IsNullOrWhiteSpace(TestName) && TestName.Length <= 128 &&
            !string.IsNullOrWhiteSpace(StoreDir) && StoreDir.Length <= 1024 && Owner?.Length <= 256 && Description?.Length <= 8192 &&
            EngineUiContract.IsFinite(TestPeriod) && TestPeriod > 0 && TestPeriod <= 600 && TestTarget > 0 &&
            Channels?.Length == 12 && Channels.All(c => c?.IsStructurallyValid() == true) &&
            Channels.Select(c => c.Channel).Distinct().Count() == 12 &&
            Hydraulics?.Length == 2 && Hydraulics.All(h => h != null && h.Id >= 1 && h.Id <= 2 &&
                h.PressureThresholdBar >= 0 && h.PressureThresholdBar <= 1000) && Hydraulics.Select(h => h.Id).Distinct().Count() == 2;

        public string ComputeSha256()
        {
            // Stable channel/group order; no telemetry revision or changing progress participates.
            var copy = Clone();
            copy.Channels = copy.Channels.OrderBy(c => c.Channel).ToArray();
            copy.Hydraulics = copy.Hydraulics.OrderBy(h => h.Id).ToArray();
            var json = new JavaScriptSerializer();
            // Framework reflection property enumeration is not a wire-order contract:
            // cache warming can move Selected/TargetTotalCount without changing values.
            // Canonicalize every object key, not only the channel/group array order.
            return SupervisorProtocol.ComputeTextSha256(json.Serialize(Canonicalize(json.DeserializeObject(json.Serialize(copy)))));
        }

        private static object Canonicalize(object value)
        {
            if (value is IDictionary<string, object> fields)
            {
                var ordered = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var field in fields) ordered.Add(field.Key, Canonicalize(field.Value));
                return ordered;
            }
            if (!(value is string) && value is IEnumerable items)
                return items.Cast<object>().Select(Canonicalize).ToArray();
            return value;
        }

        public EngineTestConfiguration Clone()
        {
            var json = new JavaScriptSerializer();
            return json.Deserialize<EngineTestConfiguration>(json.Serialize(this));
        }
    }

    public sealed class EngineHydraulicSetting
    {
        public int Id { get; set; }
        public bool Enabled { get; set; }
        public int PressureThresholdBar { get; set; }
    }

    public sealed class EngineRunnerConfiguration
    {
        public int Channel { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Selected { get; set; }
        public int TargetTotalCount { get; set; }
        public double ForwardA { get; set; }
        public double SafetyMarginA { get; set; }
        public int FwdOnLimitMs { get; set; }
        public int HoldMs { get; set; }
        public double RevDecayLimitA { get; set; }
        public int RevDecayRigidMaxMs { get; set; }
        public int RevEmptyFixedMs { get; set; }
        public int? PreReleaseKeepMs { get; set; }
        public int? PreReleaseDetectTimeoutMs { get; set; }
        public int PeakIgnoreMs { get; set; }
        public int ForwardProgressConfirmMs { get; set; }
        public double ForwardMinimumRiseSlopeAperMs { get; set; }
        public int ForwardProgressDeadlineMs { get; set; }
        public int ReverseProgressConfirmMs { get; set; }
        public double ReverseMinimumDecaySlopeAperMs { get; set; }
        public int ReverseProgressDeadlineMs { get; set; }
        public double OffCurrentClearThresholdA { get; set; }
        public int OffCurrentClearTimeoutMs { get; set; }

        public bool IsStructurallyValid() => Channel >= 1 && Channel <= 12 && Name?.Length <= 128 && TargetTotalCount >= 0 &&
            new[] { ForwardA, SafetyMarginA, RevDecayLimitA, ForwardMinimumRiseSlopeAperMs,
                ReverseMinimumDecaySlopeAperMs, OffCurrentClearThresholdA }.All(v => EngineUiContract.IsFinite(v) && v >= 0) &&
            new[] { FwdOnLimitMs, HoldMs, RevDecayRigidMaxMs, RevEmptyFixedMs, PeakIgnoreMs,
                ForwardProgressConfirmMs, ForwardProgressDeadlineMs, ReverseProgressConfirmMs, ReverseProgressDeadlineMs,
                OffCurrentClearTimeoutMs, PreReleaseKeepMs ?? 0, PreReleaseDetectTimeoutMs ?? 0 }.All(v => v >= 0 && v <= 600000);
    }

    public sealed class TestConfigurationCommit
    {
        public long BaseConfigurationRevision { get; set; }
        public string BaseConfigurationSha256 { get; set; } = string.Empty;
        public EngineTestConfiguration Configuration { get; set; }
        public EngineDaqConfiguration DaqConfiguration { get; set; }
        public AoCalibrationCommit AoCalibration { get; set; }
        public bool IsStructurallyValid() => BaseConfigurationRevision >= 0 &&
            RecoveryFailureReceipt.IsSha256(BaseConfigurationSha256) &&
            new object[] { Configuration, DaqConfiguration, AoCalibration }.Count(value => value != null) == 1 &&
            (Configuration?.IsStructurallyValid() == true || DaqConfiguration?.IsStructurallyValid() == true || AoCalibration?.IsStructurallyValid() == true);
        public string ComputeSha256() => SupervisorProtocol.ComputeTextSha256(
            (AoCalibration != null ? "AoCommitV1\n" : DaqConfiguration == null ? string.Empty : "DaqCommitV1\n") +
            BaseConfigurationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            BaseConfigurationSha256 + "\n" + (AoCalibration != null ? AoCalibration.ComputeSha256() :
                DaqConfiguration == null ? Configuration.ComputeSha256() : DaqConfiguration.ComputeSha256()));
        public TestConfigurationCommit Clone() => new TestConfigurationCommit
        {
            BaseConfigurationRevision = BaseConfigurationRevision, BaseConfigurationSha256 = BaseConfigurationSha256,
            Configuration = Configuration?.Clone(), DaqConfiguration = DaqConfiguration?.Clone(), AoCalibration = AoCalibration?.Clone()
        };
    }
}
