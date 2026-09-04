namespace MTTFTest.Watchdog.Protocol
{
    // Explicit destructive-intent metadata only. No archive path, destination
    // Run, Owner, checkpoint, isolation or progress is writable by the UI.
    public sealed class ProjectResetRequest
    {
        public int ContractVersion { get; set; } = 1;
        public EngineTestConfiguration Configuration { get; set; }
        public ProjectResetRequest Clone() => new ProjectResetRequest
        { ContractVersion = ContractVersion, Configuration = Configuration?.Clone() };
        public bool IsStructurallyValid(string path) => ContractVersion == 1 &&
            new ProjectCreationRequest { Configuration = Configuration }.IsStructurallyValid(path);
        public string ComputeSha256() => SupervisorProtocol.ComputeTextSha256("ResetProjectV1\n" + Configuration?.ComputeSha256());
    }
}
