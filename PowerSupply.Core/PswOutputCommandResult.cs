using System;

namespace PowerSupply.Core
{
    public enum PswOutputState
    {
        Unknown = 0,
        Off = 1,
        On = 2
    }

    /// <summary>
    /// Strongly typed result for an output command.  In particular, OFF is a
    /// successful observed state and must never be confused with a failed
    /// boolean operation.
    /// </summary>
    public sealed class PswOutputCommandResult
    {
        public int SupplyId { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public string Identity { get; set; } = string.Empty;
        public bool IdentityVerified { get; set; }
        public PswOutputState RequestedState { get; set; }
        public PswOutputState ObservedState { get; set; }
        public bool CommandWritten { get; set; }
        public bool ReadBackVerified { get; set; }
        public string FailureStage { get; set; } = string.Empty;
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }
        public double DurationMs { get; set; }

        public bool Succeeded =>
            IdentityVerified && CommandWritten && ReadBackVerified &&
            RequestedState == ObservedState && ObservedState != PswOutputState.Unknown;
    }
}
