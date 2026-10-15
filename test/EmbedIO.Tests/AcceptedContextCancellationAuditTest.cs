using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class AcceptedContextCancellationAuditTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task AcceptedContextClosesWhenCancellationWinsBeforeDispatch(bool cancelBeforeDispatch)
        {
            using var cancellation = new CancellationTokenSource();
            using var message = new HttpRequestMessage(HttpMethod.Get, "http://localhost/audit");
            var assembly = typeof(TestWebServer).Assembly;
            var requestType = assembly.GetType("EmbedIO.Testing.Internal.TestRequest", true)
                ?? throw new InvalidOperationException("Missing test request type.");
            var constructor = requestType.GetConstructors().Single();
            var arguments = constructor.GetParameters().Length == 1
                ? new object[] { message }
                : new object[] { message, string.Empty };
            var request = constructor.Invoke(arguments);
            var contextType = assembly.GetType("EmbedIO.Testing.Internal.TestContext", true)
                ?? throw new InvalidOperationException("Missing test context type.");
            var inner = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { request }, null) as IHttpContextImpl
                ?? throw new InvalidOperationException("Cannot create test context.");
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            inner.OnClose(_ => closed.TrySetResult(true));
            var context = DispatchProxy.Create<IHttpContextImpl, CancellationContextProxy>();
            var proxy = (CancellationContextProxy)(object)context;
            proxy.Inner = inner;
            proxy.BeforeDispatch = () => { if (cancelBeforeDispatch) cancellation.Cancel(); };
            using var server = new AcceptLoopServer();
            server.Listener.Dispose();
            var listenerField = typeof(WebServer).GetField("<Listener>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing listener backing field.");
            listenerField.SetValue(server, new SingleContextListener(context));
            try
            {
                await server.ProcessAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3));
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(proxy.CloseCount, Is.EqualTo(1));
            }
            finally
            {
                if (!closed.Task.IsCompleted) inner.Close();
            }
        }

        public class CancellationContextProxy : DispatchProxy
        {
            public IHttpContextImpl? Inner { get; set; }
            public Action? BeforeDispatch { get; set; }
            public int CloseCount { get; private set; }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                var method = targetMethod ?? throw new InvalidOperationException("Missing proxy method.");
                if (method.Name == nameof(IHttpContextImpl.Close)) CloseCount++;
                var result = method.Invoke(Inner ?? throw new InvalidOperationException("Missing proxy target."), args);
                if (method.Name == "set_Route") BeforeDispatch?.Invoke();
                return result;
            }
        }

        private sealed class AcceptLoopServer : WebServer
        {
            public AcceptLoopServer() : base("http://localhost:9696/") { }
            public Task ProcessAsync(CancellationToken token) => ProcessRequestsAsync(token);
        }

        private sealed class SingleContextListener : IHttpListener
        {
            private readonly IHttpContextImpl _context;
            public SingleContextListener(IHttpContextImpl context) => _context = context;
            public bool IgnoreWriteExceptions { get; set; }
            public List<string> Prefixes { get; } = new List<string>();
            public bool IsListening { get; private set; } = true;
            public string Name => "audit";
            public void AddPrefix(string prefix) => Prefixes.Add(prefix);
            public void Start() => IsListening = true;
            public void Stop() => IsListening = false;
            public void Dispose() => Stop();
            public Task<IHttpContextImpl> GetContextAsync(CancellationToken cancellationToken)
            {
                IsListening = false;
                return Task.FromResult(_context);
            }
        }
    }
}
