using System;

namespace Controller
{
    /// <summary>
    /// Allocation-free policy helpers for the bounded on-screen log. The durable project log is
    /// independent; these rules only limit RichTextBox work on the UI thread.
    /// </summary>
    public static class UiLogDisplayPolicy
    {
        public static int CalculateLinesToRemove(int actualLineCount, int maximumLines, int retainedLines)
        {
            if (maximumLines <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumLines));
            if (retainedLines < 0 || retainedLines >= maximumLines)
                throw new ArgumentOutOfRangeException(nameof(retainedLines));
            return actualLineCount > maximumLines
                ? Math.Max(0, actualLineCount - retainedLines)
                : 0;
        }

        public static bool ShouldAutoScroll(
            long nowTick,
            long lastScrollTick,
            long stopwatchFrequency,
            int minimumIntervalMs)
        {
            if (nowTick <= 0 || stopwatchFrequency <= 0 || minimumIntervalMs < 0)
                return false;
            if (lastScrollTick <= 0 || nowTick < lastScrollTick)
                return true;
            return (nowTick - lastScrollTick) * 1000.0 / stopwatchFrequency >= minimumIntervalMs;
        }
    }
}
