using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    // Retry and address-validation tokens (RFC 9000 8.1.2, RFC 9001 5.8).
    internal static class RetryTests
    {
        private sealed class ControlledTls : TlsSession
        {
            internal override void Add(int level, ulong offset, ReadOnlySpan<byte> bytes) { }

            internal override void Pump() { }

            public override void Dispose() => ReleaseKeys();
        }

        private static readonly byte[] Client = [9, 10, 11, 12, 13, 14, 15, 16];

        internal static async Task RunAsync()
        {
            IntegrityTagMatchesTheRfcVector();
            TokensBindAddressDestinationAndTime();
            RetryLiftsTheAmplificationLimit();
            await DispatcherAsync().ConfigureAwait(false);
        }

        // A redeemed Retry token proves the client's address, so the server's first flight is
        // not held to three times the client's 1200-byte Initial (RFC 9000 8.1); without one it is.
        private static void RetryLiftsTheAmplificationLimit()
        {
            var held = FirstFlight(null);
            var free = FirstFlight(new QuicRetry([1, 2, 3, 4, 5, 6, 7, 8]));
            Check(held > 0 && held <= 3 * 1200, "first flight held to three times the Initial without a Retry");
            Check(free > 3 * 1200, "first flight not held after a Retry");
        }

        private static int FirstFlight(QuicRetry? retry)
        {
            byte[] destination = [21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36];
            var initial = Initial(destination, []);
            using var connection = new QuicConnection(
                initial,
                static _ => new FlightTls(),
                static (port, _) => new StreamEchoApplication(port),
                0,
                retry: retry
            );
            connection.BeginRouting(
                new QuicConnectionEndpoints(new IPEndPoint(IPAddress.Loopback, 17201), new IPEndPoint(IPAddress.Loopback, 443))
            );
            return connection
                .Receive(initial, 0)
                .Sum(static packet => packet.Length);
        }

        // Answers the client's Initial with 9,000 bytes of Initial-level handshake data, like a
        // server flight that carries a long certificate chain.
        private sealed class FlightTls : TlsSession
        {
            private bool _sent;

            internal override void Add(int level, ulong offset, ReadOnlySpan<byte> bytes) { }

            internal override void Pump()
            {
                if (_sent) return;

                _sent = true;
                Output.Enqueue((0, new byte[9000]));
            }

            public override void Dispose() => ReleaseKeys();
        }

        // RFC 9001 appendix A.4: the Retry for original destination 8394c8f03e515708.
        private static void IntegrityTagMatchesTheRfcVector()
        {
            var retry = Convert.FromHexString("ff000000010008f067a5502a4262b5746f6b656e04a265ba2eff4d829058fb3f0f2496ba");
            Span<byte> tag = stackalloc byte[RetryPacket.TagLength];
            RetryPacket.Tag(retry.AsSpan(0, retry.Length - 16), Convert.FromHexString("8394c8f03e515708"), tag);
            Check(tag.SequenceEqual(retry.AsSpan(retry.Length - 16)), "integrity tag matches RFC 9001 A.4");
            // The vector sets the four unused bits of the first byte and the tag covers them, so
            // the built packet matches it between the first byte and the tag, and carries a tag
            // valid for its own bytes.
            var built = RetryPacket.Create([], Convert.FromHexString("f067a5502a4262b5"), "token"u8, Convert.FromHexString("8394c8f03e515708"));
            Check(
                built.Length == retry.Length && built.AsSpan(1, built.Length - 17).SequenceEqual(retry.AsSpan(1, retry.Length - 17)),
                "Retry layout matches RFC 9001 A.4 apart from unused bits"
            );
            RetryPacket.Tag(built.AsSpan(0, built.Length - 16), Convert.FromHexString("8394c8f03e515708"), tag);
            Check(built[0] == 0xf0 && tag.SequenceEqual(built.AsSpan(built.Length - 16)), "built Retry carries its own integrity tag");
        }

        private static void TokensBindAddressDestinationAndTime()
        {
            using var tokens = new RetryTokens();
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] retry = [21, 22, 23, 24, 25, 26, 27, 28];
            var client = IPAddress.Loopback;
            var token = tokens.Issue(original, retry, client, 1000);
            Check(Redeemed(tokens, token, client, retry, 1000)?.SequenceEqual(original) == true, "token redeems at its address");
            Check(
                Redeemed(tokens, token, IPAddress.Parse("::ffff:127.0.0.1"), retry, 1000)?.SequenceEqual(original) == true,
                "IPv4-mapped address is the same client"
            );
            Check(
                tokens.Redeem(token, IPAddress.Parse("127.0.0.2"), retry, 1000, out _) == RetryTokens.Outcome.Unknown,
                "another address cannot open the token"
            );
            Check(tokens.Redeem(token, client, original, 1000, out _) == RetryTokens.Outcome.Invalid, "another destination is invalid");
            Check(
                tokens.Redeem(token, client, retry, 1000 + RetryTokens.LifetimeMilliseconds + 1, out _) == RetryTokens.Outcome.Invalid,
                "expired token invalid"
            );
            Check(tokens.Redeem(token, client, retry, 999, out _) == RetryTokens.Outcome.Invalid, "token from the future invalid");
            var tampered = token.ToArray();
            tampered[^1] ^= 1;
            Check(tokens.Redeem(tampered, client, retry, 1000, out _) == RetryTokens.Outcome.Unknown, "tampered token unknown");
            using var other = new RetryTokens();
            Check(other.Redeem(token, client, retry, 1000, out _) == RetryTokens.Outcome.Unknown, "another listener's token unknown");
            var second = tokens.Issue(original, retry, client, 1000);
            Check(!second.AsSpan(0, 12).SequenceEqual(token.AsSpan(0, 12)), "each token has its own nonce");
        }

        private static byte[]? Redeemed(RetryTokens tokens, byte[] token, IPAddress client, byte[] destination, double now) =>
            tokens.Redeem(token, client, destination, now, out var original) == RetryTokens.Outcome.Redeemed ? original : null;

        private static async Task DispatcherAsync()
        {
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            var peer = new IPEndPoint(IPAddress.Loopback, 17101);
            QuicRetry? admitted = null;
            byte[]? parameters = null;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, now, owner, _) =>
                {
                    admitted = owner.Retry;
                    return new QuicConnection(
                        packet,
                        local =>
                        {
                            parameters = local;
                            return new ControlledTls();
                        },
                        static (port, _) => new StreamEchoApplication(port),
                        now,
                        retry: owner.Retry
                    );
                },
                static () => 0,
                capacity: 4,
                workers: 1,
                refuseConnections: true,
                validateAddresses: true
            );

            // A first Initial without a token gets a Retry and no connection.
            Check(!dispatcher.Receive(Initial(original, []), peer), "tokenless Initial gets no connection");
            Check(dispatcher.RetriesSent == 1 && dispatcher.Active == 0 && admitted == null, "Retry sent without state");
            var retry = await NextAsync(dispatcher).ConfigureAwait(false);
            Check((retry[0] & 0xf0) == 0xf0 && BinaryPrimitives.ReadUInt32BigEndian(retry.AsSpan(1)) == 1, "Retry header");
            Check(retry.AsSpan(6, retry[5]).SequenceEqual(Client), "Retry addresses the client's source ID");
            var retryIdLength = retry[6 + retry[5]];
            var retryId = retry.AsSpan(7 + retry[5], retryIdLength).ToArray();
            var token = retry.AsSpan(7 + retry[5] + retryIdLength, retry.Length - 16 - (7 + retry[5] + retryIdLength)).ToArray();
            var tag = new byte[RetryPacket.TagLength];
            RetryPacket.Tag(retry.AsSpan(0, retry.Length - 16), original, tag);
            Check(
                tag.AsSpan().SequenceEqual(retry.AsSpan(retry.Length - 16)) && retryId.Length == QuicConnectionIdIssuer.Length,
                "Retry integrity tag and ID"
            );

            // A token this listener did not seal, such as another server's, gets a Retry like a
            // missing one (RFC 9000 8.1.3).
            Check(!dispatcher.Receive(Initial(retryId, RandomNumberGenerator.GetBytes(60)), peer), "unknown token gets no connection");
            Check(
                dispatcher.RetriesSent == 2 && dispatcher.InvalidTokens == 0 && dispatcher.Active == 0,
                "unknown token answered with a Retry"
            );
            var again = await NextAsync(dispatcher).ConfigureAwait(false);
            Check((again[0] & 0xf0) == 0xf0, "second Retry sent");

            // Its own token that fails validation, here sent to the wrong destination, is closed
            // with INVALID_TOKEN, since the client will not accept a second Retry.
            Check(!dispatcher.Receive(Initial(original, token), peer), "misdirected token refused");
            Check(dispatcher.InvalidTokens == 1 && dispatcher.Active == 0, "invalid token counted without state");
            var close = await NextAsync(dispatcher).ConfigureAwait(false);
            Check(CloseCode(close, original) == ConnectionRefusal.InvalidToken, "INVALID_TOKEN close");

            // The returned token admits the connection, whose parameters name both IDs (RFC 9000 7.3).
            Check(dispatcher.Receive(Initial(retryId, token), peer), "token admits the connection");
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while (parameters == null)
                    await Task
                        .Delay(1, stop.Token)
                        .ConfigureAwait(false);

            var local = parameters ?? throw new InvalidOperationException("No transport parameters.");
            Check(admitted?.OriginalDestinationId.SequenceEqual(original) == true, "connection carries the original ID");
            Check(Parameter(local, 0)?.SequenceEqual(original) == true, "original_destination_connection_id");
            Check(Parameter(local, 16)?.SequenceEqual(retryId) == true, "retry_source_connection_id");
        }

        // A client Initial to the destination, with the token, padded to 1200 bytes.
        private static byte[] Initial(byte[] destination, byte[] token)
        {
            using var keys = PacketKeys.Initial(destination, false);
            using var header = new MemoryStream();
            header.Write([0xc3, 0, 0, 0, 1]);
            header.WriteByte((byte)destination.Length);
            header.Write(destination);
            header.WriteByte((byte)Client.Length);
            header.Write(Client);
            Wire.Integer(header, (ulong)token.Length);
            header.Write(token);
            var payload = new byte[1200 - (int)header.Length - 2 - 4 - 16];
            payload[0] = 1;
            Wire.Integer(header, (ulong)(4 + payload.Length + 16));
            var offset = (int)header.Length;
            header.Write([0, 0, 0, 0]);
            return keys.Protect(header.ToArray(), offset, 0, payload);
        }

        private static ulong CloseCode(byte[] datagram, byte[] destination)
        {
            var reader = new WireReader(datagram);
            reader.Byte();
            reader.Take(4);
            reader.Take(reader.Byte());
            reader.Take(reader.Byte());
            reader.Take(reader.Length(4096));
            var length = reader.Length(datagram.Length);
            var offset = datagram.Length - reader.Remaining;
            using var keys = PacketKeys.Initial(destination, true);
            var frame = new WireReader(keys.Unprotect(datagram.AsSpan(0, offset + length), offset, 0, out _));
            return frame.Integer() == 0x1c ? frame.Integer() : ulong.MaxValue;
        }

        private static byte[]? Parameter(byte[] parameters, ulong id)
        {
            var reader = new WireReader(parameters);

            while (reader.Remaining > 0)
            {
                var current = reader.Integer();
                var value = reader.Take(reader.Length(parameters.Length));

                if (current == id) return value.ToArray();
            }

            return null;
        }

        private static async Task<byte[]> NextAsync(QuicConnectionDispatcher dispatcher)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Check(await dispatcher.WaitForOutputAsync(stop.Token).ConfigureAwait(false), "output ready");
            byte[] sent = [];
            Check(
                await dispatcher
                    .SendOneAsync(output =>
                    {
                        sent = output.Bytes;
                        return ValueTask.CompletedTask;
                    })
                    .ConfigureAwait(false),
                "output sent"
            );
            return sent;
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("QUIC Retry regression: " + name);
        }
    }
}
