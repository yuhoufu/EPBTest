using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PowerSupply.Core
{
    public sealed class PswEndpoint
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 2268;
        public string Terminator { get; set; } = "\\r\\n";

        public string ResolveTerminator()
        {
            return (Terminator ?? string.Empty)
                .Replace("\\r", "\r")
                .Replace("\\n", "\n");
        }

        public void Validate()
        {
            if (Id <= 0) throw new InvalidOperationException("电源 Id 必须大于 0。");
            if (string.IsNullOrWhiteSpace(Host)) throw new InvalidOperationException($"电源 {Id} 的 Host 不能为空。");
            if (Port <= 0 || Port > 65535) throw new InvalidOperationException($"电源 {Id} 的端口无效：{Port}。");
            var terminator = ResolveTerminator();
            if (terminator != "\r\n" && terminator != "\n")
                throw new InvalidOperationException($"电源 {Id} 只允许 CRLF 或 LF 终止符。");
        }

        public PswEndpoint Clone()
        {
            return new PswEndpoint
            {
                Id = Id,
                DisplayName = DisplayName,
                Host = Host,
                Port = Port,
                Terminator = Terminator
            };
        }
    }

    public sealed class PswCapabilities
    {
        public string Model { get; set; } = "Unknown";
        public double MaxVoltage { get; set; }
        public double MaxCurrent { get; set; }
        public double? MinOvp { get; set; }
        public double? MaxOvp { get; set; }
        public double? MinOcp { get; set; }
        public double? MaxOcp { get; set; }
        public bool CanWriteSetpoints => MaxVoltage > 0 && MaxCurrent > 0;
        public bool CanWriteOvp => MinOvp.HasValue && MaxOvp.HasValue;
        public bool CanWriteOcp => MinOcp.HasValue && MaxOcp.HasValue;

        public static PswCapabilities FromIdentity(string identity)
        {
            var normalized = (identity ?? string.Empty).ToUpperInvariant().Replace("-", " ");
            if (normalized.Contains("PSW 30 72") || normalized.Contains("PSW30 72"))
                return new PswCapabilities { Model = "PSW 30-72", MaxVoltage = 30, MaxCurrent = 72 };
            if (normalized.Contains("PSW 30 108") || normalized.Contains("PSW30 108"))
                return new PswCapabilities { Model = "PSW 30-108", MaxVoltage = 30, MaxCurrent = 108 };
            return new PswCapabilities();
        }

        public PswCapabilities WithProtectionRanges(
            double? minOvp, double? maxOvp, double? minOcp, double? maxOcp)
        {
            return new PswCapabilities
            {
                Model = Model,
                MaxVoltage = MaxVoltage,
                MaxCurrent = MaxCurrent,
                MinOvp = minOvp,
                MaxOvp = maxOvp,
                MinOcp = minOcp,
                MaxOcp = maxOcp
            };
        }
    }

    public sealed class PswSnapshot
    {
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public int SupplyId { get; set; }
        public bool IsConnected { get; set; }
        public string Identity { get; set; } = string.Empty;
        public bool IsVerifiedPsw { get; set; }
        public PswCapabilities Capabilities { get; set; } = new PswCapabilities();
        public bool OutputEnabled { get; set; }
        public double SetVoltage { get; set; }
        public double SetCurrent { get; set; }
        public double? Ovp { get; set; }
        public double? Ocp { get; set; }
        public double MeasuredVoltage { get; set; }
        public double MeasuredCurrent { get; set; }
        public double MeasuredPower { get; set; }
        public bool ProtectionTripped { get; set; }
        public int OperationStatus { get; set; }
        public int QuestionableStatus { get; set; }
        public string LastError { get; set; } = string.Empty;
        public bool IsConstantVoltage => (OperationStatus & 256) != 0;
        public bool IsConstantCurrent => (OperationStatus & 1024) != 0;
        public bool IsVoltageLimited => (QuestionableStatus & 256) != 0;
        public bool IsCurrentLimited => (QuestionableStatus & 512) != 0;
        public bool IsPowerLimited => (QuestionableStatus & 4096) != 0;
    }

    public enum PswLogDirection
    {
        Information,
        Transmit,
        Receive,
        Error
    }

    public sealed class PswLogEntry
    {
        public PswLogEntry(DateTime timestampUtc, int supplyId, PswLogDirection direction, string message)
        {
            TimestampUtc = timestampUtc;
            SupplyId = supplyId;
            Direction = direction;
            Message = message ?? string.Empty;
        }

        public DateTime TimestampUtc { get; }
        public int SupplyId { get; }
        public PswLogDirection Direction { get; }
        public string Message { get; }
    }

    public interface IPswLog
    {
        void Write(PswLogEntry entry);
    }

    public sealed class NullPswLog : IPswLog
    {
        public static readonly NullPswLog Instance = new NullPswLog();
        private NullPswLog() { }
        public void Write(PswLogEntry entry) { }
    }
}
