using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CustomTcpClient;

namespace DataOperation
{
    public class TemperatureController : IDisposable
    {
        private readonly AsyncTcpClient _tempClient;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _pendingRequests;
        private bool _disposed;

        public TemperatureController(AsyncTcpClient tempClient)
        {
            _tempClient = tempClient ?? throw new ArgumentNullException(nameof(tempClient));
            _pendingRequests = new ConcurrentDictionary<Guid, TaskCompletionSource<string>>();
            _tempClient.DataReceived += TempClient_DataReceived;
        }

        // 设置目标温度（包含所有逻辑）
        public async Task SetTargetTempStart(int value, TimeSpan? timeout = null)
        {
            var requestId = Guid.NewGuid();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_pendingRequests.TryAdd(requestId, tcs))
            {
                throw new InvalidOperationException("无法创建温度控制请求");
            }

            try
            {
                // 构造命令
                string command = FormatSetTempCommand(value);

                // 构造完整命令（包含请求ID）
                byte[] requestIdBytes = requestId.ToByteArray();
                byte[] commandBytes = Encoding.ASCII.GetBytes(command);
                byte[] fullCommand = new byte[requestIdBytes.Length + commandBytes.Length];

                Buffer.BlockCopy(requestIdBytes, 0, fullCommand, 0, requestIdBytes.Length);
                Buffer.BlockCopy(commandBytes, 0, fullCommand, requestIdBytes.Length, commandBytes.Length);

                // 发送命令
                _tempClient.SendBinaryToServer("TempClient", fullCommand);

                // 设置超时（默认5秒）
                var timeoutValue = timeout ?? TimeSpan.FromSeconds(5);
                var delayTask = Task.Delay(timeoutValue);
                var completedTask = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);

                if (completedTask == delayTask)
                {
                    throw new TimeoutException($"等待响应超时 ({timeoutValue.TotalSeconds}秒)");
                }

                // 获取响应并验证
                string response = await tcs.Task;
                if (response != "0")
                {
                    throw new InvalidOperationException($"温度设置失败，收到响应: {response}");
                }
            }
            catch
            {
                // 确保取消任务
                tcs.TrySetCanceled();
                throw;
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        // 内联格式化方法
        private string FormatSetTempCommand(int value)
        {
            string sample = "$01E TTT 0 010000000000000000000000";
            return sample.Replace("TTT", value.ToString()) + Environment.NewLine;
        }

        // 数据接收处理
        private void TempClient_DataReceived(object sender, RecvEventArg e)
        {
            try
            {
                if (e.RecvBuffer == null || e.RecvBuffer.Length < 16) // GUID长度
                    return;

                // 提取请求ID (前16字节)
                byte[] guidBytes = new byte[16];
                Buffer.BlockCopy(e.RecvBuffer, 0, guidBytes, 0, 16);
                Guid requestId = new Guid(guidBytes);

                // 提取响应内容（剩余部分）
                string response = Encoding.ASCII.GetString(
                    e.RecvBuffer,
                    16,
                    e.RecvBuffer.Length - 16
                ).Trim();

                if (_pendingRequests.TryGetValue(requestId, out var tcs))
                {
                    // 直接返回原始响应字符串
                    tcs.TrySetResult(response);
                }
            }
            catch (Exception ex)
            {
                // 记录错误
                Debug.WriteLine($"处理温度响应时出错: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _tempClient.DataReceived -= TempClient_DataReceived;

            // 取消所有待处理请求
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetCanceled();
            }
            _pendingRequests.Clear();

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
    
