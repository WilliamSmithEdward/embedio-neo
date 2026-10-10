using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1RejectedConnectTest
    {
        [TestCase("CONNECT", 401, false, false)]
        [TestCase("CONNECT", 403, false, false)]
        [TestCase("CONNECT", 407, false, false)]
        [TestCase("CONNECT", 307, false, false)]
        [TestCase("CONNECT", 404, false, false)]
        [TestCase("CONNECT", 501, false, false)]
        [TestCase("GET", 401, true, false)]
        [TestCase("GET", 403, true, false)]
        [TestCase("connect", 403, true, false)]
        [TestCase("CONNECT", 403, false, true)]
        [TestCase("GET", 403, true, true)]
        public async Task RejectedConnectDoesNotDispatchOptimisticSuccessor(string method, int status, bool persistent, bool lateHeaderChange)
        {
            var prefix = Resources.GetServerAddress();
            var firstCalls = 0;
            var successorCalls = 0;
            var changedHeaders = 0;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix).WithAction("/", HttpVerbs.Any, async context =>
            {
                if (context.Request.Url.AbsolutePath == "/next")
                {
                    Interlocked.Increment(ref successorCalls);
                    await context.SendStringAsync("successor", "text/plain", WebServer.Utf8NoBomEncoding);
                    return;
                }
                Interlocked.Increment(ref firstCalls);
                context.Response.StatusCode = status;
                context.Response.KeepAlive = true;
                if (lateHeaderChange)
                {
                    var bytes = Encoding.ASCII.GetBytes("rejected");
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, stop.Token);
                    await context.Response.OutputStream.FlushAsync(stop.Token);
                    context.Response.Headers["Connection"] = "keep-alive";
                    Interlocked.Increment(ref changedHeaders);
                }
                else await context.SendStringAsync("rejected", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", new Uri(prefix).Port, stop.Token);
                // Valid CONNECT uses authority-form; method lookalikes and GET
                // retain origin-form. Rejection must still discard optimistic bytes.
                var upgrade = method == "GET" ? "Connection: Upgrade\r\nUpgrade: websocket\r\n" : string.Empty;
                var target = method == "CONNECT" ? new Uri(prefix).Authority : "/proxy";
                var wire = method + " " + target + " HTTP/1.1\r\nHost: " + new Uri(prefix).Authority + "\r\n" + upgrade + "\r\n"
                    + "GET /next HTTP/1.1\r\nHost: " + new Uri(prefix).Authority + "\r\nConnection: close\r\n\r\n";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(wire), stop.Token);
                using var output = new MemoryStream();
                await client.GetStream().CopyToAsync(output, stop.Token);
                var response = Encoding.ASCII.GetString(output.ToArray());
                Assert.That(response, Does.StartWith("HTTP/1.1 " + status + " "));
                Assert.That(firstCalls, Is.EqualTo(1));
                Assert.That(changedHeaders, Is.EqualTo(lateHeaderChange ? 1 : 0));
                Assert.That(successorCalls, Is.EqualTo(persistent ? 1 : 0));
                if (persistent) Assert.That(response, Does.Contain("successor"));
                else
                {
                    Assert.That(response, Does.Contain("Connection: close\r\n"));
                    Assert.That(response, Does.Not.Contain("successor"));
                    Assert.That(response.IndexOf("HTTP/1.1 ", 1, StringComparison.Ordinal), Is.EqualTo(-1));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
