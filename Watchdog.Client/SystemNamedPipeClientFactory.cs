using System.IO.Pipes;

namespace MTTFTest.Watchdog.Client
{
    public sealed class SystemNamedPipeClientFactory : INamedPipeClientFactory
    {
        public NamedPipeClientStream Create(string pipeName)
        {
            return new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
        }
    }
}
