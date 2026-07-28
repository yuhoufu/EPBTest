using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using Config;
using Controller.Adaptive;
using Controller.Alarm;
using Timing;

namespace AdaptiveControlTests
{
    internal static class Program
    {
        private static int _passed;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0].Equals("--replay", StringComparison.OrdinalIgnoreCase))
                    return ReplayCsv(args[1]);

                Run("正常夹紧", NormalClamp);
                Run("长空行程只软预警", LongEmptyTravelWarning);
                Run("反向动态释放", ReverseRelease);
                Run("保持阶段不误报断流", HoldDoesNotFault);
                Run("三样本过流", ThreeSampleOverCurrent);
                Run("开路检测", OpenCircuit);
                Run("DAQ断流", DaqStale);
                Run("单点噪声不误停", NoiseSpike);
                Run("绝对上电超限", AbsoluteOnTime);
                Run("异常高电流平台", AbnormalHighPlateau);
                Run("模型原子保存与重载", ProfilePersistence);
                Run("损坏模型回退", CorruptProfileFallback);
                Run("周期超限不追赶且圈号连续", TimerDoesNotCatchUp);
                Run("新运行复位报警停机锁存", AlarmStopLatchResetsForNewRun);
                Run("报警状态要求CSV和BIN同时存在", AlarmRequiresCsvAndBinFiles);
                Console.WriteLine($"PASS {_passed}/15");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        private static int ReplayCsv(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("回放 CSV 不存在。", path);

            var segments = ReadActiveSegments(path);
            if (segments.Count < 2)
                throw new InvalidOperationException($"需要至少两个上电脉冲，实际识别到 {segments.Count} 个。");

            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 10_000, 15, 2, 3);
            var forward = ReplaySegment(machine, segments[0], 0);
            Console.WriteLine(
                $"FORWARD stage={forward.Stage} clamp={forward.ClampReached} fault={forward.HardFault} " +
                $"reason={forward.Reason}");
            if (!forward.ClampReached || forward.HardFault) return 2;

            machine.MarkHold();
            var reverseStartMs = segments[0].Count;
            machine.ArmReverse(Tick(reverseStartMs), 100, 5_000, 3, 3);
            var reverse = ReplaySegment(machine, segments[1], reverseStartMs);
            Console.WriteLine(
                $"REVERSE stage={reverse.Stage} released={reverse.ReleaseCompleted} fault={reverse.HardFault} " +
                $"reason={reverse.Reason}");
            return reverse.ReleaseCompleted && !reverse.HardFault ? 0 : 3;
        }

        private static List<List<double>> ReadActiveSegments(string path)
        {
            var result = new List<List<double>>();
            List<double> active = null;
            using (var reader = new StreamReader(path))
            {
                var header = reader.ReadLine();
                if (header == null) return result;
                var columns = header.Split(',');
                var currentIndex = Array.FindIndex(
                    columns,
                    x => x.Trim().Equals("EpbCurrent", StringComparison.OrdinalIgnoreCase));
                if (currentIndex < 0) throw new InvalidDataException("CSV 缺少 EpbCurrent 列。");

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var cells = line.Split(',');
                    if (cells.Length <= currentIndex ||
                        !double.TryParse(
                            cells[currentIndex],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var current))
                        continue;

                    current = Math.Abs(current);
                    if (current > 0.2)
                    {
                        if (active == null) active = new List<double>();
                        active.Add(current);
                    }
                    else if (active != null)
                    {
                        if (active.Count >= 20) result.Add(active);
                        active = null;
                    }
                }
            }

            if (active != null && active.Count >= 20) result.Add(active);
            return result;
        }

        private static EpbAdaptiveDecision ReplaySegment(
            EpbAdaptiveCurrentStateMachine machine,
            List<double> samples,
            int startMs)
        {
            var last = new EpbAdaptiveDecision();
            for (var i = 0; i < samples.Count; i++)
            {
                // 现场 CSV 为约 2 kHz；使用 0.5ms 的 Stopwatch tick 回放。
                var tick = Stopwatch.Frequency +
                           (long)((startMs + i * 0.5) * Stopwatch.Frequency / 1000.0);
                var decision = machine.OnSample(tick, samples[i]);
                if (decision.ClampReached || decision.ReleaseCompleted || decision.HardFault)
                    last = decision;
                if (decision.ClampReached || decision.ReleaseCompleted || decision.HardFault)
                    break;
            }

            return last;
        }

        private static void NormalClamp()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 300, 10, _ => 1.0);
            var decision = Feed(machine, 310, 900, 10, ms => 1.0 + (ms - 310) * 0.025);
            Assert(decision.ClampReached, "未识别夹紧");
            Assert(!decision.HardFault, "正常夹紧被判硬故障");
        }

        private static void LongEmptyTravelWarning()
        {
            var profile = StableProfile();
            var machine = new EpbAdaptiveCurrentStateMachine(profile);
            machine.ArmForward(Tick(0), 100, 6500, 15, 1, 3);
            var sawWarning = false;
            for (var ms = 0; ms <= 4500; ms += 10)
            {
                var decision = machine.OnSample(Tick(ms), 1.0);
                sawWarning |= decision.SoftWarning;
                Assert(!decision.HardFault, "软时限错误触发硬故障");
            }

            Assert(sawWarning, "超过软时限未预警");
            var clamp = Feed(machine, 4510, 5100, 10, ms => 1.0 + (ms - 4510) * 0.03);
            Assert(clamp.ClampReached, "长空行程后未在同圈夹紧");
        }

        private static void ReverseRelease()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 250, 10, _ => 1.0);
            Feed(machine, 260, 900, 10, ms => 1.0 + (ms - 260) * 0.03);
            machine.MarkHold();
            machine.ArmReverse(Tick(1000), 100, 3500, 3.0, 3.0);
            var released = Feed(machine, 1000, 1900, 10, ms => ms < 1250 ? 6.0 - (ms - 1000) * 0.02 : 1.0);
            Assert(released.ReleaseCompleted, "反向低负载稳定后未释放");
        }

        private static void HoldDoesNotFault()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 250, 10, _ => 1.0);
            Feed(machine, 260, 900, 10, ms => 1.0 + (ms - 260) * 0.03);
            machine.MarkHold();
            var sample = machine.OnSample(Tick(4000), 0);
            var watchdog = machine.CheckWatchdog(Tick(5000));
            Assert(!sample.HardFault && !watchdog.HardFault, "断电保持阶段误报硬故障");
        }

        private static void ThreeSampleOverCurrent()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            machine.OnSample(Tick(0), 1);
            machine.OnSample(Tick(10), 18);
            machine.OnSample(Tick(20), 18);
            var fault = machine.OnSample(Tick(30), 18);
            Assert(fault.HardFault && fault.Reason.Contains("OverCurrent3Samples"), "三样本过流未触发");
        }

        private static void OpenCircuit()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            EpbAdaptiveDecision last = null;
            for (var ms = 0; ms <= 350; ms += 10)
            {
                last = machine.OnSample(Tick(ms), 0.01);
                if (last.HardFault) break;
            }
            Assert(last != null && last.HardFault && last.Reason.Contains("OpenCircuit"), "开路未触发");
        }

        private static void DaqStale()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            machine.OnSample(Tick(10), 1);
            var fault = machine.CheckWatchdog(Tick(120));
            Assert(fault.HardFault && fault.Reason.Contains("DaqSampleStale"), "DAQ断流未触发");
        }

        private static void NoiseSpike()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 200, 10, _ => 1.0);
            var spike = machine.OnSample(Tick(210), 18.5);
            var recovered = machine.OnSample(Tick(220), 1.1);
            Assert(!spike.HardFault && !recovered.HardFault, "单点噪声误触发硬故障");
        }

        private static void AbsoluteOnTime()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 5990, 10, _ => 1.0);
            var fault = machine.OnSample(Tick(6000), 1.0);
            Assert(fault.HardFault && fault.Reason.Contains("AbsoluteOnTime"), "绝对时限未触发");
        }

        private static void AbnormalHighPlateau()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            machine.MarkHold();
            machine.ArmReverse(Tick(0), 100, 5000, 3, 3);
            var fault = Feed(machine, 0, 1200, 10, _ => 10.0);
            Assert(fault.HardFault && fault.Reason.Contains("AbnormalHighCurrentPlateau"),
                "异常高电流平台未触发");
        }

        private static void ProfilePersistence()
        {
            var dir = CreateTempDir();
            try
            {
                var store = new EpbAdaptiveProfileStore(dir);
                var profile = store.GetOrCreate(10);
                for (var i = 0; i < 5; i++)
                    profile.AddSuccessfulCycle(1.0 + i * 0.01, 0.8 + i * 0.01, 3000 + i * 10, 1200 + i * 5);
                store.Save(profile);

                var loaded = new EpbAdaptiveProfileStore(dir).GetOrCreate(10);
                Assert(loaded.ValidSampleCount == 5, "模型样本数未持久化");
                Assert(loaded.IsStable, "五圈后模型未进入稳定状态");
                Assert(File.Exists(Path.Combine(dir, "EpbAdaptiveProfiles.xml")), "模型文件不存在");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void CorruptProfileFallback()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "EpbAdaptiveProfiles.xml"), "<broken>");
                var loaded = new EpbAdaptiveProfileStore(dir).GetOrCreate(10);
                Assert(loaded.ValidSampleCount == 0, "损坏模型未回退为空模型");
                Assert(Directory.GetFiles(dir, "*.corrupt.*").Length == 1, "损坏模型未保留副本");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void TimerDoesNotCatchUp()
        {
            var starts = new List<long>();
            var sw = Stopwatch.StartNew();
            var timer = new HighPrecisionTimer(100, OverrunPolicy.AlignToWallClock);
            timer.StartAsync(
                    3,
                    0,
                    async (cycle, token) =>
                    {
                        starts.Add(sw.ElapsedMilliseconds);
                        if (cycle == 1) await System.Threading.Tasks.Task.Delay(250, token);
                        return true;
                    })
                .GetAwaiter()
                .GetResult();

            Assert(starts.Count == 3, "超限后丢失了应完成的圈数");
            Assert(starts[1] - starts[0] >= 280, "超限后发生追赶式连续上电");
            Assert(starts[2] > starts[1], "后续圈没有按未来边界执行");
        }

        private static void AlarmStopLatchResetsForNewRun()
        {
            var latch = new ChannelAlarmStopLatch();

            latch.BeginRun(10);
            Assert(latch.TryRequestStop(10), "本次运行的首次报警未获得停机权");
            Assert(latch.IsStopRequested(10), "首次报警后锁存未置位");
            Assert(!latch.TryRequestStop(10), "同一次运行中的重复报警未被去重");

            latch.BeginRun(10);
            Assert(!latch.IsStopRequested(10), "新运行未清除上一次报警锁存");
            Assert(latch.TryRequestStop(10), "新运行中的首次报警仍被旧锁存抑制");
        }

        private static void AlarmRequiresCsvAndBinFiles()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "EPBTest_AlarmSnapshotEvidence_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var stem = Path.Combine(directory, "EPB10_Cycle_017161");
                File.WriteAllText(stem + ".csv", "header");
                Assert(
                    !AlarmSnapshotFileEvidence.HasCsvAndBin(directory, 10, 17161),
                    "只有CSV时不应允许写alarm");

                File.WriteAllBytes(stem + ".bin", Array.Empty<byte>());
                Assert(
                    AlarmSnapshotFileEvidence.HasCsvAndBin(directory, 10, 17161),
                    "CSV和BIN均存在时应允许写alarm");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static EpbAdaptiveCurrentStateMachine NewMachine()
        {
            return new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile { Channel = 10 });
        }

        private static EpbAdaptiveProfile StableProfile()
        {
            var profile = new EpbAdaptiveProfile { Channel = 10 };
            for (var i = 0; i < 5; i++)
                profile.AddSuccessfulCycle(1.0, 1.0, 3000, 1200);
            return profile;
        }

        private static EpbAdaptiveDecision Feed(
            EpbAdaptiveCurrentStateMachine machine,
            int startMs,
            int endMs,
            int stepMs,
            Func<int, double> current)
        {
            EpbAdaptiveDecision action = null;
            for (var ms = startMs; ms <= endMs; ms += stepMs)
            {
                var decision = machine.OnSample(Tick(ms), current(ms));
                if (decision.ClampReached || decision.ReleaseCompleted ||
                    decision.SoftWarning || decision.HardFault)
                    action = decision;
                if (decision.ClampReached || decision.ReleaseCompleted || decision.HardFault)
                    break;
            }

            return action ?? new EpbAdaptiveDecision();
        }

        private static long Tick(int milliseconds)
        {
            // 避免 0 被状态机视为“尚未设置”的哨兵值。
            return Stopwatch.Frequency + (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        }

        private static string CreateTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "EPBAdaptiveTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
