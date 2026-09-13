namespace MTEmbTest
{
    // UI-thread owned. The first sample must not depend on machine uptime.
    internal sealed class InstantDisplayRefreshGate
    {
        private bool _initialized;
        private int _lastTick;

        internal bool TryRefresh(int nowTick)
        {
            if (_initialized && unchecked((uint)(nowTick - _lastTick)) < 200u)
                return false;

            _initialized = true;
            _lastTick = nowTick;
            return true;
        }
    }
}
