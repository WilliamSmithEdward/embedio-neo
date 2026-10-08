using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3WireTest
    {
        private static object Settings(string hex, int maximum = 1024)
        {
            try
            {
                return (InternalType("Http3PeerSettings").GetMethod("Parse", Hidden) ?? throw new AssertionException("Missing settings parser."))
                    .Invoke(null, new object[] { Convert.FromHexString(hex), maximum }) ?? throw new AssertionException("Missing settings.");
            }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static bool Flag(object value, string name) => (bool)((value.GetType().GetProperty(name)
            ?? throw new AssertionException("Missing settings flag.")).GetValue(value) ?? throw new AssertionException("Missing flag value."));

        [Test]
        public void SettingsDefaultsAndFullWidthLimits()
        {
            var defaults = Settings("");
            Assert.That(Property(defaults, "MaximumFieldSectionSize"), Is.EqualTo(long.MaxValue));
            Assert.That(Property(defaults, "MaximumTableCapacity"), Is.Zero);
            Assert.That(Property(defaults, "BlockedStreams"), Is.Zero);
            Assert.That(Flag(defaults, "ExtendedConnect"), Is.False);
            Assert.That(Flag(defaults, "Datagrams"), Is.False);
            var settings = Settings("01ffffffffffffffff0600074000080133012100");
            Assert.That(Property(settings, "MaximumTableCapacity"), Is.EqualTo(4611686018427387903L));
            Assert.That(Property(settings, "MaximumFieldSectionSize"), Is.Zero);
            Assert.That(Property(settings, "BlockedStreams"), Is.Zero);
            Assert.That(Flag(settings, "ExtendedConnect"), Is.True);
            Assert.That(Flag(settings, "Datagrams"), Is.True);
        }

        [TestCase("01000101", 0x109)]
        [TestCase("2100402101", 0x109)]
        [TestCase("0200", 0x109)]
        [TestCase("0300", 0x109)]
        [TestCase("0400", 0x109)]
        [TestCase("0500", 0x109)]
        [TestCase("0802", 0x109)]
        [TestCase("3302", 0x109)]
        [TestCase("01", 0x106)]
        [TestCase("4001", 0x106)]
        [TestCase("0140", 0x106)]
        public void MalformedSettingsHavePreciseErrorCodes(string wire, int code)
        {
            var error = Assert.Catch<IOException>(() => Settings(wire));
            Assert.That(Property(error, "ErrorCode"), Is.EqualTo(code));
        }

        [Test]
        public void UnknownSettingsCountTowardResourceBound()
        {
            Assert.DoesNotThrow(() => Settings("2100404001", 2));
            var error = Assert.Catch<IOException>(() => Settings("2100404001", 1));
            Assert.That(Property(error, "ErrorCode"), Is.EqualTo(0x107));
        }

        private sealed class Control
        {
            private readonly object _instance;
            internal Control(Stream stream, bool server = false) => _instance = Activator.CreateInstance(InternalType("Http3ControlStream"), Hidden,
                null, new object[] { stream, server }, null) ?? throw new AssertionException("Missing control stream.");
            internal async Task<object> Next()
            {
                var task = (Task)((_instance.GetType().GetMethod("ReadAsync", Hidden) ?? throw new AssertionException("Missing control reader."))
                    .Invoke(_instance, new object[] { CancellationToken.None }) ?? throw new AssertionException("Missing control task."));
                await task;
                return (task.GetType().GetProperty("Result") ?? throw new AssertionException("Missing control result.")).GetValue(task)
                    ?? throw new AssertionException("Missing control event.");
            }
        }

        [TestCase("0000", false, 0x10a)]
        [TestCase("2100", false, 0x10a)]
        [TestCase("04000400", false, 0x105)]
        [TestCase("04000000", false, 0x105)]
        [TestCase("04000100", false, 0x105)]
        [TestCase("04000200", false, 0x105)]
        [TestCase("04000500", false, 0x105)]
        [TestCase("04000600", false, 0x105)]
        [TestCase("04000800", false, 0x105)]
        [TestCase("04000900", false, 0x105)]
        [TestCase("04000d0100", true, 0x105)]
        [TestCase("04000700", false, 0x106)]
        [TestCase("040007020000", false, 0x106)]
        [TestCase("0400070140", false, 0x106)]
        [TestCase("0400070101", true, 0x108)]
        [TestCase("040007010807010c", true, 0x108)]
        [TestCase("04000d01080d0107", false, 0x108)]
        [TestCase("0400", false, 0x104)]
        [TestCase("", false, 0x104)]
        public async Task InvalidControlSequencesRejectConnection(string wire, bool server, int code)
        {
            using var source = new FragmentedStream(Convert.FromHexString(wire), 1);
            var control = new Control(source, server);
            var error = await Assert.CatchAsync<IOException>(async () => { for (var i = 0; i < 4; i++) await control.Next(); });
            Assert.That(Property(error, "ErrorCode"), Is.EqualTo(code));
            await Assert.ThatAsync(async () => await control.Next(), Throws.TypeOf<IOException>());
        }

        [TestCase("0400800f070000", false, 0x106)]
        [TestCase("0400800f07000140", false, 0x106)]
        [TestCase("0400800f07000101", false, 0x108)]
        [TestCase("0400800f07000102", false, 0x108)]
        [TestCase("0400800f07000103", false, 0x108)]
        [TestCase("0400800f0700020080", false, 0x101)]
        [TestCase("0400800f07000100", true, 0x105)]
        [TestCase("0400800f07010100", true, 0x105)]
        [TestCase("0400800f070080004001", false, 0x107)]
        public async Task InvalidPriorityUpdateRejectsBeforeFollowingFrame(string wire, bool server, int code)
        {
            using var source = new FragmentedStream(Convert.FromHexString(wire + "070100"), 1);
            var control = new Control(source, server);
            await control.Next();
            var error = await Assert.CatchAsync<IOException>(async () => await control.Next());
            Assert.That(Property(error, "ErrorCode"), Is.EqualTo(code));
            await Assert.ThatAsync(async () => await control.Next(), Throws.TypeOf<IOException>());
        }

        [TestCase("800f07000100", 0xf0700, 0, "")]
        [TestCase("800f07000404753d31", 0xf0700, 4, "u=1")]
        [TestCase("800f07000bcffffffffffffffc753d37", 0xf0700, 1152921504606846972L, "u=7")]
        [TestCase("800f07010407753d32", 0xf0701, 7, "u=2")]
        public async Task PriorityUpdatePreservesFieldAndFollowingControlFrame(string wire, long type, long id, string field)
        {
            using var source = new FragmentedStream(Convert.FromHexString("0400" + wire + "070100"), 1);
            var control = new Control(source);
            await control.Next();
            var update = await control.Next();
            Assert.That(Property(update, "Type"), Is.EqualTo(type));
            Assert.That(Property(update, "Identifier"), Is.EqualTo(id));
            Assert.That(update.GetType().GetProperty("PriorityFieldValue")?.GetValue(update), Is.EqualTo(field));
            Assert.That(Property(await control.Next(), "Type"), Is.EqualTo(7));
        }

        [Test]
        public async Task ControlSkipsUnknownFramesAndPreservesIdentifiers()
        {
            using var source = new FragmentedStream(Convert.FromHexString("04002103aabbcc0d01040d0104070104030102"), 1);
            var control = new Control(source);
            Assert.That(Property(await control.Next(), "Type"), Is.EqualTo(4));
            Assert.That(Property(await control.Next(), "Identifier"), Is.EqualTo(4));
            Assert.That(Property(await control.Next(), "Type"), Is.EqualTo(13));
            Assert.That(Property(await control.Next(), "Type"), Is.EqualTo(7));
            Assert.That(Property(await control.Next(), "Identifier"), Is.EqualTo(2));
        }

        [Test]
        public async Task ServerGoAwayMayRepeatOrDecreaseAtRequestStreamBoundaries()
        {
            using var source = new FragmentedStream(Convert.FromHexString("0400070108070108070104"), 1);
            var control = new Control(source, true);
            await control.Next();
            Assert.That(Property(await control.Next(), "Identifier"), Is.EqualTo(8));
            Assert.That(Property(await control.Next(), "Identifier"), Is.EqualTo(8));
            Assert.That(Property(await control.Next(), "Identifier"), Is.EqualTo(4));
        }
    }
}
