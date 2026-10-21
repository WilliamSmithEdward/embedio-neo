using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using EmbedIO.Net.Internal.Http2;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    // Exercises the library's actual HTTP/3 protocol with the replacement engine.
    // Production listener configuration and controller dispatch remain separate work.
    internal sealed class ProductionHttp3Application
    {
        private int _completed;

        internal int Completed => Volatile.Read(ref _completed);

        internal Task RunAsync(AsyncStreamApplication application, QuicConnectionOwner owner, CancellationToken token)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return Http3QuicConnection.RunTransportForListenerAsync(
                    new EmbedIOQuicTransportConnection(application, owner),
                    DispatchAsync,
                    token,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2)
                );

            throw new PlatformNotSupportedException("HTTP/3 validation requires Windows, Linux or macOS.");
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private async Task DispatchAsync(Http3QuicExchange exchange)
        {
            var token = exchange.CancellationToken;
            var path = exchange.Request.Path;
            byte[] response;
            var status = 200;

            if (path == "/timeout")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

                try
                {
                    var buffer = new byte[1024];

                    while (
                        await exchange.InputStream
                            .ReadAsync(buffer, linked.Token)
                            .ConfigureAwait(false)
                        != 0
                    ) { }

                    throw new InvalidOperationException("Timeout fixture received a complete upload.");
                }

                catch (OperationCanceledException) when (
                    timeout.IsCancellationRequested && !token.IsCancellationRequested
                )
                {
                    status = 408;
                    response = Encoding.UTF8.GetBytes("complete timeout response");
                }
            }

            else if (path == "/echo")
            {
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                int count;

                while (
                    (count = await exchange.InputStream
                        .ReadAsync(buffer, token)
                        .ConfigureAwait(false))
                    != 0
                )
                {
                    if (body.Length + count > 262144) throw new InvalidDataException("Fixture upload limit exceeded.");

                    body.Write(buffer, 0, count);
                }

                response = body.ToArray();
            }

            else response = Encoding.UTF8.GetBytes("production HTTP/3");

            await exchange
                .SendHeadersAsync(
                    [
                        new HpackField(":status", status.ToString(CultureInfo.InvariantCulture)),
                        new HpackField("content-length", response.Length.ToString(CultureInfo.InvariantCulture))
                    ],
                    false,
                    token
                )
                .ConfigureAwait(false);
            await exchange
                .WriteAsync(response, 0, response.Length, true, token)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _completed);
        }
    }
}
