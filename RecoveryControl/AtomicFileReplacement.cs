using System;
using System.IO;
using System.Threading;

namespace MTTFTest.DurableIO
{
    internal static class AtomicFileReplacement
    {
        internal static void Retry(Action replace, Func<bool> sourceExists)
        {
            // Retry only failures that leave the durable source intact. Never
            // delete the destination or replay an already consumed source.
            for (var attempt = 0; ; attempt++)
            {
                try { replace(); return; }
                catch (IOException ex) when (attempt < 4 && sourceExists() &&
                    ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33 || (ex.HResult & 0xffff) == 1175))
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
    }
}
