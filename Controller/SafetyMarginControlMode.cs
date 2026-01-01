using System;

namespace Controller
{
    public enum SafetyMarginControlMode
    {
        Legacy20251010 = 0,
        FreezeA20260101 = 1,
    }

    public static class SafetyMarginControlModeParser
    {
        public static SafetyMarginControlMode ParseOrDefault(
            string raw,
            SafetyMarginControlMode defaultMode = SafetyMarginControlMode.Legacy20251010)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return defaultMode;

            var s = raw.Trim();

            if (s.Equals("Legacy20251010", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Legacy", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("20251010", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("epb-cycle-sync-20251010", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("feature/epb-cycle-sync-20251010", StringComparison.OrdinalIgnoreCase))
                return SafetyMarginControlMode.Legacy20251010;

            if (s.Equals("FreezeA20260101", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("FreezeA", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("20260101", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("safety-margin-freezeA-20260101", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("feature/safety-margin-freezeA-20260101", StringComparison.OrdinalIgnoreCase))
                return SafetyMarginControlMode.FreezeA20260101;

            return defaultMode;
        }
    }
}
