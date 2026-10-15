using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;
using EmbedIO.Internal;
using EmbedIO.Diagnostics;

namespace EmbedIO.Tests
{
    public abstract class EndToEndFixtureBase : IDisposable
    {
        private readonly bool _useTestWebServer;

        protected EndToEndFixtureBase(bool useTestWebServer = true)
        {
            _useTestWebServer = useTestWebServer;
        }

        protected Uri WebServerUrl { get; private set; } = new Uri(TestWebServer.DefaultBaseUrl);

        private TestHttpClient? _client;
        protected TestHttpClient Client
        {
            get => _client ?? throw new InvalidOperationException("The fixture has not been initialized.");
            private set => _client = value;
        }

        private IWebServer? _server;
        private CancellationTokenSource? _stop;
        private Task? _running;
        protected IWebServer Server
        {
            get => _server ?? throw new InvalidOperationException("The fixture has not been initialized.");
            set => _server = value;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        [SetUp]
        public async Task SetUp()
        {
            WebServerUrl = new Uri(Resources.GetServerAddress());

            if (_useTestWebServer)
            {
                var testWebServer = new TestWebServer(WebServerUrl);
                Server = testWebServer;
                Client = testWebServer.Client;
            }
            else
            {
                Server = new WebServer(WebServerUrl);
                Client = TestHttpClient.Create(WebServerUrl);
            }

            OnSetUp();
            _stop = new CancellationTokenSource();
            var server = Server;
            var token = _stop.Token;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStateChanged(object sender, WebServerStateChangedEventArgs args)
            {
                if (args.NewState == WebServerState.Listening) ready.TrySetResult(true);
            }
            server.StateChanged += OnStateChanged;
            try
            {
                // TestWebServer's queue loop blocks synchronously, so dispatch both
                // implementations while retaining the task that owns their lifetime.
                _running = Task.Run(() => server.RunAsync(token));
                await Task.WhenAny(ready.Task, _running).WaitAsync(TimeSpan.FromSeconds(10));
                if (_running.IsCompleted) await _running;
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally { server.StateChanged -= OnStateChanged; }
        }

        [TearDown]
        public async Task TearDown()
        {
            try
            {
                _stop?.Cancel();
                if (_running != null) await _running.WaitAsync(TimeSpan.FromSeconds(10));
                if (_server != null) Assert.That(_server.State, Is.EqualTo(WebServerState.Stopped));
            }
            finally
            {
                _client?.Dispose();
                _server?.Dispose();
                _stop?.Dispose();
                _client = null;
                _server = null;
                _stop = null;
                _running = null;
                OnTearDown();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            _stop?.Cancel();
            _client?.Dispose();
            _server?.Dispose();
            _stop?.Dispose();
        }

        protected virtual void OnSetUp()
        {
        }

        protected virtual void OnTearDown()
        {
        }
    }

    [SetUpFixture]
    public class SetUpEndToEnd
    {
        [OneTimeSetUp]
        public void RunBeforeAnyTests() => Log.Source.Listeners.Clear();
    }
}
