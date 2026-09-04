using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>Total deadline includes connect, write and read; cancellation closes a silent peer.</summary>
    public static class BoundedPipeTransport
    {
        public static async Task<T> ExchangeBinaryAsync<T>(string pipeName, Action<BinaryWriter> write,
            Func<BinaryReader, T> read, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                deadline.CancelAfter(timeoutMilliseconds);
                using (deadline.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                {
                    try
                    {
                        await pipe.ConnectAsync(timeoutMilliseconds, deadline.Token).ConfigureAwait(false);
                        return await Task.Run(() =>
                        {
                            using (var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, true))
                            {
                                var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, true);
                                try { write(writer); return read(reader); }
                                finally { try { writer.Dispose(); } catch (IOException) { } }
                            }
                        }, deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (deadline.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("PipeTotalDeadlineExceeded", ex);
                    }
                }
            }
        }

        public static async Task<byte[]> ExchangeAsync(string pipeName, byte[] request,
            int timeoutMilliseconds, int maximumBytes, CancellationToken cancellationToken,
            Action<NamedPipeClientStream> validateServer = null)
        {
            if (request == null || request.Length == 0 || request.Length > maximumBytes)
                throw new InvalidDataException("PipeRequestLengthInvalid");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                deadline.CancelAfter(timeoutMilliseconds);
                using (deadline.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                {
                    try
                    {
                        await pipe.ConnectAsync(timeoutMilliseconds, deadline.Token).ConfigureAwait(false);
                        validateServer?.Invoke(pipe);
                        var length = BitConverter.GetBytes(request.Length);
                        await pipe.WriteAsync(length, 0, length.Length, deadline.Token).ConfigureAwait(false);
                        await pipe.WriteAsync(request, 0, request.Length, deadline.Token).ConfigureAwait(false);
                        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                        await ReadExactlyAsync(pipe, length, deadline.Token).ConfigureAwait(false);
                        var count = BitConverter.ToInt32(length, 0);
                        if (count <= 0 || count > maximumBytes)
                            throw new InvalidDataException("PipeResponseLengthInvalid");
                        var response = new byte[count];
                        await ReadExactlyAsync(pipe, response, deadline.Token).ConfigureAwait(false);
                        return response;
                    }
                    catch (Exception ex) when (deadline.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("PipeTotalDeadlineExceeded", ex);
                    }
                }
            }
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken token)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("PipeResponseTruncated");
                offset += count;
            }
        }
    }
}
