using System;
using System.Collections.Generic;
using MTTFTest.SafetyHardware;

namespace AdaptiveControlTests
{
    internal static class SafetyFeedbackWindowTests
    {
#if SAFETY_FEEDBACK_STANDALONE
        private static int Main()
        {
            try
            {
                Console.WriteLine("PASS " + RunAll() + " safety feedback scenarios");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
#endif
        public static int RunAll()
        {
            var count = 0;
            Check("all channels must remain safe across new samples", () =>
            {
                var window = Window();
                Require(!window.Observe(Frame(110), 110));
                Require(!window.Observe(Frame(160), 160));
                Require(window.Observe(Frame(210), 210));
            }, ref count);
            Check("cached values cannot complete the hold", () =>
            {
                var window = Window();
                Require(!window.Observe(Frame(110), 110));
                Require(!window.Observe(Frame(110), 210));
                Require(!window.Observe(Frame(220), 220));
            }, ref count);
            foreach (var invalidTime in new[] { 99.0, 100.0, 300.0, double.NaN })
                Check("reject pre-command/future/invalid feedback " + invalidTime, () =>
                {
                    var window = Window();
                    Require(!window.Observe(Frame(invalidTime), 210));
                    Require(!window.Observe(Frame(220), 220));
                }, ref count);
            Check("missing channel resets stability", () =>
            {
                var window = Window();
                window.Observe(Frame(110), 110);
                var missing = Frame(160);
                missing.Remove("Pressure_1");
                Require(!window.Observe(missing, 160));
                Require(!window.Observe(Frame(210), 210));
            }, ref count);
            foreach (var unsafeCurrent in new[] { 0.2, -0.2, double.NaN, double.PositiveInfinity })
                Check("unsafe current resets all-channel hold " + unsafeCurrent, () =>
                {
                    var window = Window();
                    window.Observe(Frame(110), 110);
                    var frame = Frame(160);
                    frame["EPB4_current"] = new SafetyFeedbackSample(unsafeCurrent, 160);
                    Require(!window.Observe(frame, 160));
                    Require(!window.Observe(Frame(210), 210));
                }, ref count);
            Check("one pressure group unsafe blocks completion", () =>
            {
                var window = Window();
                window.Observe(Frame(110), 110);
                var frame = Frame(210);
                frame["Pressure_1"] = new SafetyFeedbackSample(6, 210);
                Require(!window.Observe(frame, 210));
            }, ref count);
            Check("observation gap requires a new hold", () =>
            {
                var window = Window();
                window.Observe(Frame(110), 110);
                Require(!window.Observe(Frame(400), 400));
                Require(window.Observe(Frame(500), 500));
            }, ref count);
            Check("clock regression cannot complete hold", () =>
            {
                var window = Window();
                window.Observe(Frame(110), 110);
                Require(!window.Observe(Frame(109), 109));
                Require(!window.Observe(Frame(210), 210));
            }, ref count);
            Check("slowest channel determines proven interval", () =>
            {
                var window = Window();
                window.Observe(Frame(110), 110);
                var frame = Frame(210);
                frame["Pressure_1"] = new SafetyFeedbackSample(1, 160);
                Require(!window.Observe(frame, 210));
                Require(window.Observe(Frame(220), 220));
            }, ref count);
            return count;
        }

        private static SafetyFeedbackWindow Window() => new SafetyFeedbackWindow(
            new[] { "EPB4_current", "Pressure_1" }, new[] { -0.1, -1.0 },
            new[] { 0.1, 5.0 }, 100, 150, 100);

        private static Dictionary<string, SafetyFeedbackSample> Frame(double time) =>
            new Dictionary<string, SafetyFeedbackSample>
            {
                ["EPB4_current"] = new SafetyFeedbackSample(0, time),
                ["Pressure_1"] = new SafetyFeedbackSample(1, time)
            };

        private static void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Safety feedback regression failed.");
        }

        private static void Check(string name, Action test, ref int count)
        {
            test();
            count++;
            Console.WriteLine("PASS " + name);
        }
    }
}
