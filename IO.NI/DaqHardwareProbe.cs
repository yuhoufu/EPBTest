using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Config;
using NationalInstruments.DAQmx;
using Task = System.Threading.Tasks.Task;

namespace IO.NI
{
    public sealed class DaqHardwareProbeResult
    {
        public string Device { get; set; } = string.Empty;
        public bool EnumerationSucceeded { get; set; }
        public bool DevicePresent { get; set; }
        public bool SelfTestAttempted { get; set; }
        public bool SelfTestSucceeded { get; set; }
        public string Error { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }

        public bool IndependentFailureConfirmed =>
            EnumerationSucceeded &&
            (!DevicePresent || (SelfTestAttempted && !SelfTestSucceeded));

        public HardwareEvidence ToEvidence()
        {
            return new HardwareEvidence
            {
                Source = "NI-DAQmx",
                Code = !DevicePresent ? "DeviceNotEnumerated" :
                    !SelfTestAttempted ? "DeviceSelfTestUnavailable" :
                    !SelfTestSucceeded ? "DeviceSelfTestFailed" : "DeviceProbePassed",
                Detail = Error,
                TimestampUtc = TimestampUtc,
                Confirmed = IndependentFailureConfirmed
            };
        }
    }

    public interface IDaqHardwareProbe
    {
        Task<DaqHardwareProbeResult> ProbeAsync(string device, CancellationToken token);
    }

    /// <summary>
    /// 使用当前机器已安装的 NI-DAQmx .NET API 做独立枚举和自检。
    /// 反射只用于兼容现场不同 DAQmx .NET 版本暴露的设备集合/加载方法差异。
    /// </summary>
    public sealed class NIDaqHardwareProbe : IDaqHardwareProbe
    {
        public Task<DaqHardwareProbeResult> ProbeAsync(string device, CancellationToken token)
        {
            return Task.Run(() => Probe(device, token), token);
        }

        private static DaqHardwareProbeResult Probe(string device, CancellationToken token)
        {
            var result = new DaqHardwareProbeResult
            {
                Device = device ?? string.Empty,
                TimestampUtc = DateTime.UtcNow
            };
            try
            {
                token.ThrowIfCancellationRequested();
                var local = DaqSystem.Local;
                var names = ReadDeviceNames(local).ToArray();
                result.EnumerationSucceeded = true;
                result.DevicePresent = names.Any(name =>
                    string.Equals(name, device, StringComparison.OrdinalIgnoreCase));
                if (!result.DevicePresent)
                {
                    result.Error = $"DAQmx枚举未发现 {device}；当前=[{string.Join(",", names)}]";
                    return result;
                }

                token.ThrowIfCancellationRequested();
                var loaded = LoadDevice(local, device);
                if (loaded == null)
                    throw new MissingMethodException("当前DAQmx .NET版本未暴露设备加载接口。");
                var selfTest = loaded.GetType().GetMethod(
                    "SelfTest",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (selfTest == null)
                    throw new MissingMethodException("当前DAQmx .NET版本未暴露Device.SelfTest。");
                result.SelfTestAttempted = true;
                selfTest.Invoke(loaded, null);
                result.SelfTestSucceeded = true;
                result.Error = "设备枚举和自检通过。";
            }
            catch (TargetInvocationException ex)
            {
                result.Error = (ex.InnerException ?? ex).Message;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        private static IEnumerable<string> ReadDeviceNames(DaqSystem local)
        {
            var property = local.GetType().GetProperty("Devices", BindingFlags.Instance | BindingFlags.Public);
            var value = property?.GetValue(local, null);
            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    if (item is string name) yield return name;
                    else
                    {
                        var nameProperty = item.GetType().GetProperty("Name");
                        yield return nameProperty?.GetValue(item, null)?.ToString() ?? item.ToString();
                    }
                }
            }
        }

        private static object LoadDevice(DaqSystem local, string device)
        {
            foreach (var methodName in new[] { "LoadDevice", "GetDevice" })
            {
                var method = local.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(string) },
                    null);
                if (method != null) return method.Invoke(local, new object[] { device });
            }
            var devices = local.GetType().GetProperty("Devices")?.GetValue(local, null) as IEnumerable;
            if (devices == null) return null;
            foreach (var item in devices)
            {
                if (item == null || item is string) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item, null)?.ToString();
                if (string.Equals(name, device, StringComparison.OrdinalIgnoreCase)) return item;
            }
            return null;
        }
    }
}
