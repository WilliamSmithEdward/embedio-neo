namespace EmbedIO.Quic
{
    // Drives the owned async APIs exactly as an application transport consumer:
    // independent readers/writers, bounded handlers and explicit FIN delivery.
    internal sealed class AsyncStreamEcho(AsyncStreamApplication application, bool closeAfterEcho = false)
    {
        private int _completed;

        internal int Completed => Volatile.Read(ref _completed);

        internal async Task RunAsync(CancellationToken token)
        {
            using var slots = new SemaphoreSlim(32);
            var workers = new List<Task>();
            var datagrams = EchoDatagramsAsync(token);

            try
            {
                while (true)
                {
                    await slots
                        .WaitAsync(token)
                        .ConfigureAwait(false);
                    AsyncQuicStream stream;

                    try
                    {
                        stream = await application
                            .AcceptInboundStreamAsync(token)
                            .ConfigureAwait(false);
                    }

                    catch
                    {
                        slots.Release();
                        throw;
                    }

                    for (var index = workers.Count - 1; index >= 0; index--)
                        if (workers[index].IsCompleted)
                        {
                            await workers[index].ConfigureAwait(false);
                            workers.RemoveAt(index);
                        }

                    workers.Add(EchoAsync(stream, slots, token));
                }
            }

            catch (IOException) when (application.IsClosed) { }

            catch (OperationCanceledException) when (token.IsCancellationRequested) { }

            finally
            {
                await Task
                    .WhenAll(workers)
                    .ConfigureAwait(false);
                await datagrams.ConfigureAwait(false);
            }
        }

        private async Task EchoAsync(AsyncQuicStream stream, SemaphoreSlim slots, CancellationToken token)
        {
            try
            {
                if (stream.Unidirectional)
                {
                    await using var output = await application
                        .OpenOutboundStreamAsync((stream.Id / 4 & 1) == 0, token)
                        .ConfigureAwait(false);
                    await CopyAsync(stream, output, token).ConfigureAwait(false);
                }

                else await CopyAsync(stream, stream, token).ConfigureAwait(false);

                Interlocked.Increment(ref _completed);

                if (closeAfterEcho)
                    await application
                        .CloseAsync(0x1234, token)
                        .ConfigureAwait(false);
            }

            catch (IOException) { }

            catch (OperationCanceledException) when (token.IsCancellationRequested) { }

            finally
            {
                await stream
                    .DisposeAsync()
                    .ConfigureAwait(false);
                slots.Release();
            }
        }

        private static async Task CopyAsync(AsyncQuicStream input, AsyncQuicStream output, CancellationToken token)
        {
            var buffer = new byte[16384];

            while (true)
            {
                var count = await input
                    .ReadAsync(buffer, token)
                    .ConfigureAwait(false);

                if (count == 0) break;

                await output
                    .WriteAsync(buffer.AsMemory(0, count), token)
                    .ConfigureAwait(false);
            }

            output.CompleteWrites();
            await output.WritesClosed
                .WaitAsync(token)
                .ConfigureAwait(false);
        }

        private async Task EchoDatagramsAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    var bytes = await application
                        .ReceiveDatagramAsync(token)
                        .ConfigureAwait(false);

                    if (bytes.Length <= application.MaximumDatagramPayload) application.TrySendDatagram(bytes);
                }
            }

            catch (IOException) when (application.IsClosed) { }

            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }
}
