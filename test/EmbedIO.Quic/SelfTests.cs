using System.Security.Cryptography;
using System.Text;

namespace EmbedIO.Quic
{
    internal static class SelfTests
    {
        private static int _assertions;

        internal static void Run()
        {
            foreach (var number in new ulong[]
            {
                0,
                63,
                64,
                16383,
                16384,
                1073741823,
                1073741824,
                (1UL << 62) - 1
            })
            {
                using var wire = new MemoryStream();
                Wire.Integer(wire, number);
                var reader = new WireReader(wire.ToArray());
                Check(reader.Integer() == number && reader.Remaining == 0, "varint boundary");
            }

            var secret = Convert.FromHexString("c00cf151ca5be075ed0ebfb5c80323c42d6b7db67881289af4008f1f6c357aea");
            Check(
                Convert.ToHexString(PacketKeys.Expand(secret, "quic key", 16)) == "1F369613DD76D5467730EFCBE3B1A22D",
                "RFC 9001 A.1 key"
            );
            Check(
                Convert.ToHexString(PacketKeys.Expand(secret, "quic iv", 12)) == "FA044B2F42A3FD3B46FB255C",
                "RFC 9001 A.1 IV"
            );
            Check(
                Convert.ToHexString(PacketKeys.Expand(secret, "quic hp", 16)) == "9F50449E04A0E810283A1E9933ADEDD2",
                "RFC 9001 A.1 HP"
            );
            using var keys = new PacketKeys(secret);
            var header = Convert.FromHexString("43010203040000002a");
            var payload = Encoding.ASCII.GetBytes("independent QUIC packet protection");
            var packet = keys.Protect(header, 5, 42, payload);
            Check(
                keys
                    .Unprotect(packet, 5, 42, out var decoded)
                    .AsSpan()
                    .SequenceEqual(payload)
                    && decoded == 42,
                "packet round trip"
            );

            for (var i = 0; i < packet.Length; i++)
            {
                var altered = (byte[])packet.Clone();
                altered[i] ^= 1;
                Reject(() => keys.Unprotect(altered, 5, 42, out _), "packet tamper");
            }

            var stream = new Reassembly(8);
            stream.Add(4, [5, 6, 7, 8], true);
            Check(!stream.Complete, "gap remains incomplete");
            stream.Add(0, [1, 2, 3, 4]);
            stream.Add(2, [3, 4, 5]);
            Check(
                stream.Complete
                    && stream
                        .Consume()
                        .AsSpan()
                        .SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
                "reorder and overlap"
            );
            Reject(() => stream.Add(2, [9]), "conflicting overlap");
            Reject(() => stream.Add(7, [8, 9]), "bounded receive");
            Reject(() => stream.Add(0, [1], true), "final size consistency");
            var random = new Random(303);

            for (var i = 0; i < 10000; i++)
            {
                var input = new byte[random.Next(0, 18)];
                random.NextBytes(input);
                var cursor = new WireReader(input);

                try
                {
                    var value = cursor.Integer();
                    Check(value < 1UL << 62 && cursor.Remaining < input.Length, "bounded random varint");
                }

                catch (InvalidDataException)
                {
                    _assertions++;
                }
            }

            AsyncStreamTests.Run();
            StreamApplicationTests.Run();
            PacketNumberWindowTests.Run();
            RecoveryRetentionTests.Run();
            RecoveryTests.Run();
            BridgeTests.Run();
            KeyUpdateTests.Run();
            StreamWindowTests.Run();
            StreamCountTests.Run();
            StreamLifecycleTests.Run();
            LifetimeTests.Run();
            QuicFuzzChecks.SelfTest();
            var result = new
            {
                selfTests = "passed",
                primitiveAssertions = _assertions,
                recoveryAndBridge = "passed"
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        }

        private static void Reject(Action operation, string name)
        {
            try
            {
                operation();
            }

            catch (Exception error) when (error is InvalidDataException or CryptographicException)
            {
                _assertions++;
                return;
            }

            throw new InvalidOperationException("Expected rejection: " + name);
        }

        private static void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException("Failed: " + name);

            _assertions++;
        }
    }
}
