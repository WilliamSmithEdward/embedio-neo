using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    // Session-ticket keys on a manual clock: the first key, rotation after the period, an old
    // key that opens tickets until the period plus the lifetime, retirement after an idle
    // spell, independent copies, disposal with the last credential reference, concurrent use
    // and the constructor's bounds. verify_session_tickets.py covers the keys through Schannel
    // and OpenSSL.
    internal static class SessionTicketKeyTests
    {
        private const int Key = SessionTicketKeys.KeyLength;

        internal static void Run()
        {
            Rotation();
            Bounds();
            Ownership();
            Concurrency();
        }

        private static void Rotation()
        {
            long now = 0;
            using var keys = new SessionTicketKeys(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), () => now);
            var first = keys.Copy(out var version);
            Check(first.Length == Key && version != 0, "one key at first");
            var again = keys.Copy(out var unchanged);
            Check(again.AsSpan().SequenceEqual(first) && unchanged == version, "one key within the period");
            again.AsSpan().Clear();
            Check(keys.Copy(out _).AsSpan().SequenceEqual(first), "a copy is the caller's own");

            now = 10_000;
            var rotated = keys.Copy(out var next);
            Check(
                rotated.Length == 2 * Key
                    && next != version
                    && rotated.AsSpan(Key).SequenceEqual(first)
                    && !rotated
                        .AsSpan(0, SessionTicketKeys.NameLength)
                        .SequenceEqual(first.AsSpan(0, SessionTicketKeys.NameLength)),
                "a new key with its own name encrypts after the period, and the old one still opens"
            );

            now = 29_999;
            var three = keys.Copy(out _);
            Check(
                three.Length == 3 * Key
                    && three.AsSpan(Key, 2 * Key).SequenceEqual(rotated)
                    && three.AsSpan(2 * Key).SequenceEqual(first),
                "a key opens tickets until it is older than the period plus the lifetime"
            );

            now = 30_000;
            var retired = keys.Copy(out _);
            Check(
                retired.Length == 2 * Key && retired.AsSpan().SequenceEqual(three.AsSpan(0, 2 * Key)),
                "the oldest key retires once older than the period plus the lifetime"
            );

            now = 1_000_000;
            var idle = keys.Copy(out _);
            Check(
                idle.Length == Key && !idle.AsSpan().SequenceEqual(three.AsSpan(0, Key)),
                "after an idle spell every key has retired and a new one encrypts"
            );
        }

        private static void Bounds()
        {
            using (var defaults = new SessionTicketKeys())
                Check(defaults.Lifetime == TimeSpan.FromHours(2), "tickets live two hours by default");

            foreach (
                var (period, lifetime) in new[]
                {
                    (TimeSpan.FromMilliseconds(999), TimeSpan.FromSeconds(1)),
                    (TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(999)),
                    (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)),
                    (TimeSpan.FromDays(7), TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1))
                }
            )
            {
                var refused = false;

                try
                {
                    using var _ = new SessionTicketKeys(period, lifetime);
                }

                catch (ArgumentOutOfRangeException)
                {
                    refused = true;
                }

                Check(refused, "a period or lifetime outside the bounds is refused");
            }

            long now = 0;
            using var widest = new SessionTicketKeys(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), () => now);

            for (now = 0; now <= 10_000; now += 1_000)
                Check(widest.Copy(out _).Length <= SessionTicketKeys.Capacity * Key, "keys stay within the capacity");
        }

        // The credential owns its keys: the last release disposes them, and a disposed set
        // gives no copies.
        private static void Ownership()
        {
            var keys = new SessionTicketKeys();
            var holder = new Holder(keys);
            holder.Retain();
            Check(holder.Release() == 0 && keys.Copy(out _).Length == Key, "keys outlive all but the last release");
            Check(holder.Release() == 0 && holder.Frees == 1, "the last release frees the credential");
            var disposed = false;

            try
            {
                keys.Copy(out _);
            }

            catch (ObjectDisposedException)
            {
                disposed = true;
            }

            Check(disposed, "the last release disposes the credential's keys");
            keys.Dispose();
        }

        private static void Concurrency()
        {
            long now = 0;
            using var keys = new SessionTicketKeys(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), () => Interlocked.Read(ref now));
            var invalid = 0;
            Parallel.For(
                0,
                8,
                _ =>
                {
                    for (var copy = 0; copy < 2_000; copy++)
                    {
                        Interlocked.Add(ref now, 3);
                        var bytes = keys.Copy(out _);

                        if (bytes.Length == 0 || bytes.Length % Key != 0 || bytes.Length > SessionTicketKeys.Capacity * Key)
                            Interlocked.Increment(ref invalid);
                    }
                }
            );
            Check(invalid == 0, "concurrent handshakes copy whole, bounded key sets while the keys rotate");
        }

        private static void Check(bool condition, string name) =>
            QuicFuzzChecks.Require(condition, "session-ticket-keys-" + name);

        private sealed class Holder(SessionTicketKeys keys) : SharedCredential(true, keys)
        {
            internal int Frees;

            protected override int Free()
            {
                Frees++;
                return 0;
            }
        }
    }
}
