using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    internal static class KeyUpdateTests
    {
        internal static void Run()
        {
            Protection();
            Generations();
            Policy();
            ReadOrdering();
            Limits();
            Mutate(
                Enumerable
                    .Range(0, 128)
                    .Select(i => (byte)i)
                    .ToArray()
            );
        }

        private static void Protection()
        {
            using var initial = new PacketKeys(new byte[32]);
            using var next = initial.Next();
            var header = Convert.FromHexString("47010203040000002a");
            var payload = new byte[32];
            var packet = next.Protect(header, 5, 42, payload);

            if (!initial.Phase(packet, 5)) throw new InvalidOperationException("Key update changed header protection.");

            if (
                !next
                    .Unprotect(packet, 5, 42, out var number, true)
                    .AsSpan()
                    .SequenceEqual(payload)
                || number != 42
            )
                throw new InvalidOperationException("Updated packet did not authenticate.");

            try
            {
                initial.Unprotect(packet, 5, 42, out _, true);
            }

            catch (CryptographicException)
            {
                return;
            }

            throw new InvalidOperationException("Old packet key authenticated updated ciphertext.");
        }

        private static void Generations()
        {
            var current = new PacketKeys(new byte[32]);

            try
            {
                for (var epoch = 0; epoch < 100; epoch++)
                {
                    var number = epoch < 50
                        ? (1UL << 32) + (ulong)epoch
                        : PacketNumberWindow.Maximum - 100 + (ulong)epoch;
                    var header = new byte[5];
                    header[0] = (epoch & 1) == 0 ? (byte)0x43 : (byte)0x47;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        header.AsSpan(1),
                        unchecked((uint)number)
                    );
                    var packet = current.Protect(header, 1, number, [(byte)epoch]);
                    var decoded = current.Decode(packet, 1, number);
                    QuicFuzzChecks.Require(
                        decoded.Number == number
                            && decoded.Phase == ((epoch & 1) != 0)
                            && current
                                .Unprotect(decoded, decoded.Phase)
                                .AsSpan()
                                .SequenceEqual([(byte)epoch]),
                        "repeated-key-and-packet-number-wrap"
                    );
                    var next = current.Next();
                    header[0] ^= 4;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        header.AsSpan(1),
                        unchecked((uint)(number + 1))
                    );
                    var stale = current.Decode(next.Protect(header, 1, number + 1, [1]), 1, number + 1);
                    QuicFuzzChecks.Require(
                        next
                            .Unprotect(stale, stale.Phase)
                            .AsSpan()
                            .SequenceEqual([(byte)1]),
                        "next-epoch-positive-oracle"
                    );

                    try
                    {
                        current.Unprotect(stale, stale.Phase);
                        throw new QuicFuzzFailure("old-epoch-authenticated-next");
                    }

                    catch (CryptographicException) { }

                    current.Dispose();
                    current = next;
                }
            }

            finally
            {
                current.Dispose();
            }
        }

        private static void Policy()
        {
            var state = new KeyUpdateState(4);
            QuicFuzzChecks.Require(!state.TryInitiate(4, 4, true, 0, 10), "update-needs-current-ack");
            state.Acknowledge(3, true, 0);
            QuicFuzzChecks.Require(!state.TryInitiate(4, 4, false, 0, 10), "update-needs-handshake");
            QuicFuzzChecks.Require(state.TryInitiate(4, 4, true, 0, 10), "first-local-update");
            QuicFuzzChecks.Require(state.WriteGeneration == 1 && state.ReadGeneration == 0, "local-one-ahead");
            state.Acknowledge(4, true, 1);
            QuicFuzzChecks.Require(!state.TryInitiate(5, 4, true, 20, 10), "update-needs-peer-keys");
            QuicFuzzChecks.Require(!state.PeerUpdated(1, 5), "peer-confirms-local-generation");
            state.Acknowledge(3, true, 2);
            state.Acknowledge(4, false, 2);
            QuicFuzzChecks.Require(!state.TryInitiate(5, 4, true, 20, 10), "old-ack-cannot-confirm-update");
            state.Acknowledge(4, true, 3);
            QuicFuzzChecks.Require(!state.TryInitiate(5, 4, true, 12, 10), "update-waits-three-pto");
            QuicFuzzChecks.Require(state.TryInitiate(5, 4, true, 13, 10), "second-local-update");
            QuicFuzzChecks.Require(!state.WritePhase && state.WriteGeneration == 2, "phase-wrap-keeps-generation");
            QuicFuzzChecks.Require(!state.PeerUpdated(2, 6) && state.PeerUpdated(3, 6), "peer-update-mirrored-once");
            QuicFuzzChecks.Require(state.WriteGeneration == 3 && state.ReadGeneration == 3, "peer-generations-equal");
        }

        private static void ReadOrdering()
        {
            var state = new KeyUpdateState();
            state.ReadCurrent(100);
            state.PeerUpdated(105, 0);
            state.ReadCurrent(102);
            state.ReadPrevious(101);
            QuicFuzzChecks.Require(state.ReadMinimum == 102, "reordered-current-lowers-key-boundary");
            RejectOrdering(() => state.ReadPrevious(103));
            RejectOrdering(() => state.ReadCurrent(100));
            RejectOrdering(() => state.PeerUpdated(105, 0));
            QuicFuzzChecks.Require(
                state.ReadGeneration == 1 && state.ReadMinimum == 102,
                "invalid-ordering-does-not-advance"
            );
        }

        private static void RejectOrdering(Action operation)
        {
            try
            {
                operation();
            }

            catch (KeyUpdateException)
            {
                return;
            }

            throw new QuicFuzzFailure("key-packet-order-not-rejected");
        }

        private static void ReadOrderingMutate(ReadOnlySpan<byte> data)
        {
            var state = new KeyUpdateState();
            var packets = new List<(ulong Epoch, ulong Number)>();
            var epoch = 0UL;

            for (var index = 0; index < Math.Min(data.Length, 128); index++)
            {
                var kind = data[index] % 3;
                var selected = kind == 0 ? epoch + 1 : kind == 1 || epoch == 0 ? epoch : epoch - 1;
                var number = (ulong)(data[index] + index % 5);
                var permitted = packets.All(
                    p => p.Epoch == selected || (p.Epoch < selected ? p.Number < number : p.Number > number)
                );
                var accepted = true;

                try
                {
                    if (selected > epoch) state.PeerUpdated(number, 0);

                    else if (selected == epoch) state.ReadCurrent(number);

                    else state.ReadPrevious(number);
                }

                catch (KeyUpdateException)
                {
                    accepted = false;
                }

                QuicFuzzChecks.Require(accepted == permitted, "key-order-independent-packet-oracle");

                if (permitted)
                {
                    packets.Add((selected, number));
                    epoch = Math.Max(epoch, selected);
                }

                QuicFuzzChecks.Require(state.ReadGeneration == epoch, "key-order-generation");
            }
        }

        private static void Limits()
        {
            using var keys = new PacketKeys(new byte[32], encryptionLimit: 8);
            var header = new byte[5];
            header[0] = 0x43;

            for (byte number = 0; number < 8; number++)
            {
                header[4] = number;
                keys.Protect(header, 1, number, [1]);
            }

            QuicFuzzChecks.Require(keys.Encryptions == 8 && !keys.CanEncrypt, "aead-counts-every-encryption");

            try
            {
                keys.Protect(header, 1, 8, [1]);
            }

            catch (InvalidOperationException)
            {
                using var fresh = keys.Next();
                QuicFuzzChecks.Require(fresh.Encryptions == 0 && fresh.CanEncrypt, "updated-aead-budget");
                return;
            }

            throw new QuicFuzzFailure("aead-limit-not-enforced");
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            var state = new KeyUpdateState(4);
            ReadOrderingMutate(data);
            var received = 0UL;
            var read = 0UL;
            var write = 0UL;
            var start = 0UL;
            var confirmed = false;
            var confirmedAt = 0.0;
            var encrypted = 0;
            ulong number = 0;
            var now = 0.0;

            foreach (var command in data[..Math.Min(data.Length, 128)])
            {
                now += command % 5;

                switch (command % 4)
                {
                    case 0:
                        encrypted++;
                        number++;
                        state.ReadCurrent(++received);
                        break;

                    case 1:
                        read++;
                        var mirrored = read > write;
                        QuicFuzzChecks.Require(state.PeerUpdated(++received, number) == mirrored, "update-model-peer");

                        if (mirrored)
                        {
                            write++;
                            start = number;
                            confirmed = false;
                            encrypted = 0;
                        }

                        break;

                    case 2:
                        var current = (command & 16) != 0;
                        var ack = (command & 32) != 0 ? number : start == 0 ? 0 : start - 1;
                        state.Acknowledge(ack, current, now);

                        if (current && read == write && ack >= start && !confirmed)
                        {
                            confirmed = true;
                            confirmedAt = now;
                        }

                        break;

                    case 3:
                        var handshake = (command & 8) != 0;
                        var expected = encrypted >= 4
                            && handshake
                            && read == write
                            && confirmed
                            && (write == 0 || now >= confirmedAt + 10);
                        QuicFuzzChecks.Require(
                            state.TryInitiate(number, encrypted, handshake, now, 10) == expected,
                            "update-model-local"
                        );

                        if (expected)
                        {
                            write++;
                            start = number;
                            confirmed = false;
                            encrypted = 0;
                        }

                        break;
                }

                QuicFuzzChecks.Require(
                    state.ReadGeneration == read
                        && state.WriteGeneration == write
                        && write >= read
                        && write - read <= 1
                        && state.WritePhase == ((write & 1) != 0),
                    "update-model-ownership"
                );
            }
        }
    }
}
