using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    // Negotiation borrows the transport and the session's read buffer. It never
    // closes either, and every byte read remains available to the chosen protocol.
    internal static class TcpProtocolInput
    {
        private static readonly byte[] Http2Preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

        internal readonly struct Selection
        {
            internal Selection(bool http2, int count, bool buffered)
            { IsHttp2 = http2; Count = count; Buffered = buffered; }
            internal bool IsHttp2 { get; }
            internal int Count { get; }
            internal bool Buffered { get; }
        }

        internal static async Task<Selection> ReadAsync(Stream transport, byte[] input,
            bool buffered, bool initial, bool secure, X509Certificate? certificate)
        {
            if (transport is SslStream tls && !tls.IsAuthenticated)
            {
                var serverCertificate = certificate ?? throw new InvalidOperationException("The HTTPS listener has no certificate.");
#if NET10_0_OR_GREATER
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    EnabledSslProtocols = SslProtocols.None,
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
                }).ConfigureAwait(false);
                if (tls.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
                {
                    if (tls.SslProtocol != SslProtocols.Tls12 && tls.SslProtocol != SslProtocols.Tls13)
                        throw new AuthenticationException("HTTP/2 requires TLS 1.2 or later.");
                    return new Selection(true, 0, false);
                }
#else
                await tls.AuthenticateAsServerAsync(serverCertificate, false, SslProtocols.None, false).ConfigureAwait(false);
#endif
            }

            if (buffered) return new Selection(false, 0, true);
            var count = await transport.ReadAsync(input, 0, input.Length).ConfigureAwait(false);
            if (secure || !initial || count == 0 || input[0] != Http2Preface[0])
                return new Selection(false, count, false);

            // Compare each candidate preface byte once. A mismatch hands the entire
            // accumulated input to HTTP/1, including the mismatching byte.
            var checkedBytes = 0;
            while (true)
            {
                var available = Math.Min(count, Http2Preface.Length);
                while (checkedBytes < available)
                {
                    if (input[checkedBytes] != Http2Preface[checkedBytes])
                        return new Selection(false, count, false);
                    checkedBytes++;
                }
                if (checkedBytes == Http2Preface.Length) return new Selection(true, count, false);
                var read = await transport.ReadAsync(input, count, input.Length - count).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Incomplete protocol preface.");
                count += read;
            }
        }
    }
}
