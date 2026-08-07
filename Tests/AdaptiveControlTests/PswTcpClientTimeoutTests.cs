using System;
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
            return passed;
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
                        var client = new PswTcpClient(
                            new PswEndpoint
                            {
                                Id = attempt + 1,
                                DisplayName = "SilentTestPsw",
                                Host = "127.0.0.1",
                                Port = server.Port,
                                Terminator = "\\r\\n"
                            },
                            commandTimeoutMs: 100,
                            connectTimeoutMs: 1000);
                        try
                        {
                            AssertThrows<TimeoutException>(() =>
                                client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult());
                            Assert(server.WaitForCommand(1000), "模拟电源未收到*IDN?查询");
                            Assert(!client.IsConnected, "查询超时后仍错误报告连接有效");
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
    }
}
