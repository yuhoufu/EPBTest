using System.Threading;

namespace Controller
{
    public enum DaqRecoveryTerminal
    {
        None = 0,
        Recovered = 1,
        Cancelled = 2,
        SystemFault = 3,
        HardwareConfirmed = 4
    }

    /// <summary>恢复成功、超时、停止和硬件确认共用的单次原子终态门。</summary>
    public sealed class DaqRecoveryTerminalGate
    {
        private int _terminal;

        public DaqRecoveryTerminal Current =>
            (DaqRecoveryTerminal)Volatile.Read(ref _terminal);

        public bool TryCommit(DaqRecoveryTerminal terminal)
        {
            if (terminal == DaqRecoveryTerminal.None) return false;
            return Interlocked.CompareExchange(
                       ref _terminal,
                       (int)terminal,
                       (int)DaqRecoveryTerminal.None) ==
                   (int)DaqRecoveryTerminal.None;
        }
    }
}
