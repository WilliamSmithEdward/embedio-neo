using System;
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

        ~EndToEndFixtureBase()
        {
            Dispose(false);
        }

        protected Uri WebServerUrl { get; private set; } = new Uri(TestWebServer.DefaultBaseUrl);

        private TestHttpClient? _client;
        protected TestHttpClient Client
        {
            get => _client ?? throw new InvalidOperationException("The fixture has not been initialized.");
            private set => _client = value;
        }

        private IWebServer? _server;
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
        public void SetUp()
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
            Server.Start();
        }

        [TearDown]
        public void TearDown()
        {
            Task.Delay(500).ConfigureAwait(false).GetAwaiter().GetResult();
            _server?.Dispose();
            OnTearDown();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            _client?.Dispose();
            _server?.Dispose();
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
