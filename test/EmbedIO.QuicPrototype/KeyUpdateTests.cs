using System.Security.Cryptography;

namespace EmbedIO.QuicPrototype
{
    internal static class KeyUpdateTests
    {
        internal static void Run()
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
    }
}
