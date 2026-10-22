using System;
using System.IO;
using System.Reflection;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class QuicProtocolFailureTraceTest
    {
        private static void RejectPacketNumber()
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Quic.PacketNumberWindow")
                ?? throw new AssertionException("Missing actual QUIC packet-number parser.");
            var window = Activator.CreateInstance(type, true)
                ?? throw new AssertionException("Missing actual packet-number window.");
            var add = type.GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing actual packet-number validation.");

            try
            {
                add.Invoke(window, [ulong.MaxValue]);
                Assert.Fail("The actual parser accepted an invalid packet number.");
            }

            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
        }

        [Test]
        public void ActualParserFailuresAreBoundedAndDetachAfterDisposal()
        {
            using var trace = QuicProtocolFailureTrace.Start();
            trace.Select(true);

            for (var iteration = 0; iteration < 32; iteration++) RejectPacketNumber();

            var snapshot = trace.Snapshot();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot, Has.Length.EqualTo(16));
                Assert.That(trace.CaptureFailed, Is.False);
                Assert.That(snapshot, Has.All.Contains("Invalid packet number."));
                Assert.That(snapshot, Has.All.Contains("EmbedIO.Quic.PacketNumberWindow.Add"));
                Assert.That(snapshot, Has.All.Length.LessThanOrEqualTo(4096));
            }

            trace.Dispose();
            trace.Dispose();
            RejectPacketNumber();
            Assert.That(trace.Snapshot(), Is.EqualTo(snapshot));
        }

        [Test]
        public void UnrelatedExceptionsAndUnselectedCasesAreIgnored()
        {
            using var trace = QuicProtocolFailureTrace.Start();
            trace.Select(true);
            Assert.Throws<InvalidDataException>(() => throw new InvalidDataException("Unrelated fixture exception."));
            Assert.That(trace.Snapshot(), Is.Empty);
            RejectPacketNumber();
            Assert.That(trace.Snapshot(), Is.Not.Empty);
            trace.Select(false);
            RejectPacketNumber();
            Assert.That(trace.Snapshot(), Is.Empty);
        }
    }
}
