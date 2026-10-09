using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Controller;

namespace AdaptiveControlTests
{
    internal static class FormalHydraulicScheduleTests
    {
        public static int RunAll()
        {
            VerifyWindow(5333, 10400, 10410, 15000);
            VerifyWindow(-5333, 10400, 10410, 15000);
            VerifyWindow(3600000, 14000, 15000, 30000);
            VerifyWindow(-3600000, 14000, 15000, 30000);
            Console.WriteLine("PASS 正式液压资格/相位/截止点不受 UTC 正反跳变影响 (4)");
            return 4;
        }

        private static void VerifyWindow(int utcJumpMs, int qualifiedTimestamp,
            long expectedAnchor, long expectedDeadline)
        {
            long now = 0;
            var utc = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
            var clock = new FormalScheduleClock(
                () => Interlocked.Read(ref now), 1000, () => utc,
                (delay, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Interlocked.Add(ref now, delay);
                    return Task.CompletedTask;
                });
            var grant = new FormalBatchSlotGrant(0, 0, 0, 0, 15000, 15000, clock);
            // Simulate the system clock changing after the shared slot has been granted.
            utc = utc.AddMilliseconds(utcJumpMs);
            var run = Guid.NewGuid();
            var key = new GlobalHydraulicSlotKey(run, HydraulicPhaseKind.Formal, 0);
            Task<HydraulicCycleLease> Enter(int hydraulic, IReadOnlyList<int> members,
                CancellationToken token)
            {
                Interlocked.Exchange(ref now, qualifiedTimestamp);
                var observedUtc = utc;
                return Task.FromResult(new HydraulicCycleLease(
                    new HydraulicGenerationKey(run, hydraulic, HydraulicPhaseKind.Formal, 0),
                    members, new PressureQualification(hydraulic, 1, 70, 70,
                        observedUtc, 0, 70, 70, 70, 7, observedUtc),
                    observedUtc, Task.CompletedTask, 1));
            }
            var coordinator = new GlobalHydraulicSlotCoordinator();
            var operation = coordinator.JoinFormalAsync(
                key, 2, 7, 1, grant.PlannedUtc, grant.PlannedUtc, 15000, 1600, 10,
                Enter, (_, __) => Task.CompletedTask,
                CancellationToken.None, CancellationToken.None, grant);
            if (!operation.Wait(2000))
                throw new Exception("UTC 跳变错误地延迟了正式液压建压");
            var result = operation.Result.Slot;
            if (result.MotorAnchorTimestamp != expectedAnchor ||
                result.MotorDeadlineTimestamp != expectedDeadline)
                throw new Exception($"正式单调相位窗口不正确: {result.MotorAnchorTimestamp}/{result.MotorDeadlineTimestamp}");
            utc = utc.AddHours(-2);
            var due = clock.AddMilliseconds(result.MotorAnchorTimestamp.Value, 1600);
            clock.DelayUntilAsync(due, CancellationToken.None).GetAwaiter().GetResult();
            if (now != expectedAnchor + 1600)
                throw new Exception("电气相位等待受 UTC 调整影响");
        }
    }
}
