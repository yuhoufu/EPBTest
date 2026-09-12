using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    // Binary framing remains synchronous to callers; every underlying I/O is
    // overlapped and joined before return. No abandoned Task.Run reader.
    public static class DeadlinePipeExchange
    {
        public static T Execute<T>(string name, int timeoutMs,
            Action<BinaryWriter> write, Func<BinaryReader, T> read,
            CancellationToken cancellation = default)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var pipe = new NamedPipeClientStream(".", name,
                       PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                deadline.CancelAfter(timeoutMs);
                using (deadline.Token.Register(() => pipe.Dispose()))
                try
                {
                    pipe.ConnectAsync(timeoutMs, deadline.Token).GetAwaiter().GetResult();
                    using (var stream = new DeadlineStream(pipe, deadline.Token))
                    using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), true))
                    using (var reader = new BinaryReader(stream, new UTF8Encoding(false), true))
                    {
                        write(writer);
                        var result = read(reader);
                        deadline.Token.ThrowIfCancellationRequested();
                        return result;
                    }
                }
                catch (Exception error) when (deadline.IsCancellationRequested)
                {
                    cancellation.ThrowIfCancellationRequested();
                    throw new TimeoutException("SupervisorPipeTotalDeadlineExceeded", error);
                }
            }
        }

        private sealed class DeadlineStream : Stream
        {
            private readonly Stream _inner;
            private readonly CancellationToken _token;
            internal DeadlineStream(Stream inner, CancellationToken token)
            { _inner = inner; _token = token; }
            public override int Read(byte[] buffer, int offset, int count) =>
                _inner.ReadAsync(buffer, offset, count, _token).GetAwaiter().GetResult();
            public override void Write(byte[] buffer, int offset, int count) =>
                _inner.WriteAsync(buffer, offset, count, _token).GetAwaiter().GetResult();
            public override void Flush() => _token.ThrowIfCancellationRequested();
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long length) => throw new NotSupportedException();
        }
    }
}
