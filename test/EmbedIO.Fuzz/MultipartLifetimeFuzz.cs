using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;

internal static class MultipartLifetimeFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        // All three parsers are blocked in their actual first source read before release.
        var gates = Enumerable.Range(0, 3).Select(_ => new ReadGate()).ToArray();
        var overlapping = gates.Select((gate, actor) => Exercise(0, actor + 1, gate)).ToArray();
        try { await Task.WhenAll(gates.Select(gate => gate.Entered.Task)).WaitAsync(token).ConfigureAwait(false); }
        finally { foreach (var gate in gates) gate.Release.TrySetResult(); }
        await Task.WhenAll(overlapping).ConfigureAwait(false);
        trace.Cover("multipart.three-pending-readers");
        // Each safety boundary is retained even if the generated trace shrinks to empty.
        for (var mode = 1; mode < 6; mode++) await Exercise(mode, mode, null).ConfigureAwait(false);
        await Exercise(3, 4, null).ConfigureAwait(false);
        await Exercise(3, 5, null).ConfigureAwait(false);
        foreach (var step in trace.Steps) await Exercise(step.Command % 6, step.Value + step.Actor, null).ConfigureAwait(false);

        async Task Exercise(int mode, int value, ReadGate? first)
        {
            var size = mode == 3 ? 16384 : 1 + value * 17;
            var expected = Enumerable.Range(0, size).Select(index => (byte)('a' + (index + value) % 26)).ToArray();
            var body = (byte[])expected.Clone();
            if (injectFault) body[0] ^= 1;
            var prefix = Encoding.ASCII.GetBytes("--state-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"item.bin\"\r\n\r\n");
            var suffix = Encoding.ASCII.GetBytes("\r\n--state-boundary\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\ndone\r\n--state-boundary--\r\n");
            var encoded = prefix.Concat(body).Concat(suffix).ToArray();
            if (mode == 5) encoded = encoded[..^5];
            using var source = new Source(encoded, 1 + value % 37) { Gate = first };
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var reader = new MultipartReader(source, "state-boundary",
                new MultipartOptions { MaxFileBytes = mode == 4 ? size - 1 : 32768 }, lifetime.Token);
            Task? copying = null;
            try
            {
                var file = await reader.ReadNextSectionAsync(token).ConfigureAwait(false)
                    ?? throw new StateMachineFailure("multipart.file-present", "Missing first part.");
                Require(file.Name == "file" && file.FileName == "item.bin", "multipart.metadata", value.ToString());
                if (mode == 3)
                {
                    var mid = new ReadGate(); source.Gate = mid;
                    copying = file.Body.CopyToAsync(Stream.Null, value % 3 == 0 ? cancellation.Token : token);
                    try
                    {
                        await mid.Entered.Task.WaitAsync(token).ConfigureAwait(false);
                        var concurrentRejected = false;
                        try { await reader.ReadNextSectionAsync(token).ConfigureAwait(false); }
                        catch (InvalidOperationException) { concurrentRejected = true; }
                        Require(concurrentRejected, "multipart.sequential-contract", value.ToString());
                        if (value % 3 == 0) cancellation.Cancel();
                        else if (value % 3 == 1) lifetime.Cancel();
                        else reader.Dispose();
                        var rejected = false;
                        try { await copying.ConfigureAwait(false); } catch (OperationCanceledException) { rejected = true; }
                        Require(rejected, "multipart.pending-read-cancellation", value.ToString());
                        trace.Cover("multipart.cancel-source-read-" + value % 3);
                    }
                    finally { mid.Release.TrySetResult(); }
                    return;
                }
                if (mode == 2) file.Body.Dispose();
                if (mode == 0)
                {
                    using var output = new MemoryStream();
                    await file.Body.CopyToAsync(output, token).ConfigureAwait(false);
                    Require(expected.AsSpan().SequenceEqual(output.ToArray()), "multipart.independent-body", value.ToString());
                }
                // Advancing drains unread/disposed parts and still enforces their limits.
                var note = await reader.ReadNextSectionAsync(token).ConfigureAwait(false)
                    ?? throw new StateMachineFailure("multipart.note-present", value.ToString());
                using var text = new StreamReader(note.Body, Encoding.UTF8, false, 128, true);
                Require(note.Name == "note" && await text.ReadToEndAsync(token).ConfigureAwait(false) == "done", "multipart.next-part-isolation", value.ToString());
                Require(await reader.ReadNextSectionAsync(token).ConfigureAwait(false) == null, "multipart.final-boundary", value.ToString());
                Require(mode is not (4 or 5), "multipart.invalid-input-rejected", value.ToString());
            }
            catch (HttpException error) when ((mode == 4 && error.StatusCode == 413) || (mode == 5 && error.StatusCode == 400))
            { trace.Cover(mode == 4 ? "multipart.drain-limit" : "multipart.truncated-rejected"); }
            finally
            {
                cancellation.Cancel(); source.Gate?.Release.TrySetResult(); reader.Dispose();
                if (copying != null)
                {
                    try { await copying.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested || lifetime.IsCancellationRequested) { }
                }
                Require(source.DisposeCalls == 0, "multipart.borrowed-source", value.ToString());
            }
            trace.Cover("multipart.mode-" + mode);
        }
    }
    private sealed class ReadGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Source(byte[] bytes, int chunk) : MemoryStream(bytes, false)
    {
        internal ReadGate? Gate;
        internal int DisposeCalls;
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var gate = Gate;
            if (gate != null) { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            return await base.ReadAsync(buffer, offset, Math.Min(count, chunk), cancellationToken).ConfigureAwait(false);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var gate = Gate;
            if (gate != null) { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing) { if (disposing) DisposeCalls++; base.Dispose(disposing); }
    }
    private static void Require(bool condition, string invariant, string detail)
    { if (!condition) throw new StateMachineFailure(invariant, detail); }
}
