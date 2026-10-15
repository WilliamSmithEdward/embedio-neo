using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed class ExtendedConnectAcceptanceTest
    {
        public enum Commitment { Body, Flush, Empty, Dispose, StatusEdit, NoContent, RepeatedWrite }

        public static IEnumerable<TestCaseData> UnacceptedCases()
        {
            foreach (var tls in new[] { false, true })
                foreach (var protocol in new[] { "example-tunnel", "websocket" })
                    foreach (var commitment in Enum.GetValues<Commitment>())
                        yield return new TestCaseData(tls, protocol, commitment);
        }

        [TestCaseSource(nameof(UnacceptedCases))]
        public async Task SuccessfulOutputRequiresExplicitAcceptance(bool tls, string protocol, Commitment commitment)
            => await VerifyAsync(tls, protocol, 501, context => WriteUnacceptedAsync(context, commitment), "");

        internal static async Task WriteUnacceptedAsync(IHttpContext context, Commitment commitment)
        {
            switch (commitment)
            {
                case Commitment.Body:
                    await context.Response.OutputStream.WriteAsync("ordinary-body"u8.ToArray(), context.CancellationToken);
                    break;
                case Commitment.Flush:
                    await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                    await context.Response.OutputStream.WriteAsync("after-flush"u8.ToArray(), context.CancellationToken);
                    break;
                case Commitment.Empty: break;
                case Commitment.Dispose: context.Response.OutputStream.Dispose(); break;
                case Commitment.StatusEdit:
                    context.Response.StatusCode = 403;
                    context.Response.StatusCode = 201;
                    await context.Response.OutputStream.WriteAsync("edited-status"u8.ToArray(), context.CancellationToken);
                    break;
                case Commitment.NoContent: context.Response.StatusCode = 204; break;
                case Commitment.RepeatedWrite:
                    await context.Response.OutputStream.WriteAsync("first"u8.ToArray(), context.CancellationToken);
                    await context.Response.OutputStream.WriteAsync("second"u8.ToArray(), context.CancellationToken);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(commitment));
            }
        }

        [TestCase(false, 400)]
        [TestCase(false, 401)]
        [TestCase(false, 403)]
        [TestCase(false, 404)]
        [TestCase(false, 501)]
        [TestCase(true, 400)]
        [TestCase(true, 401)]
        [TestCase(true, 403)]
        [TestCase(true, 404)]
        [TestCase(true, 501)]
        public async Task ExplicitApplicationRejectionPreservesStatusAndBody(bool tls, int status)
            => await VerifyAsync(tls, "example-tunnel", status, async context =>
            {
                context.Response.StatusCode = status;
                await context.SendStringAsync("application-rejection", "text/plain", WebServer.Utf8NoBomEncoding);
            }, "application-rejection");

        private static async Task VerifyAsync(bool tls, string protocol, int status,
            Func<IHttpContext, Task> handler, string expectedBody)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var prefix = HttpsSmoke.GetUrl();
            if (!tls) prefix = prefix.Replace("https:", "http:", StringComparison.Ordinal);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var calls = 0;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectPort = 0;
            var healthyPort = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Any, async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/healthy")
                    {
                        healthyPort = context.RemoteEndPoint.Port;
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }
                    connectPort = context.RemoteEndPoint.Port;
                    Interlocked.Increment(ref calls);
                    context.OnClose(_ => closed.TrySetResult());
                    await handler(context);
                });
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, change) =>
            {
                if (change.NewState == WebServerState.Listening) listening.TrySetResult();
            };
            var running = server.RunAsync(stop.Token);
            using var client = HttpsSmoke.CreateClient(certificate, new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 });
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            try
            {
                var first = await Task.WhenAny(listening.Task, running).WaitAsync(stop.Token);
                if (first == running) { await running; Assert.Fail("Listener stopped before becoming ready."); }
                await listening.Task.WaitAsync(stop.Token);
                using (var request = new HttpRequestMessage(HttpMethod.Connect, prefix + "carrier")
                { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact })
                {
                    request.Headers.Protocol = protocol;
                    if (protocol == "websocket") request.Headers.TryAddWithoutValidation("sec-websocket-version", "13");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                    Assert.That((int)response.StatusCode, Is.EqualTo(status), "Ordinary success must not negotiate an extension.");
                    Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo(expectedBody));
                }
                await closed.Task.WaitAsync(stop.Token);
                Assert.That(await client.GetStringAsync(prefix + "healthy", stop.Token), Is.EqualTo("healthy"));
                Assert.That(healthyPort, Is.EqualTo(connectPort), "Rejection must preserve the same connection.");
                Assert.That(calls, Is.EqualTo(1), "Extended CONNECT must reach application dispatch exactly once.");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
