using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;
using Config;
using Controller.Alarm;

namespace AdaptiveControlTests
{
    public static class InstallationAlarmTests
    {
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        public static int RunAll()
        {
            PreserveEvidenceAndSuppressAcrossRestart();
            CompareExchangeAndPersistenceFailure();
            LatestDemandAfterOutputFailure();
            SafeCloseWaitsForInFlightOutput();
            ActualPipeIdentityRejectsSpoofing();
            ProtocolRejectsUnboundedAndInvalidRequests();
            FailedSafeCloseRestoresOrdinaryAlarms();
            InvalidAllOffCannotClaimCommandSent();
            SuppressedP0DisplayIncludesCurrentProjectDemand();
            Console.WriteLine("PASS 安装报警 9/9（未访问物理串口）");
            return 9;
        }

        private static Type OwnerType
        {
            get
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(loaded =>
                    !loaded.IsDynamic && Path.GetFileName(loaded.Location) == "MTTFTest.Watchdog.exe") ??
                    Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MTTFTest.Watchdog.exe"));
                return assembly.GetTypes().Single(type => type.Name == "SupervisorP0AlarmHardwareOwner");
            }
        }

        private static string DirectoryForTest()
        {
            var path = Path.Combine(Environment.CurrentDirectory, "Codex", "alarm-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static object Owner(string path, Action<bool, bool, bool[], bool> output = null)
        {
            return Activator.CreateInstance(OwnerType, PrivateInstance, null,
                new object[] { path, path, output ?? ((latched, muted, lights, buzzer) => { }) }, null);
        }

        private static void Latch(object owner, string id)
        {
            Call(owner, "Apply", new SupervisorP0AlarmRequest { Action = SupervisorP0AlarmAction.Latch,
                EventId = id, Code = "SafetyAgentProcessFailure", Detail = "original failure" }, "S-1-5-18");
        }

        private static object Call(object owner, string method, params object[] arguments)
        {
            try { return owner.GetType().GetMethod(method, PrivateInstance).Invoke(owner, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }

        private static InstallationAlarmRequest Request(InstallationAlarmAction action, InstallationAlarmSnapshot expected = null)
        {
            using (var process = Process.GetCurrentProcess())
                return new InstallationAlarmRequest { RequestId = Guid.NewGuid().ToString("N"), Action = action,
                    ProcessId = process.Id, ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    EventId = expected?.EventId, ExpectedRevision = expected?.Revision ?? 0 };
        }

        private static InstallationAlarmSnapshot Apply(object owner, InstallationAlarmAction action, InstallationAlarmSnapshot expected = null)
        {
            return (InstallationAlarmSnapshot)Call(owner, "ApplyOperatorRequest", Request(action, expected), "S-1-5-21-operator");
        }

        private static void PreserveEvidenceAndSuppressAcrossRestart()
        {
            var path = DirectoryForTest();
            object owner = null;
            try
            {
                owner = Owner(path);
                var id = Guid.NewGuid().ToString("N");
                Latch(owner, id);
                var original = Apply(owner, InstallationAlarmAction.Query);
                var muted = Apply(owner, InstallationAlarmAction.MuteBuzzer, original);
                Assert(muted.Latched && muted.BuzzerMuted && !muted.OutputsSuppressed && muted.EventId == id &&
                    muted.Code == original.Code && muted.Detail == original.Detail &&
                    muted.FirstObservedUtcTicks == original.FirstObservedUtcTicks && muted.LastMuteOperatorSid == "S-1-5-21-operator",
                    "仅静音必须保存原故障、首次观测和操作员证据");
                Latch(owner, id);
                Assert(Apply(owner, InstallationAlarmAction.Query).Revision == muted.Revision, "同一P0重送不应取消静音或刷新故障时间");
                var closed = Apply(owner, InstallationAlarmAction.CompleteSafeClose, muted);
                Assert(closed.Latched && closed.OutputsSuppressed && closed.EventId == id, "正常关闭必须关闭声光但保留锁存");
                ((IDisposable)owner).Dispose();
                owner = Owner(path);
                var restored = Apply(owner, InstallationAlarmAction.Query);
                Assert(restored.OutputsSuppressed && restored.LastOutputAction == "CompleteSafeClose" && restored.Revision == closed.Revision,
                    "Supervisor重启丢失持久声光关闭状态");
                Latch(owner, id);
                Assert(Apply(owner, InstallationAlarmAction.Query).OutputsSuppressed, "旧事件重放取消了声光关闭");
                Call(owner, "Apply", new SupervisorP0AlarmRequest { Action = SupervisorP0AlarmAction.ClearTransient }, "S-1-5-18");
                Assert(Apply(owner, InstallationAlarmAction.Query).Latched, "新项目首圈不具备解除历史P0的证据");
                var nextId = Guid.NewGuid().ToString("N");
                Latch(owner, nextId);
                var next = Apply(owner, InstallationAlarmAction.Query);
                Assert(next.Latched && !next.OutputsSuppressed && !next.BuzzerMuted && next.EventId == nextId,
                    "新的P0必须重新请求声光输出");
                var on = Request(InstallationAlarmAction.SetIndicator); on.Channel = 1; on.On = true;
                Call(owner, "ApplyOperatorRequest", on, "S-1-5-21-operator");
                Apply(owner, InstallationAlarmAction.ReleaseProjectOutputs);
                Assert(!Apply(owner, InstallationAlarmAction.Query).OutputsSuppressed, "普通Dispose不能冒充安全关闭");
            }
            finally { (owner as IDisposable)?.Dispose(); Directory.Delete(path, true); }
        }

        private static void CompareExchangeAndPersistenceFailure()
        {
            var path = DirectoryForTest();
            object owner = null;
            try
            {
                owner = Owner(path);
                Latch(owner, Guid.NewGuid().ToString("N"));
                var old = Apply(owner, InstallationAlarmAction.Query);
                Latch(owner, Guid.NewGuid().ToString("N"));
                ExpectFailure(() => Apply(owner, InstallationAlarmAction.SuppressOutputs, old), "报警已变化");
                var current = Apply(owner, InstallationAlarmAction.Query);
                var file = Path.Combine(path, "p0-alarm-state.v5.json");
                using (var blocker = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ExpectFailure(() => Apply(owner, InstallationAlarmAction.MuteBuzzer, current), null);
                var after = Apply(owner, InstallationAlarmAction.Query);
                Assert(!after.BuzzerMuted && !after.OutputsSuppressed && after.Revision == current.Revision,
                    "持久写失败发布了幽灵静音/关闭状态");
                ((IDisposable)owner).Dispose(); owner = Owner(path);
                Assert(!Apply(owner, InstallationAlarmAction.Query).BuzzerMuted, "写失败后的重启不应读到成功静音");
            }
            finally { (owner as IDisposable)?.Dispose(); Directory.Delete(path, true); }
        }

        private static void LatestDemandAfterOutputFailure()
        {
            var path = DirectoryForTest();
            object owner = null;
            var fail = 1;
            var observedAllOff = 0;
            try
            {
                owner = Owner(path, (latched, muted, lights, buzzer) =>
                {
                    if (Volatile.Read(ref fail) != 0) throw new IOException("simulated serial port busy");
                    if (!latched && !buzzer && !lights.Any(value => value)) Interlocked.Exchange(ref observedAllOff, 1);
                });
                Latch(owner, Guid.NewGuid().ToString("N"));
                Assert(SpinWait.SpinUntil(() => Apply(owner, InstallationAlarmAction.Query).OutputStatus == "RetryPending", 3000),
                    "串口故障应显示RetryPending");
                var failed = Apply(owner, InstallationAlarmAction.Query);
                Assert(failed.CommandSentRevision != failed.Revision && failed.OutputError.Contains("serial port busy"),
                    "串口失败被错误报告为命令已发送");
                Interlocked.Exchange(ref fail, 0);
                var closed = Apply(owner, InstallationAlarmAction.CompleteSafeClose, failed);
                Assert(SpinWait.SpinUntil(() => Volatile.Read(ref observedAllOff) != 0 &&
                    Apply(owner, InstallationAlarmAction.Query).OutputStatus == "CommandSent", 3000),
                    "串口恢复后没有应用最新声光全关需求");
                Assert(Apply(owner, InstallationAlarmAction.Query).CommandSentRevision == closed.Revision,
                    "最新输出版本与发送回执不一致");
            }
            finally { (owner as IDisposable)?.Dispose(); Directory.Delete(path, true); }
        }

        private static void ActualPipeIdentityRejectsSpoofing()
        {
            var runtimeType = OwnerType.DeclaringType;
            var runtime = Activator.CreateInstance(runtimeType, true);
            var validate = runtimeType.GetMethod("ValidateAlarmPipeIdentity", PrivateInstance);
            var actorPolicy = runtimeType.GetMethod("AlarmOperatorIdentityAllowed", BindingFlags.Static | BindingFlags.NonPublic);
            Assert((bool)actorPolicy.Invoke(null, new object[] { false, true, "S-1-5-18", "operator" }),
                "精确归属的SYSTEM侧车应可请求恢复窗静音");
            Assert(!(bool)actorPolicy.Invoke(null, new object[] { false, false, "S-1-5-18", "operator" }) &&
                !(bool)actorPolicy.Invoke(null, new object[] { true, false, "S-1-5-18", "operator" }),
                "任意SYSTEM进程或错误操作员不能取得仅静音权限");
            using (var process = Process.GetCurrentProcess())
            {
                var pid = process.Id;
                var start = process.StartTime.ToUniversalTime().Ticks;
                Action<int, long, bool> check = (claim, ticks, operatorRequest) =>
                {
                    var name = "MTTFTest-alarm-test-" + Guid.NewGuid().ToString("N");
                    using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                    {
                        var receive = Task.Run(() =>
                        {
                            server.WaitForConnection(); server.ReadByte();
                            try { validate.Invoke(runtime, new object[] { server, claim, ticks, operatorRequest, false }); }
                            catch (TargetInvocationException error) { throw error.InnerException; }
                        });
                        client.Connect(1000); client.WriteByte(1); client.Flush();
                        if (!SpinWait.SpinUntil(() => receive.IsCompleted, 3000)) throw new TimeoutException("身份测试管道超时");
                        receive.GetAwaiter().GetResult();
                    }
                };
                check(pid, start, false);
                ExpectFailure(() => check(pid + 1, start, false), "AlarmPipeClientIdentityMismatch");
                ExpectFailure(() => check(pid, start + 1, false), "AlarmPipeProcessIdentityMismatch");
                ExpectFailure(() => check(pid, start, true), null);
                var pipeName = "MTTFTest-alarm-protocol-test-" + Guid.NewGuid().ToString("N");
                using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                {
                    var serve = Task.Run(() =>
                    {
                        server.WaitForConnection();
                        runtimeType.GetMethod("HandleConnection", PrivateInstance).Invoke(runtime, new object[] { server });
                    });
                    client.Connect(1000);
                    var spoofed = Request(InstallationAlarmAction.Query); spoofed.ProcessId = pid + 1;
                    var response = DeadlinePipeExchange.OnConnected(client, 3000, (reader, writer) =>
                    {
                        InstallationAlarmProtocol.Write(writer, InstallationAlarmProtocol.RequestMagic, spoofed);
                        Assert(SupervisorProtocol.ReadRequestMagic(reader) == InstallationAlarmProtocol.ResponseMagic, "身份拒绝响应magic错误");
                        return InstallationAlarmProtocol.ReadBody<InstallationAlarmResponse>(reader);
                    }, CancellationToken.None);
                    Assert(!response.Accepted && response.Error == "AlarmPipeClientIdentityMismatch", "真实IPC接受了伪造PID");
                    Assert(serve.Wait(3000), "测试IPC未收尾");
                }
            }
        }

        private static void SafeCloseWaitsForInFlightOutput()
        {
            var path = DirectoryForTest();
            object owner = null;
            using (var oldOnStarted = new ManualResetEventSlim())
            using (var releaseOldOn = new ManualResetEventSlim())
            using (var deadline = new CancellationTokenSource(5000))
            {
                try
                {
                    owner = Owner(path, (latched, muted, lights, buzzer) =>
                    {
                        if (latched)
                        {
                            oldOnStarted.Set();
                            if (!releaseOldOn.Wait(4000)) throw new TimeoutException("test old ON blocked");
                        }
                    });
                    Latch(owner, Guid.NewGuid().ToString("N"));
                    Assert(oldOnStarted.Wait(3000), "旧ON没有进入模拟输出");
                    var close = Apply(owner, InstallationAlarmAction.CompleteSafeClose, Apply(owner, InstallationAlarmAction.Query));
                    var pending = InstallationAlarmClient.WaitForCommandSentAsync(close,
                        () => Apply(owner, InstallationAlarmAction.Query), deadline.Token);
                    Assert(!pending.IsCompleted && close.OutputStatus != "CommandSent", "在途旧ON被误当最新全关确认");
                    releaseOldOn.Set();
                    var confirmed = pending.GetAwaiter().GetResult();
                    Assert(confirmed.CommandSentRevision == close.Revision && confirmed.OutputsSuppressed,
                        "正常关闭没有等待同事件同版本的全关命令");
                    Latch(owner, Guid.NewGuid().ToString("N"));
                    ExpectFailure(() => InstallationAlarmClient.WaitForCommandSentAsync(close,
                        () => Apply(owner, InstallationAlarmAction.Query), deadline.Token).GetAwaiter().GetResult(), "报警已变化");
                    var broken = Apply(owner, InstallationAlarmAction.CompleteSafeClose, Apply(owner, InstallationAlarmAction.Query));
                    broken.OutputStatus = "RetryPending"; broken.CommandSentRevision = 0;
                    using (var timeout = new CancellationTokenSource(100))
                        ExpectFailure(() => InstallationAlarmClient.WaitForCommandSentAsync(broken, () => broken, timeout.Token)
                            .GetAwaiter().GetResult(), null);
                }
                finally
                {
                    releaseOldOn.Set();
                    (owner as IDisposable)?.Dispose();
                    Directory.Delete(path, true);
                }
            }
        }

        private static void ProtocolRejectsUnboundedAndInvalidRequests()
        {
            var invalid = Request(InstallationAlarmAction.MuteBuzzer);
            ExpectFailure(invalid.Validate, "InstallationAlarmRequestInvalid");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            using (var reader = new BinaryReader(stream))
            {
                writer.Write(65537); writer.Flush(); stream.Position = 0;
                ExpectFailure(() => InstallationAlarmProtocol.ReadBody<InstallationAlarmRequest>(reader), "InstallationAlarmFrameTooLarge");
            }
        }

        private static void FailedSafeCloseRestoresOrdinaryAlarms()
        {
            var config = new AlarmConfig();
            config.Behavior.BuzzerDebounceMs = 0;
            config.Mappings.Epb.Add(new AlarmEpbMapping { Channel = 1, DeviceId = 1, Line = 0 });
            config.Mappings.Buzzer = new AlarmBuzzerMapping { DeviceId = 1, Line = 1 };
            var onRequests = 0;
            var closeRequests = 0;
            using (var manager = new AlarmManager(config, null, (action, channel, on, token) =>
            {
                if (action == InstallationAlarmAction.CompleteSafeClose)
                {
                    Interlocked.Increment(ref closeRequests);
                    return Task.FromException<InstallationAlarmSnapshot>(new IOException("simulated close output failure"));
                }
                if (action == InstallationAlarmAction.SetIndicator && on) Interlocked.Increment(ref onRequests);
                return Task.FromResult(new InstallationAlarmSnapshot());
            }))
            {
                manager.SetAlarmAsync(1, true).GetAwaiter().GetResult();
                var before = Volatile.Read(ref onRequests);
                ExpectFailure(() => manager.CompleteSafeCloseAsync().GetAwaiter().GetResult(), "simulated close output failure");
                Assert(SpinWait.SpinUntil(() => Volatile.Read(ref onRequests) > before, 3000), "关闭失败后没有恢复当前项目报警需求");
                before = Volatile.Read(ref onRequests);
                manager.SetIndicatorOutputAsync(1, true).GetAwaiter().GetResult();
                Assert(Volatile.Read(ref onRequests) > before, "关闭失败导致后续普通报警被永久抑制");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    ExpectFailure(() => manager.CompleteSafeCloseAsync(cancelled.Token).GetAwaiter().GetResult(), null);
                }
                before = Volatile.Read(ref onRequests);
                manager.SetIndicatorOutputAsync(1, true).GetAwaiter().GetResult();
                Assert(Volatile.Read(ref onRequests) > before && Volatile.Read(ref closeRequests) == 1,
                    "等待关锁取消后未恢复报警，或自动重试了持久关闭请求");
            }
        }

        private static void InvalidAllOffCannotClaimCommandSent()
        {
            var path = DirectoryForTest();
            try
            {
                var config = Path.Combine(path, "AlarmConfig.xml");
                File.WriteAllText(config, "<AlarmConfig><Serial Port='unused-test-port'/><Mappings>" +
                    "<Epb Channel='1' DeviceId='1' Line='0'/><Buzzer DeviceId='1' Line='1'/></Mappings>" +
                    "<Commands><AllOff/><SingleCoil><Cmd DeviceId='1' Line='0' OnHex='01' OffHex='00'/>" +
                    "<Cmd DeviceId='1' Line='1' OnHex='01' OffHex='00'/></SingleCoil></Commands></AlarmConfig>");
                var type = OwnerType.DeclaringType.GetNestedType("P0AlarmHardwareConfiguration", BindingFlags.NonPublic);
                ExpectFailure(() => type.GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { config }), "AlarmAllOffCommandsMissing");
            }
            finally { Directory.Delete(path, true); }
        }

        private static void ExpectFailure(Action action, string text)
        {
            try { action(); }
            catch (Exception error)
            {
                if (text == null || error.GetBaseException().Message.Contains(text)) return;
                throw;
            }
            throw new InvalidOperationException("预期拒绝但操作成功：" + text);
        }

        private static void SuppressedP0DisplayIncludesCurrentProjectDemand()
        {
            var render = typeof(MtEmbTest.Main_Frm).GetMethod("AlarmStateText", BindingFlags.Static | BindingFlags.NonPublic);
            var state = new InstallationAlarmSnapshot { Latched = true, OutputsSuppressed = true,
                Revision = 17, CommandSentRevision = 17, OutputStatus = "CommandSent" };
            foreach (var lights in new[] { 0, 2 })
            foreach (var buzzer in new[] { false, true })
            {
                state.RequestedLightCount = lights;
                state.RequestedBuzzerOn = buzzer;
                var text = (string)render.Invoke(null, new object[] { state });
                if (lights > 0 || buzzer)
                    Assert(text.Contains("旧事件输出已关闭") && text.Contains("当前项目有报警输出需求") &&
                        !text.Contains("声光关闭命令已发送"), "P0抑制后项目有ON需求，界面却显示声光全关");
                else Assert(text.Contains("声光关闭命令已发送"), "全关且无项目输出需求时状态显示错误");
                Assert(text.Contains("故障仍锁存"), "显示项目输出需求时丢失P0锁存提示");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
