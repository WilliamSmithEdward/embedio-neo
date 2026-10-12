using System;
using System.Globalization;
using System.Text;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class HttpApplicationLifecycleAuditTest
    {
        // Independent, bounded readers for the plain status representations used
        // by these wire fixtures; no production compression parser is called.
        private static int RawBodyHpackStatus(byte[] section)
        {
            var offset = 0;
            while (offset < section.Length && (section[offset] & 0xe0) == 0x20)
                _ = RawBodyPrefix(section, ref offset, 5);
            Assert.That(offset, Is.LessThan(section.Length));
            if ((section[offset] & 0x80) != 0)
            {
                Assert.That(RawBodyPrefix(section, ref offset, 7), Is.EqualTo(8));
                return 200;
            }
            Assert.That(RawBodyPrefix(section, ref offset, (section[offset] & 0x40) != 0 ? 6 : 4), Is.EqualTo(8));
            Assert.That(offset, Is.LessThan(section.Length));
            Assert.That(section[offset] & 0x80, Is.Zero);
            Assert.That(RawBodyPrefix(section, ref offset, 7), Is.EqualTo(3));
            Assert.That(section.Length - offset, Is.GreaterThanOrEqualTo(3));
            return int.Parse(Encoding.ASCII.GetString(section, offset, 3), CultureInfo.InvariantCulture);
        }

        private static int RawBodyPrefix(byte[] bytes, ref int offset, int bits)
        {
            Assert.That(offset, Is.InRange(0, bytes.Length - 1));
            var maximum = (1 << bits) - 1;
            var value = bytes[offset++] & maximum;
            if (value < maximum) return value;
            var shift = 0;
            byte next;
            do
            {
                Assert.That(offset, Is.LessThan(bytes.Length));
                Assert.That(shift, Is.LessThanOrEqualTo(28));
                next = bytes[offset++];
                value = checked(value + ((next & 127) << shift));
                shift += 7;
            } while ((next & 128) != 0);
            return value;
        }
    }
}
