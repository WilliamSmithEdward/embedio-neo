using System.Net;

namespace EmbedIO.Quic
{
    internal static class ConnectionSetTests
    {
        internal static void Mutate(string library, string certificate, string key, int seed, int count)
        {
            using var owners = new ConnectionSet(library, certificate, key);
            var random = new Random(seed);
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            var now = 0.0;
            long received = 0;
            long sent = 0;

            for (var index = 0; index < count; index++)
            {
                now += random.Next(50);
                original[0] = (byte)random.Next(8);
                using var keys = PacketKeys.Initial(original, false);
                var bytes = new byte[random.Next(1, 80)];
                random.NextBytes(bytes);
                var packet = NativeFuzz.Initial(keys, original, client, (uint)index, index % 3 == 0 ? [1] : bytes);

                if (index % 5 == 0) packet[^1] ^= 1;

                var peer = new IPEndPoint(IPAddress.Loopback, 14000 + random.Next(3));
                received += packet.Length;
                var replies = owners.Receive(packet, peer, now);

                foreach (var reply in replies)
                {
                    QuicFuzzChecks.Require(reply.Peer.Equals(peer), "admission-response-owner");
                    sent += reply.Bytes.Length;
                }

                foreach (var reply in owners.Tick(now)) sent += reply.Bytes.Length;

                QuicFuzzChecks.Require(
                    owners.Active <= ConnectionSet.Capacity
                        && owners.Accepted - owners.Retired == owners.Active
                        && sent <= 3 * received,
                    "admission-resource-accounting"
                );
            }

            owners.Dispose();
            QuicFuzzChecks.Require(owners.Active == 0 && owners.Accepted == owners.Retired, "mutated-owners-retired");
        }

        private static void ExhaustOwner(string library, string certificate, string key)
        {
            using var owners = new ConnectionSet(library, certificate, key);
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            using var keys = PacketKeys.Initial(original, false);
            var first = NativeFuzz.Initial(keys, original, client, 0, [1]);
            var busy = new IPEndPoint(IPAddress.Loopback, 15000);
            var healthy = new IPEndPoint(IPAddress.Loopback, 15001);
            owners.Receive(first, busy, 0);

            for (uint number = 1; number <= 4096; number++)
                owners.Receive(NativeFuzz.Initial(keys, original, client, number, [1]), busy, 1);

            QuicFuzzChecks.Require(owners.Active == 1 && owners.FailedOwners == 0, "recovery-budget-closes-owner");
            owners.Receive(first, healthy, 1000);
            owners.Tick(3100);
            QuicFuzzChecks.Require(
                owners.Retired == 1 && owners.Active == 1 && owners.FailedOwners == 0,
                "recovery-budget-retires-only-owner"
            );
            var response = owners.Receive(NativeFuzz.Initial(keys, original, client, 1, [1]), healthy, 3101);
            QuicFuzzChecks.Require(
                response.Count == 1 && response[0].Peer.Equals(healthy),
                "recovery-budget-healthy-sibling"
            );
        }

        internal static void Run(string library, string certificate, string key)
        {
            ExhaustOwner(library, certificate, key);
            using var owners = new ConnectionSet(library, certificate, key);
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            using var keys = PacketKeys.Initial(original, false);
            var first = NativeFuzz.Initial(keys, original, client, 0, [1]);
            var peer = new IPEndPoint(IPAddress.Loopback, 12345);
            var replies = owners.Receive(first, peer, 0);
            QuicFuzzChecks.Require(owners.Active == 1 && replies.Count == 1, "admission-first");
            var response = new WireReader(replies[0].Bytes);
            response.Take(5);
            response.Take(response.Byte());
            var serverId = response
                .Take(response.Byte())
                .ToArray();
            owners.Receive(first, peer, 1);
            QuicFuzzChecks.Require(owners.Accepted == 1, "initial-alias-reuse");
            owners.Receive(first, new IPEndPoint(IPAddress.Loopback, 12346), 2);
            QuicFuzzChecks.Require(owners.Accepted == 2, "original-id-independent-peer");
            byte[] unknown =
            [
                0x40,
                .. serverId,
                .. new byte[20]
            ];
            owners.Receive(unknown, new IPEndPoint(IPAddress.Loopback, 12347), 3);
            QuicFuzzChecks.Require(owners.Active == 2, "issued-id-peer-binding");
            var damaged = (byte[])first.Clone();
            damaged[^1] ^= 1;
            owners.Receive(damaged, new IPEndPoint(IPAddress.Loopback, 12348), 4);
            QuicFuzzChecks.Require(owners.Accepted == 2, "invalid-initial-no-owner");

            byte[] otherId = [2, 2, 3, 4, 5, 6, 7, 8];
            using var otherKeys = PacketKeys.Initial(otherId, false);
            owners.Receive(NativeFuzz.Initial(otherKeys, otherId, client, 0, [1]), peer, 5);
            QuicFuzzChecks.Require(owners.Active == 3, "independent-connections-same-endpoint");

            // Move past the admission window, then fill the table with bounded handshakes.
            for (
                var index = 0;
                index < ConnectionSet.Capacity;
                index++
            )
                owners.Receive(first, new IPEndPoint(IPAddress.Loopback, 13000 + index), 1001);

            QuicFuzzChecks.Require(
                owners.Active == ConnectionSet.Capacity && owners.Peak == ConnectionSet.Capacity,
                "admission-capacity"
            );
            replies = owners.Receive(NativeFuzz.Initial(keys, original, client, 1, [1]), peer, 1002);
            QuicFuzzChecks.Require(replies.Count == 1, "existing-owner-survives-capacity");
            owners.Tick(20000);
            QuicFuzzChecks.Require(
                owners.Active == 0
                    && owners.Retired == ConnectionSet.Capacity
                    && owners.ExpiredHandshakes == ConnectionSet.Capacity,
                "handshake-deadline-releases-capacity"
            );
            replies = owners.Receive(first, peer, 20001);
            QuicFuzzChecks.Require(owners.Active == 1 && replies.Count == 1, "late-listener-admission");
            owners.Dispose();
            owners.Dispose();
            QuicFuzzChecks.Require(
                owners.Active == 0 && owners.Accepted == owners.Retired,
                "admission-once-only-disposal"
            );
        }
    }
}
