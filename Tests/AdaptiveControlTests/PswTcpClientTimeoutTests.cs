using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PowerSupply.Core;

namespace AdaptiveControlTests
{
    internal static class PswTcpClientTimeoutTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("程控电源查询超时不遗留未观察NetworkStream异常", TimeoutObservesDisposedReadTask, ref passed);
            Run("电源快照时间戳取轮询完成且保护设定低频读取", SnapshotTimestampUsesPollCompletion, ref passed);
            return passed;
        }

        private static void SnapshotTimestampUsesPollCompletion()
        {
            using (var server = new ScriptedScpiServer(180))
            {
                server.Start();
                using (var client = new PswTcpClient(
                           new PswEndpoint
                           {
                               Id = 1,
                               DisplayName = "ScriptedPsw",
                               Host = "127.0.0.1",
                               Port = server.Port,
                               Terminator = "\r\n"
                           },
                           commandTimeoutMs: 1000,
                           connectTimeoutMs: 1000))
                {
                    var snapshot = client.ConnectAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var returnedUtc = DateTime.UtcNow;
                    Assert(snapshot.PollDurationMs >= 150 &&
                           snapshot.PollStartedUtc <= snapshot.PollCompletedUtc,
                        $"快照未记录完整慢轮询耗时：{snapshot.PollDurationMs:F1}ms");
                    Assert(snapshot.TimestampUtc == snapshot.PollCompletedUtc &&
                           (returnedUtc - snapshot.TimestampUtc).TotalMilliseconds < 100,
                        "快照时间戳仍取自轮询中途而不是全部查询完成时刻");
                    Assert(server.OvpReadCount == 1 && server.OcpReadCount == 1,
                        "首次轮询没有读取OVP/OCP缓存");

                    var next = client.ReadSnapshotAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(next.TimestampUtc == next.PollCompletedUtc &&
                           server.OvpReadCount == 1 && server.OcpReadCount == 1,
                        "100ms常规轮询仍重复查询低频保护设定值");
                }
            }
        }

        private static void TimeoutObservesDisposedReadTask()
        {
            var unobservedNetworkStreamFaults = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
            {
                var flattened = args.Exception.Flatten();
                if (flattened.InnerExceptions.Any(ex =>
                        ex is ObjectDisposedException ||
                        ex.Message.IndexOf("NetworkStream", StringComparison.OrdinalIgnoreCase) >= 0))
                    Interlocked.Increment(ref unobservedNetworkStreamFaults);
                args.SetObserved();
            };
            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    using (var server = new SilentScpiServer())
                    {
                        server.Start();
                        var phaseLog = new RecordingPswLog();
                        var client = new PswTcpClient(
                            new PswEndpoint
                            {
                                Id = attempt + 1,
                                DisplayName = "SilentTestPsw",
                                Host = "127.0.0.1",
                                Port = server.Port,
                                Terminator = "\\r\\n"
                            },
                            phaseLog,
                            commandTimeoutMs: 100,
                            connectTimeoutMs: 1000);
                        try
                        {
                            AssertThrows<TimeoutException>(() =>
                                client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult());
                            Assert(server.WaitForCommand(1000), "模拟电源未收到*IDN?查询");
                            Assert(!client.IsConnected, "查询超时后仍错误报告连接有效");
                            Assert(phaseLog.Messages.Any(message =>
                                       message.Contains("PowerCommPhase Phase=ScpiResponseFirstByte") &&
                                       message.Contains("State=TimedOut") &&
                                       message.Contains("ElapsedMs=")),
                                "SCPI首字节超时未记录分阶段耗时与TimedOut终态");
                            Assert(phaseLog.Messages.Any(message =>
                                       message.Contains("PowerCommPhase Phase=ConnectSession") &&
                                       message.Contains("State=TimedOut")),
                                "连接会话未记录超时总耗时终态");
                        }
                        finally
                        {
                            client.Dispose();
                            server.Release();
                        }
                    }
                }

                // UnobservedTaskException 只在故障 Task 被回收时触发；强制终结器运行验证清理链。
                for (var i = 0; i < 4; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Thread.Sleep(50);
                }
                Assert(Volatile.Read(ref unobservedNetworkStreamFaults) == 0,
                    $"查询超时仍产生{unobservedNetworkStreamFaults}条未观察NetworkStream异常");
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= handler;
            }
        }

        private static void AssertThrows<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new InvalidOperationException($"预期异常 {typeof(T).Name} 未发生");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Run(string name, Action test, ref int passed)
        {
            test();
            passed++;
            Console.WriteLine($"PASS {name}");
        }

        private sealed class SilentScpiServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly ManualResetEventSlim _commandReceived = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            private Task _serverTask;

            internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            internal void Start()
            {
                _listener.Start();
                _serverTask = Task.Run(() =>
                {
                    try
                    {
                        using (var socket = _listener.AcceptTcpClient())
                        using (var stream = socket.GetStream())
                        using (var reader = new StreamReader(stream))
                        {
                            reader.ReadLine();
                            _commandReceived.Set();
                            _release.Wait(5000);
                        }
                    }
                    catch (ObjectDisposedException) { }
                    catch (SocketException) { }
                });
            }

            internal bool WaitForCommand(int timeoutMs) => _commandReceived.Wait(timeoutMs);

            internal void Release() => _release.Set();

            public void Dispose()
            {
                _release.Set();
                try { _listener.Stop(); } catch { }
                try { _serverTask?.Wait(2000); } catch { }
                _commandReceived.Dispose();
                _release.Dispose();
            }
        }

        private sealed class ScriptedScpiServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly int _ocpDelayMs;
            private Task _serverTask;
            private int _ovpReadCount;
            private int _ocpReadCount;
            private int _ocpDelayApplied;

            internal ScriptedScpiServer(int ocpDelayMs) => _ocpDelayMs = ocpDelayMs;
            internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            internal int OvpReadCount => Volatile.Read(ref _ovpReadCount);
            internal int OcpReadCount => Volatile.Read(ref _ocpReadCount);

            internal void Start()
            {
                _listener.Start();
                _serverTask = Task.Run(async () =>
                {
                    try
                    {
                        using (var socket = await _listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        using (var stream = socket.GetStream())
                        using (var reader = new StreamReader(stream))
                        using (var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" })
                        {
                            while (true)
                            {
                                var command = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (command == null) break;
                                var response = ResponseFor(command.Trim());
                                if (response == null) continue;
                                if (command.Trim().Equals("SOUR:CURR:PROT?", StringComparison.OrdinalIgnoreCase) &&
                                    Interlocked.Exchange(ref _ocpDelayApplied, 1) == 0)
                                    await Task.Delay(_ocpDelayMs).ConfigureAwait(false);
                                await writer.WriteLineAsync(response).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (ObjectDisposedException) { }
                    catch (SocketException) { }
                    catch (IOException) { }
                });
            }

            private string ResponseFor(string command)
            {
                switch (command.ToUpperInvariant())
                {
                    case "*IDN?": return "GW INSTEK,PSW 30-72,SN,1.0";
                    case "SOUR:VOLT:PROT? MIN": return "1";
                    case "SOUR:VOLT:PROT? MAX": return "33";
                    case "SOUR:CURR:PROT? MIN": return "1";
                    case "SOUR:CURR:PROT? MAX": return "79";
                    case "OUTP?": return "0";
                    case "SOUR:VOLT?": return "24";
                    case "SOUR:CURR?": return "20";
                    case "MEAS:ALL:DC?": return "0,0,0";
                    case "STAT:OPER:COND?": return "256";
                    case "STAT:QUES:COND?": return "0";
                    case "OUTP:PROT:TRIP?": return "0";
                    case "SOUR:VOLT:PROT?":
                        Interlocked.Increment(ref _ovpReadCount);
                        return "27";
                    case "SOUR:CURR:PROT?":
                        Interlocked.Increment(ref _ocpReadCount);
                        return "22";
                    default: throw new InvalidDataException("Unexpected SCPI command: " + command);
                }
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
                try { _serverTask?.Wait(2000); } catch { }
            }
        }

        private sealed class RecordingPswLog : IPswLog
        {
            private readonly ConcurrentQueue<string> _messages =
                new ConcurrentQueue<string>();

            internal string[] Messages => _messages.ToArray();

            public void Write(PswLogEntry entry)
            {
                if (entry != null) _messages.Enqueue(entry.Message);
            }
        }
    }
}
