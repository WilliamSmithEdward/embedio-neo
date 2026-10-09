using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The macOS rebind retry policy, driven by a manual clock and a scripted bind.
    public class QuicEndpointReleaseTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly IPEndPoint Released = new(IPAddress.Loopback, 50001);
        private long _now;
        private object? _releases;

        [SetUp]
        public void CreateTracker()
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.QuicEndpointReleases");
            if (type == null) { Assert.Ignore("The selected asset has no HTTP/3 transport."); return; }
            _now = 0;
            _releases = Activator.CreateInstance(type, Hidden, null, new object[] { TimeSpan.FromSeconds(1), new Func<long>(() => _now) }, null)
                ?? throw new AssertionException("Missing tracker instance.");
        }

        private static long Seconds(double value) => (long)(value * Stopwatch.Frequency);

        private object? Call(string name, Type[]? generic, params object[] args)
        {
            var releases = _releases ?? throw new AssertionException("Missing tracker instance.");
            var method = releases.GetType().GetMethod(name, Hidden) ?? throw new AssertionException("Missing tracker method.");
            if (generic != null) method = method.MakeGenericMethod(generic);
            try { return method.Invoke(releases, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }

        private void Record(IPEndPoint endpoint) => Call("Record", null, endpoint);

        // Fails with each error in turn, advancing the clock after every failure, then binds.
        private int _attempts;
        private void Bind(IPEndPoint endpoint, bool deferredClose, double advance, params SocketError[] failures)
        {
            _attempts = 0;
            Func<int> bind = () =>
            {
                if (++_attempts > failures.Length) return _attempts;
                _now += Seconds(advance);
                throw new SocketException((int)failures[_attempts - 1]);
            };
            Assert.That(Call("Bind", new[] { typeof(int) }, endpoint, deferredClose, bind), Is.EqualTo(_attempts));
        }

        private void Rejects(SocketError expected, int attempts, IPEndPoint endpoint, bool deferredClose, double advance, params SocketError[] failures)
        {
            var error = Assert.Throws<SocketException>(() => Bind(endpoint, deferredClose, advance, failures));
            Assert.That(error?.SocketErrorCode, Is.EqualTo(expected));
            Assert.That(_attempts, Is.EqualTo(attempts));
        }

        [Test]
        public void RetriesImmediateRebindOfReleasedEndpoint()
        {
            Record(Released);
            Bind(Released, true, 0.001, SocketError.AddressAlreadyInUse, SocketError.AddressAlreadyInUse, SocketError.AddressAlreadyInUse);
            Assert.That(_attempts, Is.EqualTo(4));
        }

        [Test]
        public void DoesNotRetryWhereTheCloseIsNotDeferred()
        {
            Record(Released);
            Rejects(SocketError.AddressAlreadyInUse, 1, Released, false, 0.001, SocketError.AddressAlreadyInUse);
        }

        [Test]
        public void DoesNotRetryAnEndpointThisProcessDidNotRelease()
        {
            Record(Released);
            var other = new IPEndPoint(IPAddress.Loopback, Released.Port + 1);
            Rejects(SocketError.AddressAlreadyInUse, 1, other, true, 0.001, SocketError.AddressAlreadyInUse);
        }

        [Test]
        public void DoesNotRetryOtherSocketErrors()
        {
            Record(Released);
            Rejects(SocketError.AccessDenied, 1, Released, true, 0.001, SocketError.AccessDenied);
        }

        [Test]
        public void StopsRetryingWhenTheReleaseIsNoLongerRecent()
        {
            Record(Released);
            // Failures at 0.4, 0.8 and 1.2 seconds after the release: the third is outside the window.
            Rejects(SocketError.AddressAlreadyInUse, 3, Released, true, 0.4,
                SocketError.AddressAlreadyInUse, SocketError.AddressAlreadyInUse, SocketError.AddressAlreadyInUse, SocketError.AddressAlreadyInUse);
        }

        [Test]
        public void AnotherReleaseRestartsTheWindow()
        {
            Record(Released);
            _now += Seconds(5);
            Rejects(SocketError.AddressAlreadyInUse, 1, Released, true, 0.001, SocketError.AddressAlreadyInUse);
            Record(Released);
            Bind(Released, true, 0.001, SocketError.AddressAlreadyInUse);
            Assert.That(_attempts, Is.EqualTo(2));
        }
    }
}
