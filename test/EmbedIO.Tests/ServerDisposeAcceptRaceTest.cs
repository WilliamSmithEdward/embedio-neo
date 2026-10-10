using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class ServerDisposeAcceptRaceTest
    {
        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        public async Task DisposalBetweenListeningCheckAndAcceptPreservesGenuineFailures(bool disposeServer, int failure)
        {
            Exception expected = failure switch
            {
                0 => new ObjectDisposedException("Controlled listener"),
                1 => new HttpListenerException(995, "The listener stopped accepting requests."),
                3 => new HttpListenerException(503, "Independent admission failure."),
                _ => new IOException("Independent output failure."),
            };
            using var server = new WebServer("http://127.0.0.1:1/");
            var original = server.Listener;
            var listener = new RaceListener(expected);
            var field = typeof(WebServer).GetField("<Listener>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing server listener field.");
            field.SetValue(server, listener);
            original.Dispose();
            // Return the listening snapshot from before disposal, then dispose
            // before GetContextAsync. No timing or scheduler luck is required.
            if (disposeServer) listener.OnListeningSnapshot = server.Dispose;
            var running = server.RunAsync();
            if (disposeServer && failure < 2)
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await Assert.ThatAsync(async () => await running.WaitAsync(TimeSpan.FromSeconds(5)), Throws.Exception.SameAs(expected));
            Assert.That(listener.AcceptAttempted, Is.True);
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
        }

        private sealed class RaceListener : IHttpListener
        {
            private readonly Exception _failure;
            private bool _listening;
            internal RaceListener(Exception failure) => _failure = failure;
            internal Action? OnListeningSnapshot { get; set; }
            internal bool AcceptAttempted { get; private set; }
            public bool IgnoreWriteExceptions { get; set; }
            public List<string> Prefixes { get; } = new();
            public string Name => "Controlled listener";
            public bool IsListening
            {
                get
                {
                    var snapshot = _listening;
                    var action = OnListeningSnapshot;
                    OnListeningSnapshot = null;
                    action?.Invoke();
                    return snapshot;
                }
            }
            public void AddPrefix(string listenerPrefix) => Prefixes.Add(listenerPrefix);
            public void Start() => _listening = true;
            public void Stop() => _listening = false;
            public void Dispose() => Stop();
            public Task<IHttpContextImpl> GetContextAsync(CancellationToken cancellationToken)
            {
                AcceptAttempted = true;
                return Task.FromException<IHttpContextImpl>(_failure);
            }
        }
    }
}
