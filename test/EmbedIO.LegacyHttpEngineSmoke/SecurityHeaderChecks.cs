using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Security;

public static partial class Program
{
    private static async Task VerifySecurityHeadersAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var url = "http://127.0.0.1:" + port + "/";
        var policy = new SecurityHeadersPolicy("script-src 'nonce-{nonce}'", hstsMaxAge: TimeSpan.FromSeconds(42));
        using var server = new WebServer(o =>
        {
            o.WithUrlPrefix(url);
            o.SecurityHeaders = policy;
        }).WithModule(new ActionModule(c => c.SendStringAsync(c.GetSecurityHeadersNonce(), "text/plain", WebServer.Utf8NoBomEncoding)));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = server.RunAsync(stop.Token);

        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler);
            string? previous = null;

            for (var i = 0; i < 3; i++)
            {
                using var response = await client
                    .GetAsync(url)
                    .ConfigureAwait(false);
                var nonce = await response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false);
                Check(response.StatusCode == HttpStatusCode.OK, "Security-header status.");
                Check(
                    Convert.FromBase64String(nonce).Length == 32 && previous != nonce,
                    "Fresh legacy cryptographic nonce."
                );
                Check(
                    response.Headers
                        .GetValues("Content-Security-Policy")
                        .Single()
                        == "script-src 'nonce-" + nonce + "'",
                    "Legacy nonce header/body binding."
                );
                Check(response.Headers.CacheControl?.NoStore == true, "Legacy no-store policy.");
                Check(!response.Headers.Contains("Strict-Transport-Security"), "HTTP cannot emit HSTS.");
                previous = nonce;
            }
        }

        finally
        {
            stop.Cancel();
            await running.ConfigureAwait(false);
        }
    }
}
