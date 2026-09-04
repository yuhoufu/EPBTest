namespace PowerSupply.Core
{
    /// <summary>通信资源退出事实，不是电源输出关闭的物理证明。</summary>
    public sealed class PswClientRetirementSnapshot
    {
        internal PswClientRetirementSnapshot() { }

        public PswClientRetirementSnapshot(bool requested, bool transportReleased, bool operationsExited,
            int pendingOperations, int pendingTransportTasks, string failure = "")
        {
            if (pendingOperations < 0 || pendingTransportTasks < 0)
                throw new System.ArgumentOutOfRangeException(nameof(pendingOperations));
            RetirementRequested = requested;
            TransportReleased = transportReleased;
            OperationsExited = operationsExited;
            PendingOperations = pendingOperations;
            PendingTransportTasks = pendingTransportTasks;
            Failure = failure ?? string.Empty;
        }
        public bool RetirementRequested { get; internal set; }
        public bool TransportReleased { get; internal set; }
        public bool OperationsExited { get; internal set; }
        public int PendingOperations { get; internal set; }
        public int PendingTransportTasks { get; internal set; }
        public string Failure { get; internal set; } = string.Empty;
        public bool FullyReleased => RetirementRequested && TransportReleased && OperationsExited &&
                                     PendingOperations == 0 && PendingTransportTasks == 0 && Failure.Length == 0;
    }

    public interface IPswClientRetirementEvidence
    {
        PswClientRetirementSnapshot CaptureRetirement();
    }
}
