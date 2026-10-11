using System;
using System.Threading;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class GracefulDrainValidationTest
    {
        [TestCase(0L)]
        [TestCase(-1L)]
        [TestCase(4294967295L)]
        public void InvalidTimeoutIsRejectedWithoutStartingListener(long milliseconds)
        {
            using var server = new WebServer(HttpListenerMode.EmbedIO, "http://localhost:19999/");
            Assert.That(() => server.DrainAsync(TimeSpan.FromMilliseconds(milliseconds)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(server.Listener.IsListening, Is.False);
        }
        [Test]
        public void RetiredMicrosoftModeIsRejectedBeforeListenerCreation()
        {
            Assert.That(() => { using var server = new WebServer((HttpListenerMode)1, "http://localhost:19999/"); },
                Throws.InstanceOf<NotSupportedException>().With.Message.Contains("no longer supports the Microsoft HTTP listener"));
        }
        [Test]
        public void PreCanceledDrainHasNoListenerSideEffects()
        {
            using var server = new WebServer(HttpListenerMode.EmbedIO, "http://localhost:19999/");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.That(() => server.DrainAsync(TimeSpan.FromSeconds(1), canceled.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(server.Listener.IsListening, Is.False);
        }
    }
}
