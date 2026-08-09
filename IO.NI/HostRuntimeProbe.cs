using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace IO.NI
{
    /// <summary>
    /// 低频主机运行快照。只允许从后台诊断路径调用，不进入 DAQ 回调热路径。
    /// </summary>
    internal struct HostRuntimeSnapshot
    {
        public int ProcessId;
        public int ProcessBitness;
        public double ProcessCpuPercent;
        public double SystemCpuPercent;
        public double OtherCpuPercent;
        public long WorkingSetBytes;
        public long PrivateMemoryBytes;
        public long VirtualMemoryBytes;
        public int HandleCount;
        public int ThreadCount;
        public ulong SystemAvailableMemoryBytes;
        public long ProgramDriveFreeBytes;
        public double DiskQueueLength;
        public double DiskReadBytesPerSecond;
        public double DiskWriteBytesPerSecond;
    }

    internal static class HostRuntimeProbe
    {
        private static readonly object Gate = new object();
        private static long _lastCaptureTimestamp;
        private static long _lastProcessCpuTimestamp;
        private static long _lastProcessCpuTicks;
        private static ulong _lastSystemIdle;
        private static ulong _lastSystemKernel;
        private static ulong _lastSystemUser;
        private static HostRuntimeSnapshot _cached;
        private static PerformanceCounter _diskQueueCounter;
        private static PerformanceCounter _diskReadCounter;
        private static PerformanceCounter _diskWriteCounter;
        private static bool _diskCountersUnavailable;

        public static HostRuntimeSnapshot Capture()
        {
            lock (Gate)
            {
                var now = Stopwatch.GetTimestamp();
                // 两块设备各自约 1 Hz 请求探针；全局缓存防止相位错开时把昂贵的
                // Process/PerformanceCounter 采样放大为约 2 Hz。
                if (_lastCaptureTimestamp != 0 &&
                    (now - _lastCaptureTimestamp) * 1000.0 / Stopwatch.Frequency < 900)
                    return _cached;

                using (var process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    var processCpuTicks = process.TotalProcessorTime.Ticks;
                    var processCpuTimestamp = Stopwatch.GetTimestamp();
                    var processCpu = 0.0;
                    if (_lastProcessCpuTimestamp != 0 &&
                        processCpuTimestamp > _lastProcessCpuTimestamp)
                    {
                        // CPU 时间差必须使用与 TotalProcessorTime 同一采样阶段的时钟锚点。
                        // 缓存锚点在昂贵探针全部完成后更新；二者混用会把本次探针阻塞
                        // 从 elapsed 中扣掉，导致 ProcessCpuPercent 虚高并被夹到100%。
                        var elapsedSeconds = (processCpuTimestamp - _lastProcessCpuTimestamp) /
                                             (double)Stopwatch.Frequency;
                        var cpuSeconds = (processCpuTicks - _lastProcessCpuTicks) / (double)TimeSpan.TicksPerSecond;
                        processCpu = ClampPercent(
                            cpuSeconds * 100.0 / (elapsedSeconds * Math.Max(1, Environment.ProcessorCount)));
                    }

                    var snapshot = new HostRuntimeSnapshot
                    {
                        ProcessId = process.Id,
                        ProcessBitness = Environment.Is64BitProcess ? 64 : 32,
                        ProcessCpuPercent = processCpu,
                        WorkingSetBytes = process.WorkingSet64,
                        PrivateMemoryBytes = process.PrivateMemorySize64,
                        VirtualMemoryBytes = process.VirtualMemorySize64,
                        HandleCount = SafeHandleCount(process),
                        ThreadCount = SafeThreadCount(process),
                        ProgramDriveFreeBytes = GetProgramDriveFreeBytes()
                    };

                    if (TryReadSystemTimes(out var idle, out var kernel, out var user))
                    {
                        if (_lastCaptureTimestamp != 0)
                        {
                            var idleDelta = idle - _lastSystemIdle;
                            var totalDelta = kernel - _lastSystemKernel + user - _lastSystemUser;
                            if (totalDelta > 0 && totalDelta >= idleDelta)
                                snapshot.SystemCpuPercent = ClampPercent(
                                    (totalDelta - idleDelta) * 100.0 / totalDelta);
                        }
                        _lastSystemIdle = idle;
                        _lastSystemKernel = kernel;
                        _lastSystemUser = user;
                    }

                    snapshot.OtherCpuPercent = Math.Max(
                        0,
                        snapshot.SystemCpuPercent - snapshot.ProcessCpuPercent);
                    snapshot.SystemAvailableMemoryBytes = GetSystemAvailableMemoryBytes();
                    ReadDiskCounters(
                        out snapshot.DiskQueueLength,
                        out snapshot.DiskReadBytesPerSecond,
                        out snapshot.DiskWriteBytesPerSecond);
                    // 以昂贵探针全部返回后的时刻作为缓存锚点。旧实现保存调用前时刻，
                    // 探针一旦阻塞超过900ms，返回后会立即再采一次，形成级联放大。
                    _lastCaptureTimestamp = Stopwatch.GetTimestamp();
                    _lastProcessCpuTimestamp = processCpuTimestamp;
                    _lastProcessCpuTicks = processCpuTicks;
                    _cached = snapshot;
                    return snapshot;
                }
            }
        }

        private static int SafeHandleCount(Process process)
        {
            try { return process.HandleCount; }
            catch { return -1; }
        }

        private static int SafeThreadCount(Process process)
        {
            try { return process.Threads.Count; }
            catch { return -1; }
        }

        private static long GetProgramDriveFreeBytes()
        {
            try
            {
                var root = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
                return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return -1; }
        }

        private static ulong GetSystemAvailableMemoryBytes()
        {
            try
            {
                var status = new MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                return GlobalMemoryStatusEx(ref status) ? status.AvailablePhysical : 0;
            }
            catch { return 0; }
        }

        private static void ReadDiskCounters(
            out double queueLength,
            out double readBytesPerSecond,
            out double writeBytesPerSecond)
        {
            queueLength = readBytesPerSecond = writeBytesPerSecond = double.NaN;
            if (_diskCountersUnavailable) return;
            try
            {
                _diskQueueCounter ??= new PerformanceCounter(
                    "PhysicalDisk", "Current Disk Queue Length", "_Total", true);
                _diskReadCounter ??= new PerformanceCounter(
                    "PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                _diskWriteCounter ??= new PerformanceCounter(
                    "PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                queueLength = Math.Max(0, _diskQueueCounter.NextValue());
                readBytesPerSecond = Math.Max(0, _diskReadCounter.NextValue());
                writeBytesPerSecond = Math.Max(0, _diskWriteCounter.NextValue());
            }
            catch
            {
                _diskCountersUnavailable = true;
            }
        }

        private static bool TryReadSystemTimes(out ulong idle, out ulong kernel, out ulong user)
        {
            idle = kernel = user = 0;
            try
            {
                if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
                    return false;
                idle = idleTime.Value;
                kernel = kernelTime.Value;
                user = userTime.Value;
                return true;
            }
            catch { return false; }
        }

        private static double ClampPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return 0;
            return value > 100 ? 100 : value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
            public ulong Value => ((ulong)High << 32) | Low;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(
            out FileTime idleTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    }
}
