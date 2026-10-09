using System;
using System.Diagnostics;
using System.IO;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class IndependentFallbackStatus
    {
        public int SchemaVersion { get; set; } = 1;
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string ExecutablePath { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string SessionId { get; set; }
        public bool Armed { get; set; }
        public int[] Channels { get; set; }
        public long UpdatedUtcTicks { get; set; }
        public WatchdogHeartbeat Heartbeat { get; set; }
    }

    public sealed class IndependentFallbackRequest
    {
        public int SchemaVersion { get; set; } = 1;
        public string Id { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public int GuardProcessId { get; set; }
        public long GuardStartUtcTicks { get; set; }
        public long RequestedUtcTicks { get; set; }
    }

    public sealed class IndependentFallbackReceipt
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public string RunId { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public bool Ready { get; set; }
        public string Nonce { get; set; }
        public string Detail { get; set; }
        public long UpdatedUtcTicks { get; set; }
    }

    public static class IndependentFallbackProtocol
    {
        public static string StatusPath(string directory) => Path.Combine(directory, "fallback-main-status.json");
        public static string RequestPath(string directory) => Path.Combine(directory, "fallback-main-request.json");
        public static string ReceiptPath(string directory) => Path.Combine(directory, "fallback-main-receipt.json");
        public static bool IsAlive(int pid, long ticks)
        {
            if (pid <= 0 || ticks <= 0) return false;
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == ticks; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        public static bool Matches(IndependentFallbackRequest request, IndependentFallbackStatus status, long now)
        {
            return request != null && status != null && request.SchemaVersion == 1 && status.SchemaVersion == 1 &&
                Guid.TryParseExact(request.Id, "N", out _) && status.Armed &&
                Guid.TryParse(status.RunId, out _) && status.RunEpoch > 0 && status.ProcessId > 0 &&
                status.ProcessStartUtcTicks > 0 && request.RunId == status.RunId && request.RunEpoch == status.RunEpoch &&
                request.ProcessId == status.ProcessId && request.ProcessStartUtcTicks == status.ProcessStartUtcTicks &&
                request.RequestedUtcTicks <= now && now - request.RequestedUtcTicks <= TimeSpan.FromSeconds(90).Ticks;
        }
        public static bool CanTerminate(IndependentFallbackRequest request, IndependentFallbackReceipt receipt,
            IndependentFallbackStatus status, long now)
        {
            return Matches(request, status, now) && receipt != null && receipt.SchemaVersion == 1 && receipt.Ready &&
                receipt.RequestId == request.Id && receipt.RunId == request.RunId &&
                receipt.ProcessId == request.ProcessId && receipt.ProcessStartUtcTicks == request.ProcessStartUtcTicks &&
                Guid.TryParseExact(receipt.Nonce, "N", out _) && receipt.UpdatedUtcTicks >= request.RequestedUtcTicks &&
                receipt.UpdatedUtcTicks <= now && now - receipt.UpdatedUtcTicks <= TimeSpan.FromSeconds(15).Ticks &&
                status.UpdatedUtcTicks <= now && now - status.UpdatedUtcTicks <= TimeSpan.FromSeconds(5).Ticks;
        }
    }
}
