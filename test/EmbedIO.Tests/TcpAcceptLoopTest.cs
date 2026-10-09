using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class TcpAcceptLoopTest
    {
        private static Task Start(Socket listener, Action<Socket> admit, Func<bool> stopped, bool startInline = false)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.TcpAcceptLoop");
            if (type == null) throw new AssertionException("Every retained asset must have the owned TCP accept loop.");
            var loop = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { listener, admit, stopped, (Action)listener.Dispose }, null) ?? throw new AssertionException("Missing TCP actor.");
            var run = type.GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new AssertionException("Missing runner.");
            if (startInline) return (Task)(run.Invoke(loop, null) ?? throw new AssertionException("Missing accept task."));
            return Task.Run(() => (Task)(run.Invoke(loop, null) ?? throw new AssertionException("Missing accept task.")));
        }
        private static Socket Listener()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); socket.Listen(500); return socket; }
            catch { socket.Dispose(); throw; }
        }
        [TestCase(1)]
        [TestCase(64)]
        [TestCase(128)]
        public async Task QueuedAndLateConnectionsTransferOwnershipExactlyOnce(int count)
        {
            using var listener = Listener();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var endpoint = (IPEndPoint)(listener.LocalEndPoint ?? throw new AssertionException("Missing endpoint."));
            var peers = new List<TcpClient>();
            var owned = new ConcurrentQueue<Socket>();
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = 0;
            Task? running = null;
            try
            {
                for (var index = 0; index < count; index++)
                {
                    var peer = new TcpClient(); peers.Add(peer);
                    await peer.ConnectAsync(endpoint.Address, endpoint.Port, stop.Token);
                }
                running = Start(listener, accepted =>
                {
                    owned.Enqueue(accepted);
                    if (Interlocked.Increment(ref seen) == count + 1) completed.TrySetResult(true);
                }, () => stop.IsCancellationRequested);
                var late = new TcpClient(); peers.Add(late);
                await late.ConnectAsync(endpoint.Address, endpoint.Port, stop.Token);
                await completed.Task.WaitAsync(stop.Token);
                Assert.That(seen, Is.EqualTo(count + 1));
                Assert.That(owned.Count, Is.EqualTo(count + 1));
                foreach (var socket in owned) Assert.That(socket.SafeHandle.IsClosed, Is.False, "Normal admission transfers a live socket.");
            }
            finally
            {
                stop.Cancel(); listener.Dispose();
                if (running != null) await running.WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var socket in owned) socket.Dispose();
                foreach (var peer in peers) peer.Dispose();
            }
        }
        [Test]
        public async Task AdmissionFailureClosesSocketAndNextConnectionStillWorks()
        {
            using var listener = Listener();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var endpoint = (IPEndPoint)(listener.LocalEndPoint ?? throw new AssertionException("Missing endpoint."));
            var first = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            var running = Start(listener, accepted =>
            {
                if (Interlocked.Increment(ref count) == 1)
                { first.TrySetResult(accepted); throw new InvalidOperationException("Controlled admission failure."); }
                second.TrySetResult(accepted);
            }, () => stop.IsCancellationRequested);
            try
            {
                using var rejected = new TcpClient(); await rejected.ConnectAsync(endpoint.Address, endpoint.Port, stop.Token);
                var failed = await first.Task.WaitAsync(stop.Token);
                Assert.That(await rejected.GetStream().ReadAsync(new byte[1], stop.Token), Is.Zero);
                Assert.That(failed.SafeHandle.IsClosed, Is.True);
                using var healthy = new TcpClient(); await healthy.ConnectAsync(endpoint.Address, endpoint.Port, stop.Token);
                using var admitted = await second.Task.WaitAsync(stop.Token);
                Assert.That(admitted.SafeHandle.IsClosed, Is.False);
                Assert.That(count, Is.EqualTo(2));
            }
            finally { stop.Cancel(); listener.Dispose(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        [TestCase(0)]
        [TestCase(32)]
        [TestCase(128)]
        public async Task StopDuringConnectsCompletesWorkerAndRetainsNoUnadmittedHandles(int count)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            for (var round = 0; round < 12; round++)
            {
                using var listener = Listener();
                var endpoint = (IPEndPoint)(listener.LocalEndPoint ?? throw new AssertionException("Missing endpoint."));
                var closed = 0;
                var accepted = new ConcurrentQueue<Socket>();
                // With no peers, run through the first await inline so Stop really interrupts a pending accept.
                var running = Start(listener, socket => { accepted.Enqueue(socket); socket.Dispose(); }, () => Volatile.Read(ref closed) != 0, startInline: count == 0);
                var connecting = new List<Task>();
                var peers = new List<TcpClient>();
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        var peer = new TcpClient(); peers.Add(peer);
                        connecting.Add(peer.ConnectAsync(endpoint.Address, endpoint.Port, stop.Token).AsTask());
                    }
                    Interlocked.Exchange(ref closed, 1); listener.Dispose();
                    await running.WaitAsync(stop.Token);
                    foreach (var attempt in connecting)
                    {
                        try { await attempt.WaitAsync(stop.Token); }
                        catch (SocketException) { }
                    }
                    foreach (var socket in accepted) Assert.That(socket.SafeHandle.IsClosed, Is.True);
                }
                finally
                {
                    Interlocked.Exchange(ref closed, 1); listener.Dispose();
                    await running.WaitAsync(TimeSpan.FromSeconds(5));
                    foreach (var peer in peers) peer.Dispose();
                }
            }
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task QueuedInitializationAlreadyHasAHeaderDeadline(bool secure)
        {
            var assembly = typeof(WebServer).Assembly;
            Assert.That(assembly.GetType("EmbedIO.Net.Internal.TcpAcceptLoop"), Is.Not.Null, "Every retained asset must initialize accepted connections through the owned loop.");
            var endpointType = assembly.GetType("EmbedIO.Net.Internal.EndPointListener") ?? throw new AssertionException("Missing endpoint.");
            var connectionType = assembly.GetType("EmbedIO.Net.Internal.HttpConnection") ?? throw new AssertionException("Missing connection.");
            using var owner = new Net.HttpListener();
            using var endpoint = (IDisposable)(Activator.CreateInstance(endpointType, owner, IPAddress.Loopback, 0, secure)
                ?? throw new AssertionException("Missing endpoint instance."));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var peer = new TcpClient();
            var accepting = listener.AcceptSocketAsync(stop.Token);
            await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
            using var socket = await accepting;
            using var connection = (IDisposable)(Activator.CreateInstance(connectionType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { socket, endpoint, 100 }, null) ?? throw new AssertionException("Missing connection instance."));
            // Deliberately never begin reading/authenticating: queued work must still expire.
            Assert.That(await peer.GetStream().ReadAsync(new byte[1], stop.Token), Is.Zero);
            Assert.That(socket.SafeHandle.IsClosed, Is.True);
        }
        private sealed class FailingAdmissionTrace : TraceListener
        {
            private readonly string _mode;
            internal FailingAdmissionTrace(string mode) { _mode = mode; }
            public override void Write(string? message)
            {
                if (message?.Contains("TCP connection admission failed", StringComparison.Ordinal) == true)
                {
                    if (_mode == "disposed") throw new ObjectDisposedException(nameof(FailingAdmissionTrace));
                    if (_mode == "socket") throw new SocketException(10022);
                    throw new IOException("Controlled diagnostic failure.");
                }
            }
            public override void WriteLine(string? message) => Write(message);
        }
        [TestCase("admission")]
        [TestCase("io")]
        [TestCase("disposed")]
        [TestCase("socket")]
        [NonParallelizable]
        public async Task UnrecoverableAdmissionOrDiagnosticFailureClosesPendingAndBacklogSockets(string mode)
        {
            Assert.That(typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.TcpAcceptLoop"), Is.Not.Null, "Every retained asset must have the owned TCP accept loop.");
            using var listener = Listener();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var endpoint = (IPEndPoint)(listener.LocalEndPoint ?? throw new AssertionException("Missing endpoint."));
            var peers = new List<TcpClient>();
            var source = EmbedIO.Diagnostics.Log.Source;
            var previousLevel = source.Switch.Level;
            var diagnostic = mode != "admission";
            using var failingTrace = new FailingAdmissionTrace(mode);
            Task? running = null;
            try
            {
                for (var index = 0; index < 3; index++)
                {
                    var peer = new TcpClient(); peers.Add(peer);
                    await peer.ConnectAsync(endpoint.Address, endpoint.Port, deadline.Token);
                }
                source.Switch.Level = SourceLevels.Warning;
                if (diagnostic) source.Listeners.Add(failingTrace);
                running = Start(listener, _ =>
                {
                    if (diagnostic) throw new InvalidOperationException("Controlled admission failure.");
                    // Synthetic exception, not a real memory-exhaustion experiment.
                    throw new OutOfMemoryException("Controlled nonrecoverable admission failure.");
                }, () => listener.SafeHandle.IsClosed);
                if (mode == "disposed") await Assert.ThrowsAsync<ObjectDisposedException>(() => running.WaitAsync(deadline.Token));
                else if (mode == "socket") await Assert.ThrowsAsync<SocketException>(() => running.WaitAsync(deadline.Token));
                else if (diagnostic) await Assert.ThrowsAsync<IOException>(() => running.WaitAsync(deadline.Token));
                else await Assert.ThrowsAsync<OutOfMemoryException>(() => running.WaitAsync(deadline.Token));
                Assert.That(listener.SafeHandle.IsClosed, Is.True, "A failed worker must request owner shutdown before completing.");
                foreach (var peer in peers)
                {
                    try { Assert.That(await peer.GetStream().ReadAsync(new byte[1], deadline.Token), Is.Zero); }
                    catch (IOException error) when (error.InnerException is SocketException) { }
                }
            }
            finally
            {
                source.Listeners.Remove(failingTrace); source.Switch.Level = previousLevel;
                listener.Dispose();
                if (running != null)
                {
                    try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (ObjectDisposedException) when (mode == "disposed") { }
                    catch (SocketException) when (mode == "socket") { }
                    catch (IOException) when (mode == "io") { }
                    catch (OutOfMemoryException) when (!diagnostic) { }
                }
                foreach (var peer in peers) peer.Dispose();
            }
        }
    }
}
