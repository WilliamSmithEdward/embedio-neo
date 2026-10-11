using System;
using System.Collections.Concurrent;
using System.Net.Quic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class MsQuicApiLifetimeTest
    {
        [TestCase(QuicAbortDirection.Read)]
        [TestCase(QuicAbortDirection.Write)]
        [TestCase(QuicAbortDirection.Both)]
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public async Task DisposalPreservesEveryExplicitNativeAbortCode(QuicAbortDirection direction)
        {
            if (!QuicListener.IsSupported)
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.Fail("QUIC is required.");
                Assert.Ignore("QUIC is unavailable.");
            }
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                await using var sending = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                await sending.WriteAsync(new byte[] { 42 }, false, token);
                var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await accepting.WaitAsync(token);
                using var native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                    ?? throw new AssertionException("Missing native stream."));
                Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", new byte[1].AsMemory(), token), Is.EqualTo(1));
                using var recorder = new NativeShutdownRecorder(native);
                ListenerCall(native, "Abort", direction, 0x1234L);
                await ((ValueTask)ListenerCall(native, "DisposeAsync")).AsTask().WaitAsync(token);
                var sendCode = direction == QuicAbortDirection.Read ? 0x10cUL : 0x1234UL;
                var readCode = direction == QuicAbortDirection.Write ? 0x10cUL : 0x1234UL;
                Assert.That(recorder.SendAborts.ToArray(), Is.EqualTo(new[] { sendCode }), "An explicit send reset must not be reissued during disposal.");
                Assert.That(recorder.ReadAborts.ToArray(), Is.EqualTo(new[] { readCode }), "An explicit read abort must not be reissued during disposal.");
                await Assert.ThatAsync(async () => await sending.ReadsClosed.WaitAsync(token),
                    Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.ApplicationErrorCode)).EqualTo((long)sendCode));
                await Assert.ThatAsync(async () => await sending.WritesClosed.WaitAsync(token),
                    Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.ApplicationErrorCode)).EqualTo((long)readCode));
            });
        }

        // Observe our own binding while forwarding every operation to stock MsQuic.
        // Capture only this stream; restore the binding before releasing its API.
        private sealed class NativeShutdownRecorder : IDisposable
        {
            private readonly object _functions;
            private readonly FieldInfo _field;
            private readonly Delegate _original;
            private readonly IntPtr _stream;
            internal readonly ConcurrentQueue<ulong> SendAborts = new();
            internal readonly ConcurrentQueue<ulong> ReadAborts = new();

            internal NativeShutdownRecorder(SafeHandle stream)
            {
                _stream = stream.DangerousGetHandle();
                _functions = stream.GetType().GetField("_functions", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(stream)
                    ?? throw new AssertionException("Missing stream API binding.");
                _field = _functions.GetType().GetField("Shutdown", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing stream shutdown binding.");
                _original = (Delegate)(_field.GetValue(_functions) ?? throw new AssertionException("Missing shutdown delegate."));
                _field.SetValue(_functions, Delegate.CreateDelegate(_field.FieldType, this, nameof(Forward)));
            }

            public uint Forward(IntPtr stream, uint flags, ulong code)
            {
                if (stream == _stream)
                {
                    if ((flags & 2) != 0) SendAborts.Enqueue(code);
                    if ((flags & 4) != 0) ReadAborts.Enqueue(code);
                }
                return (uint)(_original.DynamicInvoke(stream, flags, code) ?? throw new AssertionException("Missing native shutdown status."));
            }

            public void Dispose() => _field.SetValue(_functions, _original);
        }
    }
}
