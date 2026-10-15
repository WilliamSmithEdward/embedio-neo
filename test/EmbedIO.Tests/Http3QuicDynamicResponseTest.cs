using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class Http3QuicTest
    {
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ExerciseDynamicResponse(QuicConnection client, object session, string scenario, List<QuicStream> peers, CancellationToken token)
        {
            object Field(string name) => session.GetType().GetField(name, Flags)?.GetValue(session) ?? throw new AssertionException("Missing connection state.");
            long Property(object value, string name) => Convert.ToInt64(value.GetType().GetProperty(name, Flags)?.GetValue(value));
            while (Property(Field("_peer"), "MaximumTableCapacity") == 0) await Task.Delay(1, token);
            var payload = Enumerable.Repeat((byte)'a', 37).ToArray();
            async Task<byte[]> Request()
            {
                await using var request = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                var wire = Convert.FromHexString("01100000D4D7C150096C6F63616C686F73740025").Concat(payload).ToArray();
                await request.WriteAsync(wire, true, token);
                using var response = new MemoryStream();
                await request.CopyToAsync(response, token);
                var bytes = response.ToArray();
                Assert.That(bytes[0], Is.EqualTo(1));
                var length = bytes[1];
                Assert.That(length, Is.LessThan(64));
                Assert.That(bytes.Skip(length + 2).ToArray(), Is.EqualTo(new byte[] { 0, 37 }.Concat(payload).ToArray()));
                return bytes.Skip(2).Take(length).ToArray();
            }
            Assert.That((await Request())[0], Is.Zero);
            if (scenario == "dynamic-no-credit")
            {
                for (var i = 0; i < 8; ++i) Assert.That((await Request())[0], Is.Zero);
                return; // No third outgoing stream credit; responses still finish.
            }
            QuicStream encoder;
            while (true)
            {
                encoder = await client.AcceptInboundStreamAsync(token);
                peers.Add(encoder);
                var type = new byte[1]; await encoder.ReadExactlyAsync(type, token);
                if (type[0] == 2) break;
            }
            var instructions = new byte[7];
            await encoder.ReadExactlyAsync(instructions, token);
            Assert.That(Convert.ToHexString(instructions), Is.EqualTo("3FE11FC4023337"));
            if (scenario == "dynamic-encoder-reset")
            {
                encoder.Abort(QuicAbortDirection.Read, 0x10c);
                var failure = await Assert.ThrowsAsync<QuicException>(async () => await client.AcceptInboundStreamAsync(token));
                Assert.That(failure?.ApplicationErrorCode, Is.EqualTo(0x104));
                return;
            }
            var feedback = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token);
            peers.Add(feedback);
            await feedback.WriteAsync(new byte[] { 3, 1 }, token);
            var state = Field("_encoderFeedback");
            while (Property(state, "KnownReceivedCount") != 1) await Task.Delay(1, token);
            Assert.That(Convert.ToHexString(await Request()), Is.EqualTo("0200D980"));
            Assert.That(Property(state, "PendingSections"), Is.EqualTo(1));
            await feedback.WriteAsync(new byte[] { 0x84 }, token);
            while (Property(state, "PendingSections") != 0) await Task.Delay(1, token);
            Assert.That(Convert.ToHexString(await Request()), Is.EqualTo("0200D980"));
            await feedback.WriteAsync(new byte[] { 0x48 }, token);
            while (Property(state, "PendingSections") != 0) await Task.Delay(1, token);
            Assert.That(Property(state, "PotentiallyBlockedStreams"), Is.Zero);
        }
    }
}
