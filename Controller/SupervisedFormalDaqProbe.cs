using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IO.NI;

namespace Controller
{
    internal static class SupervisedFormalDaqProbe
    {
        internal static async Task VerifyAsync(long expectedGeneration, Func<DaqFreshnessSnapshot> read,
            CancellationToken token, int timeoutMs = 500)
        {
            if (expectedGeneration <= 0 || read == null || timeoutMs < 1 || timeoutMs > 3000)
                throw new ArgumentException("QualifiedDaqProbeInvalid");
            var clock = Stopwatch.StartNew();
            var verifier = new DaqRecoveryFreshnessVerifier(expectedGeneration, 3);
            token.ThrowIfCancellationRequested();
            var initial = read();
            if (initial == null || initial.Generation != expectedGeneration || !initial.IsFresh)
                throw new InvalidOperationException("QualifiedDaqGenerationOrFreshnessChanged");
            verifier.Seed(initial);
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                token.ThrowIfCancellationRequested();
                var snapshot = read();
                if (snapshot == null || snapshot.Generation != expectedGeneration || !snapshot.IsFresh)
                    throw new InvalidOperationException("QualifiedDaqGenerationOrFreshnessChanged");
                if (verifier.Observe(snapshot)) return;
                await Task.Delay(5, token).ConfigureAwait(false);
            }
            throw new TimeoutException("QualifiedDaqFreshCallbacksNotObserved");
        }
    }
}
