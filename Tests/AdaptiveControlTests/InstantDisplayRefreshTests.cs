using System;
using MTEmbTest;

namespace AdaptiveControlTests
{
    internal static class InstantDisplayRefreshTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            foreach (var first in new[] { -2127161265, int.MinValue, 0, 123, int.MaxValue - 100, -100 })
            {
                var gate = new InstantDisplayRefreshGate();
                if (!gate.TryRefresh(first)) throw new Exception("First real sample was not displayed");
                if (gate.TryRefresh(first)) throw new Exception("Repeated sample bypassed throttle");
                if (gate.TryRefresh(unchecked(first + 199))) throw new Exception("Refreshed before interval");
                if (!gate.TryRefresh(unchecked(first + 200))) throw new Exception("Tick wrap blocked refresh");
                if (!gate.TryRefresh(unchecked(first + 400))) throw new Exception("Subsequent refresh blocked");
                passed++;
            }
            return passed;
        }
    }
}
