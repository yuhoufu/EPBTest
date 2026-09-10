using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using Controller;
using IO.NI;

namespace AdaptiveControlTests
{
    internal static class PhysicalSafetyBatchTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Check("停止联合取证保留已卸压的禁用组且不虚构无液压组", () =>
            {
                var configured = new List<Config.HydraulicItem>
                {
                    new Config.HydraulicItem { Id = 1, Enabled = true },
                    new Config.HydraulicItem { Id = 2, Enabled = false }
                };
                var scope = EpbManager.SelectStopPhysicalSafetyHydraulics(configured);
                Require(scope.Length == 2 && scope[0].Id == 1 && scope[1].Id == 2 && !scope[1].Enabled);
                configured.Clear();
                Require(scope.Length == 2);
                Require(EpbManager.SelectStopPhysicalSafetyHydraulics(configured).Length == 0);
            }, ref passed);
            Check("安全批次仅使用持久化标定且不接受界面置零偏移", () =>
            {
                var capture = typeof(PhysicalSafetyBatchSample).GetMethod("Capture",
                    BindingFlags.Static | BindingFlags.NonPublic);
                foreach (var parameter in capture.GetParameters())
                    Require(parameter.Name != "zeroOffset");
                var sample = PhysicalSafetyBatchSample.Capture("Dev1", 1, 1,
                    Stopwatch.GetTimestamp(), 1000, new double[,] { { 0.2, 0.4 } }, 0,
                    2, 0.3, 0.1, -10, 10, FastSignalQualityFlags.None, DateTime.UtcNow);
                Require(sample.Valid && Math.Abs(sample.Maximum - 0.9) < 1e-12 &&
                    Math.Abs(sample.MaximumAbsolute - 0.9) < 1e-12);
            }, ref passed);
            foreach (var failures in new[] { 0, 1, 2, 3 })
                Check("双设备停止始终尝试两侧并保留异常 " + failures, () =>
                {
                    var calls = new List<int>();
                    var first = new TimeoutException("Dev1");
                    var second = new InvalidOperationException("Dev2");
                    Exception observed = null;
                    try
                    {
                        TwoDeviceAiAcquirer.StopBothDevices(
                            () => { calls.Add(1); if ((failures & 1) != 0) throw first; },
                            () => { calls.Add(2); if ((failures & 2) != 0) throw second; });
                    }
                    catch (Exception ex) { observed = ex; }
                    Require(calls.Count == 2 && calls[0] == 1 && calls[1] == 2);
                    if (failures == 0) Require(observed == null);
                    else if (failures == 1) Require(ReferenceEquals(observed, first));
                    else if (failures == 2) Require(ReferenceEquals(observed, second));
                    else Require(observed is AggregateException aggregate &&
                        aggregate.InnerExceptions.Count == 2 &&
                        ReferenceEquals(aggregate.InnerExceptions[0], first) &&
                        ReferenceEquals(aggregate.InnerExceptions[1], second));
                }, ref passed);
            Check("重复StopDevice不能绕过上次未完成释放", () =>
            {
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(TwoDeviceAiAcquirer);
                // No constructor or NI task is run: retain only an unresolved
                // old generation and call the production StopDevice entry.
                var acquirer = FormatterServices.GetUninitializedObject(type);
                var dictionaryField = type.GetField("_quiescingReads", fields);
                var dictionary = Activator.CreateInstance(dictionaryField.FieldType);
                dictionaryField.SetValue(acquirer, dictionary);
                var stateType = type.GetNestedType("DeviceReadState", BindingFlags.NonPublic);
                var state = FormatterServices.GetUninitializedObject(stateType);
                using (var quiesced = new ManualResetEventSlim(false))
                {
                    stateType.GetField("<Quiesced>k__BackingField", fields).SetValue(state, quiesced);
                    dictionary.GetType().GetMethod("TryAdd").Invoke(dictionary, new[] { (object)"Dev1", state });
                    try
                    {
                        type.GetMethod("StopDevice", fields).Invoke(acquirer, new object[] { "Dev1" });
                        throw new InvalidOperationException("重复停止意外成功");
                    }
                    catch (TargetInvocationException ex)
                    {
                        Require(ex.InnerException is InvalidOperationException &&
                            ex.InnerException.Message == "PreviousDaqGenerationNotQuiesced:Dev1");
                    }
                    Require((int)dictionary.GetType().GetProperty("Count").GetValue(dictionary) == 1);
                }
            }, ref passed);
            Check("DAQ后台释放与读线程都完成才允许交接", () =>
            {
                Require(TwoDeviceAiAcquirer.IsStoppedGenerationQuiesced(true, true, false, Task.CompletedTask));
                Require(!TwoDeviceAiAcquirer.IsStoppedGenerationQuiesced(false, true, false, Task.CompletedTask));
                Require(!TwoDeviceAiAcquirer.IsStoppedGenerationQuiesced(true, false, false, Task.CompletedTask));
                Require(!TwoDeviceAiAcquirer.IsStoppedGenerationQuiesced(true, true, true, Task.CompletedTask));
                Require(!TwoDeviceAiAcquirer.IsStoppedGenerationQuiesced(true, true, false, null));
            }, ref passed);
            Check("DAQ释放任务未完成失败或取消均不允许交接", () =>
            {
                var pending = new TaskCompletionSource<bool>();
                Require(!TwoDeviceAiAcquirer.IsStoppedGenerationQuiesced(true, true, false, pending.Task));
                pending.SetException(new InvalidOperationException("simulated NI dispose failure"));
                Require(!TwoDeviceAiAcquirer.IsStoppedGenerationQuiesced(true, true, false, pending.Task));
                var observed = pending.Task.Exception;
                var cancelled = new TaskCompletionSource<bool>();
                cancelled.SetCanceled();
                Require(!TwoDeviceAiAcquirer.IsStoppedGenerationQuiesced(true, true, false, cancelled.Task));
            }, ref passed);
            Check("原始整批最大值保留早期正负尖峰", () =>
            {
                var sample = Sample(1110, 1, new[] { -0.3, 0.2, 0.0 });
                Require(sample.Valid && sample.MaximumAbsolute == 0.3 && sample.Maximum == 0.2);
                Require(sample.FirstSampleMs < sample.LastSampleMs - 1.9);
                Require(sample.FirstSampleUtc == new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(1108));
            }, ref passed);
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, -10.0, 10.0 })
                Check("无效或饱和整批拒绝 " + value, () =>
                    Require(!Sample(1110, 1, new[] { value, 0.0, 0.0 }).Valid), ref passed);
            Check("采样质量异常拒绝", () => Require(!Sample(1110, 1,
                quality: FastSignalQualityFlags.SequenceDiscontinuity).Valid), ref passed);
            Check("迟到旧代次及旧序号不能覆盖新物理证据", () =>
            {
                var current = Sample(1160, 2, generation: 2);
                Require(ReferenceEquals(current, PhysicalSafetyBatchSample.Newer(current, Sample(1170, 99))));
                Require(ReferenceEquals(current, PhysicalSafetyBatchSample.Newer(current, Sample(1170, 1, generation: 2))));
                var next = Sample(1180, 3, generation: 2);
                Require(ReferenceEquals(next, PhysicalSafetyBatchSample.Newer(current, next)));
            }, ref passed);
            Check("快速轮询缓存不能成功也不破坏新样本稳定窗", () =>
            {
                var window = Window();
                var first = Frame(1110, 1);
                Require(!window.Observe(first, 1111));
                Require(!window.Observe(first, 1115));
                Require(!window.Observe(Frame(1160, 2), 1161));
                Require(window.Observe(Frame(1215, 3), 1216));
            }, ref passed);
            Check("一组缓存时另一组超限仍重置联合窗口", () =>
            {
                var window = Window();
                var first = Frame(1110, 1);
                Require(!window.Observe(first, 1111));
                first["Pressure_1"] = Sample(1160, 2, new[] { 6.0, 0.0, 0.0 });
                Require(!window.Observe(first, 1161));
                Require(!window.Observe(Frame(1215, 3), 1216));
                Require(!window.Observe(Frame(1265, 4), 1266));
                Require(window.Observe(Frame(1320, 5), 1321));
            }, ref passed);
            Check("DAQ换代必须重新累计安全时长", () =>
            {
                var window = Window();
                Require(!window.Observe(Frame(1110, 1), 1111));
                Require(!window.Observe(Frame(1160, 2), 1161));
                Require(!window.Observe(Frame(1215, 1, generation: 2), 1216));
            }, ref passed);
            Check("跨越断能时刻的整批不能作为动作后反馈", () =>
            {
                var window = Window();
                Require(!window.Observe(Frame(1001, 1), 1002));
            }, ref passed);
            Check("压力组缺失不能确认", () =>
            {
                var window = Window();
                var frame = Frame(1110, 1); frame.Remove("Pressure_1");
                Require(!window.Observe(frame, 1111));
            }, ref passed);
            Check("超龄批次不能重新打时间戳放行", () =>
            {
                var window = Window();
                Require(!window.Observe(Frame(1110, 1), 1300));
                Require(!window.Observe(Frame(1310, 2), 1311));
            }, ref passed);
            return passed;
        }

        private static PhysicalSafetyBatchWindow Window() => new PhysicalSafetyBatchWindow(
            new[] { "EPB1_current", "Pressure_1" }, 1, new[] { 0.1, 5.0 }, 1000, 100, 100);

        private static Dictionary<string, PhysicalSafetyBatchSample> Frame(double endMs, long sequence, long generation = 1) =>
            new Dictionary<string, PhysicalSafetyBatchSample>
            {
                ["EPB1_current"] = Sample(endMs, sequence, generation: generation),
                ["Pressure_1"] = Sample(endMs, sequence, generation: generation)
            };

        private static PhysicalSafetyBatchSample Sample(double endMs, long sequence, double[] raw = null,
            long generation = 1, FastSignalQualityFlags quality = FastSignalQualityFlags.None)
        {
            raw ??= new[] { 0.0, 0.0, 0.0 };
            var matrix = new double[1, raw.Length];
            for (var index = 0; index < raw.Length; index++) matrix[0, index] = raw[index];
            return PhysicalSafetyBatchSample.Capture("Dev1", generation, sequence,
                (long)(endMs * Stopwatch.Frequency / 1000.0), 1000, matrix, 0,
                1, 0, 0, -10, 10, quality,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(endMs));
        }

        private static void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("物理批次安全证据断言失败");
        }

        private static void Check(string name, Action action, ref int passed)
        {
            action(); passed++; Console.WriteLine("PASS " + name);
        }
    }
}
