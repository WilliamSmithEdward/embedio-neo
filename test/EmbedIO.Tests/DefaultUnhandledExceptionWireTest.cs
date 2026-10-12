using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.Problems;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed class DefaultUnhandledExceptionWireTest
    {
        public enum ResponsePolicy
        {
            Default,
            Html,
            Headers,
            Custom,
            ProblemDetails,
        }

        [TestCase(1, ResponsePolicy.Default)]
        [TestCase(2, ResponsePolicy.Default)]
        [TestCase(3, ResponsePolicy.Default)]
        [TestCase(1, ResponsePolicy.Html)]
        [TestCase(2, ResponsePolicy.Html)]
        [TestCase(3, ResponsePolicy.Html)]
        [TestCase(1, ResponsePolicy.Headers)]
        [TestCase(2, ResponsePolicy.Headers)]
        [TestCase(3, ResponsePolicy.Headers)]
        [TestCase(1, ResponsePolicy.Custom)]
        [TestCase(2, ResponsePolicy.Custom)]
        [TestCase(3, ResponsePolicy.Custom)]
        [TestCase(1, ResponsePolicy.ProblemDetails)]
        [TestCase(2, ResponsePolicy.ProblemDetails)]
        [TestCase(3, ResponsePolicy.ProblemDetails)]
        public async Task ErrorDisclosurePolicyPreservesLoggingAndHealthyConnection(int protocol, ResponsePolicy policy)
        {
            // A retained Standard asset has no secure H2 or direct H3 transport.
            if (protocol != 1 && typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener") == null)
            {
                Assert.Ignore("This wire case requires the modern core asset; H1 remains separately exercised.");
                return;
            }

            if (protocol == 3)
            {
                var supported = QuicConnection.IsSupported && QuicListener.IsSupported;
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1")
                    Assert.That(supported, Is.True, "Required stock QUIC provider is unavailable.");
                if (!supported)
                {
                    Assert.Ignore("The host has no supported QUIC provider.");
                    return;
                }
            }

            var version = protocol == 1 ? HttpVersion.Version11 : protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version30;
            var marker = "owned-error-" + Guid.NewGuid().ToString("N");
            var original = new InvalidOperationException(marker);
            const string applicationMessage = "Application conflict remains intentional.";
            var peerPorts = new ConcurrentQueue<int>();
            Exception? callbackException = null;
            var callbackCount = 0;
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            var prefix = protocol == 3 ? QuicPrefix() : HttpsSmoke.GetUrl();
            using var server = new WebServer(options => options.WithUrlPrefix(prefix)
                .WithMode(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO)
                .WithCertificate(certificate));
            switch (policy)
            {
                case ResponsePolicy.Html:
                    server.HandleUnhandledException(ExceptionHandler.HtmlResponse);
                    break;
                case ResponsePolicy.Headers:
                    server.HandleUnhandledException(ExceptionHandler.EmptyResponseWithHeaders);
                    break;
                case ResponsePolicy.Custom:
                    server.HandleUnhandledException(async (context, error) =>
                    {
                        callbackException = error;
                        Interlocked.Increment(ref callbackCount);
                        context.Response.StatusCode = 500;
                        await context.SendStringAsync("Explicit custom response.", "text/plain", WebServer.Utf8NoBomEncoding);
                    });
                    break;
                case ResponsePolicy.ProblemDetails:
                    server.WithProblemDetails();
                    break;
            }

            // This independently configured application-error handler must remain
            // unaffected by the unhandled-exception default or ProblemDetails opt-in.
            server.HandleHttpException(HttpExceptionHandler.PlainTextResponse);
            server.WithModule(new ActionModule(async context =>
            {
                peerPorts.Enqueue(context.RemoteEndPoint.Port);
                if (context.RequestedPath == "/boom") throw original;
                if (context.RequestedPath == "/application") throw new HttpException(409, applicationMessage);
                await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
            }));
            using var trace = new ErrorTrace();
            var source = Diagnostics.Log.Source;
            var previousLevel = source.Switch.Level;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, change) =>
            {
                if (change.NewState == WebServerState.Listening) listening.TrySetResult();
            };
            Task? running = null;
            Exception? primaryFailure = null;
            source.Listeners.Add(trace);
            try
            {
                source.Switch.Level = SourceLevels.All;
                running = server.RunAsync(stop.Token);
                var first = await Task.WhenAny(listening.Task, running).WaitAsync(stop.Token);
                if (first == running)
                {
                    await running;
                    Assert.Fail("The listener stopped before publishing Listening.");
                }

                await listening.Task.WaitAsync(stop.Token);
                using var client = HttpsSmoke.CreateClient(certificate, new SocketsHttpHandler
                {
                    UseProxy = false,
                    MaxConnectionsPerServer = 1,
                });
                client.DefaultRequestVersion = version;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                using (var before = await client.GetAsync(prefix + "healthy", stop.Token))
                {
                    Assert.That(before.Version, Is.EqualTo(version));
                    Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await before.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("healthy"));
                }

                using (var response = await client.GetAsync(prefix + "boom", stop.Token))
                {
                    Assert.That(response.Version, Is.EqualTo(version));
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                    if (protocol == 1) Assert.That(response.Headers.ConnectionClose, Is.True, "Accepted H1 policy closes a 500 response.");
                    var bytes = await response.Content.ReadAsByteArrayAsync(stop.Token);
                    var body = WebServer.Utf8NoBomEncoding.GetString(bytes);
                    switch (policy)
                    {
                        case ResponsePolicy.Default:
                            Assert.That(bytes, Is.Empty, "The approved default must not disclose exception details.");
                            break;
                        case ResponsePolicy.Html:
                            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
                            Assert.That(body, Does.Contain(marker).And.Contain("System.InvalidOperationException"));
                            break;
                        case ResponsePolicy.Headers:
                            Assert.That(bytes, Is.Empty);
                            Assert.That(response.Headers.GetValues(ExceptionHandler.ExceptionTypeHeaderName),
                                Is.EqualTo(new[] { Uri.EscapeDataString(nameof(InvalidOperationException)) }));
                            Assert.That(response.Headers.GetValues(ExceptionHandler.ExceptionMessageHeaderName),
                                Is.EqualTo(new[] { Uri.EscapeDataString(marker) }));
                            break;
                        case ResponsePolicy.Custom:
                            Assert.That(body, Is.EqualTo("Explicit custom response."));
                            Assert.That(callbackException, Is.SameAs(original));
                            Assert.That(callbackCount, Is.EqualTo(1));
                            break;
                        case ResponsePolicy.ProblemDetails:
                            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
                            Assert.That(body, Does.Contain("500").And.Not.Contain(marker).And.Not.Contain(nameof(InvalidOperationException)));
                            break;
                    }

                    if (policy != ResponsePolicy.Headers)
                    {
                        Assert.That(response.Headers.Contains(ExceptionHandler.ExceptionTypeHeaderName), Is.False);
                        Assert.That(response.Headers.Contains(ExceptionHandler.ExceptionMessageHeaderName), Is.False);
                    }
                }

                using (var application = await client.GetAsync(prefix + "application", stop.Token))
                {
                    Assert.That(application.Version, Is.EqualTo(version));
                    Assert.That(application.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                    Assert.That(await application.Content.ReadAsStringAsync(stop.Token), Is.EqualTo(applicationMessage));
                }

                using (var after = await client.GetAsync(prefix + "healthy", stop.Token))
                {
                    Assert.That(after.Version, Is.EqualTo(version));
                    Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await after.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("healthy"));
                }

                Assert.That(peerPorts, Has.Count.EqualTo(4));
                var ports = peerPorts.ToArray();
                if (protocol == 1)
                {
                    Assert.That(ports[0], Is.EqualTo(ports[1]), "The error uses the initial healthy connection.");
                    Assert.That(ports[2], Is.EqualTo(ports[3]), "The replacement connection remains healthy and reusable.");
                    Assert.That(ports[1], Is.Not.EqualTo(ports[2]), "Accepted H1 status-500 close policy remains unchanged.");
                }
                else Assert.That(ports.Distinct().Count(), Is.EqualTo(1), "Multiplexed successors retain the physical connection.");
                source.Flush();
                var records = trace.Records.Where(record => record.Contains(marker, StringComparison.Ordinal)).ToArray();
                Assert.That(records, Is.Not.Empty, "Original exception details must remain available to diagnostics.");
                Assert.That(string.Concat(records), Does.Contain(nameof(InvalidOperationException)));
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                try
                {
                    await stop.CancelAsync();
                    if (running != null)
                    {
                        var cleanup = running.WaitAsync(TimeSpan.FromSeconds(10));
                        if (primaryFailure == null) await cleanup;
                        else
                        {
                            // Observe teardown independently without replacing the
                            // original disclosure/transport/assertion failure.
                            await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(11)));
                            if (cleanup.IsFaulted) TestContext.Out.WriteLine("Cleanup failure: " + cleanup.Exception);
                            else if (!cleanup.IsCompleted) TestContext.Out.WriteLine("Cleanup did not settle within its bound.");
                        }
                    }
                }
                finally
                {
                    source.Listeners.Remove(trace);
                    source.Switch.Level = previousLevel;
                }
            }
        }

        private static string QuicPrefix()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = (IPEndPoint)(socket.LocalEndPoint ?? throw new AssertionException("Missing UDP endpoint."));
            return $"https://127.0.0.1:{endpoint.Port}/";
        }

        private sealed class ErrorTrace : TraceListener
        {
            internal ConcurrentQueue<string> Records { get; } = new();

            public override void Write(string? message) => Records.Enqueue(message ?? string.Empty);

            public override void WriteLine(string? message) => Records.Enqueue(message ?? string.Empty);
        }
    }
}
