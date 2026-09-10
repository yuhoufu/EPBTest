using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Controller;
using MTTFTest.SafetyHardware;
using MTTFTest.Watchdog;

namespace AdaptiveControlTests
{
    internal static class I0009IncidentTests
    {
        internal static int RunAll()
        {
            var count = 0;
            foreach (var channels in new[] { new[] { 4 }, new[] { 4, 5 }, new[] { 4, 5, 7, 8, 9, 12 } })
            {
                ScopeCompetition(channels);
                Console.WriteLine("PASS I0009 DAQ与启动恢复范围竞争 " + string.Join(",", channels));
                count++;
            }
            PhysicalConfiguration();
            Console.WriteLine("PASS I0009 真实双Record配置与缺失反馈拒绝");
            count++;
            var oldRun = Guid.NewGuid().ToString("N");
            var newRun = Guid.NewGuid().ToString("N");
            Check(WatchdogHost.TakeoverRunWasSuperseded(oldRun, newRun), "同进程新Run必须撤销旧接管");
            Check(!WatchdogHost.TakeoverRunWasSuperseded(oldRun, oldRun), "同Run不得误撤销");
            Check(!WatchdogHost.TakeoverRunWasSuperseded(oldRun, ""), "清场空Run不是新试验");
            Console.WriteLine("PASS I0009 重复开始与清场心跳运行身份");
            return count + 1;
        }

        private static void ScopeCompetition(int[] channels)
        {
            var registry = new RecoveryTaskRegistry();
            var states = new Dictionary<Guid, bool>();
            var gate = new object();
            var off = 0;
            var reserves = 0;
            var coordinator = new RecoveryIncidentCoordinator(new object(), new RecoveryIncidentCoordinator.Port
            {
                Reserve = c => { reserves++; return registry.Reserve(c); },
                Schedule = body => Task.Run(body),
                Bind = (lease, task) => lease.TryBind(task),
                PublishRecovering = c => { },
                Observe = (incident, task) => { },
                Register = incident => { lock (gate) states.Add(incident.Contract.IncidentId, false); },
                Unregister = incident => { lock (gate) states.Remove(incident.Contract.IncidentId); },
                IsRegistered = c => { lock (gate) return states.ContainsKey(c.IncidentId); },
                IsRecoveringPublished = c => { lock (gate) return states.ContainsKey(c.IncidentId) && !states[c.IncidentId]; },
                IsTerminalCommitted = c => { lock (gate) return states.TryGetValue(c.IncidentId, out var terminal) && terminal; },
                CommandOff = (c, r) => off++,
                PublishSafeTerminal = (c, r, d) => { lock (gate) states[c.IncidentId] = true; },
                Start = incident => incident.ReleaseStartSignal()
            });
            var run = Guid.NewGuid();
            var result = coordinator.TryBegin("DaqRecovery", run, 1,
                RecoveryOwnerKind.HydraulicGroupRecovery, RecoveryTargetPhase.Startup, Guid.NewGuid(), channels,
                _ => () => Task.CompletedTask, _ => { }, out var owner);
            Check(result == RecoveryIncidentCoordinator.BeginResult.Created, "必须先建立实际恢复所有权");
            result = coordinator.TryBegin("StartupPositioningSelfHealing", run, 1,
                RecoveryOwnerKind.BatchStartup, RecoveryTargetPhase.Startup, Guid.NewGuid(),
                new[] { channels[0] }, new[] { channels[0] }, _ => () => Task.CompletedTask, _ => { },
                out var rejected, reportScopeBusy: true);
            Check(result == RecoveryIncidentCoordinator.BeginResult.ScopeBusy && rejected == null && reserves == 1 && off == 0,
                "范围竞争不得登记新worker、断能或被报成登记故障");
            Check(owner.CompleteAfterTerminal(c => { lock (gate) states[c.IncidentId] = true; }), "旧恢复必须安全退休");
            RecoveryIncidentCoordinator.Incident retry = null;
            result = coordinator.TryBegin("StartupPositioningSelfHealing", run, 1,
                RecoveryOwnerKind.BatchStartup, RecoveryTargetPhase.Startup, Guid.NewGuid(),
                channels, _ => async () =>
                {
                    await Task.Yield();
                    Check(retry.CompleteAfterTerminal(c => { lock (gate) states[c.IncidentId] = true; }), "重试终态必须在worker内提交");
                }, _ => { }, out retry);
            Check(result == RecoveryIncidentCoordinator.BeginResult.Created && retry.Start(), "恢复退休后必须允许重新登记");
            retry.WorkerTask.GetAwaiter().GetResult();
            Check(off == 0, "正常终态不得触发失败兜底");
        }

        private static void PhysicalConfiguration()
        {
            var root = Path.Combine(Path.GetTempPath(), "I0009-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var test = "<TestConfig><EpbRecords><Record><Id>4</Id><Enabled>True</Enabled></Record>" +
                    "<Record><Id>5</Id><Enabled>False</Enabled></Record></EpbRecords>" +
                    "<EpbCycleRunnerConfig><Record><Channel>4</Channel><PeriodMs>15000</PeriodMs></Record></EpbCycleRunnerConfig></TestConfig>";
                File.WriteAllText(Path.Combine(root, "TestConfig.xml"), test);
                File.WriteAllText(Path.Combine(root, "AIConfig.xml"), "<Root><Records><参数名>EPB4_current</参数名>" +
                    "<是否启用>1</是否启用><物理通道>Dev1/ai0</物理通道><变换斜率>2</变换斜率>" +
                    "<变换截距>0</变换截距><零位漂移>0</零位漂移></Records></Root>");
                Check(SafetyPhysicalChannel.Load(root, Array.Empty<string>()).Single().IsCurrent, "周期Record不得污染通道解析");
                foreach (var invalid in new[] { test.Replace("True", "invalid"), test.Replace("<Id>4</Id>", "<Id>13</Id>"),
                    test.Replace("<Id>4</Id>", "<Id>6</Id>") })
                {
                    File.WriteAllText(Path.Combine(root, "TestConfig.xml"), invalid);
                    var blocked = false;
                    try { SafetyPhysicalChannel.Load(root, Array.Empty<string>()); }
                    catch (SafetyHardwareConfigurationException) { blocked = true; }
                    Check(blocked, "非法通道或缺失反馈必须保持门禁");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
    }
}
