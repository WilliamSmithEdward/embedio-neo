using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Idempotency;
using EmbedIO.Authorization;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;
using Coordinator = EmbedIO.Idempotency.Idempotency;

namespace EmbedIO.Tests
{
    public sealed partial class IdempotencyTest
    {
        [TestCase("empty")]
        [TestCase("space")]
        [TestCase("comma")]
        [TestCase("duplicate")]
        [TestCase("long")]
        public async Task InvalidClientKeyIsRejectedWithoutClaim(string mode)
        {
            var store = new MemoryIdempotencyStore(); var calls = 0;
            await With(new Coordinator(store), Policy(), c => Write(c, (++calls).ToString()), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/write") { Content = new StringContent("x") };
                var value = mode == "empty" ? "" : mode == "space" ? "bad key" : mode == "comma" ? "one,two" : mode == "long" ? new string('x', 129) : "one";
                request.Headers.TryAddWithoutValidation("Idempotency-Key", value);
                if (mode == "duplicate") request.Headers.TryAddWithoutValidation("Idempotency-Key", "two");
                using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest)); Assert.That(calls, Is.Zero); Assert.That(store.Count, Is.Zero);
            });
        }
        [TestCase("POST")]
        [TestCase("PUT")]
        [TestCase("PATCH")]
        [TestCase("DELETE")]
        public async Task WriteMethodsCanReplayFiniteResults(string method)
        {
            var calls = 0;
            await With(new Coordinator(), Policy(), c => Write(c, (++calls).ToString()), async client =>
            {
                for (var i = 0; i < 2; i++)
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), "/write") { Content = new StringContent("x") }; request.Headers.Add("Idempotency-Key", "key");
                    using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK)); Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("1"));
                }
                Assert.That(calls, Is.EqualTo(1));
            });
        }
        [TestCase("acquire-after", HttpStatusCode.Conflict, 0)]
        [TestCase("complete-before", HttpStatusCode.Conflict, 1)]
        [TestCase("complete-after", HttpStatusCode.OK, 1)]
        [TestCase("complete-refused", HttpStatusCode.Conflict, 1)]
        public async Task StoreFailuresNeverAuthorizeAnotherExecution(string mode, HttpStatusCode retryStatus, int effects)
        {
            var store = new FaultStore(mode); var calls = 0;
            await With(new Coordinator(store), Policy(wait: TimeSpan.Zero), c => Write(c, (++calls).ToString()), async client =>
            {
                using var first = await Send(client); Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                using var retry = await Send(client); Assert.That(retry.StatusCode, Is.EqualTo(retryStatus)); Assert.That(calls, Is.EqualTo(effects));
                if (retryStatus == HttpStatusCode.OK) Assert.That(await retry.Content.ReadAsStringAsync(), Is.EqualTo("1"));
            });
        }
        [Test]
        public async Task SharedStoreCoordinatesDifferentCoordinatorInstances()
        {
            var store = new MemoryIdempotencyStore(); var calls = 0;
            await With(new Coordinator(store), Policy(), c => Write(c, (++calls).ToString()), async first =>
            {
                using var initial = await Send(first);
                await With(new Coordinator(store), Policy(), c => Write(c, (++calls).ToString()), async second =>
                { using var replay = await Send(second); Assert.That(await replay.Content.ReadAsStringAsync(), Is.EqualTo("1")); Assert.That(calls, Is.EqualTo(1)); });
            });
        }
        [Test]
        public async Task PolicyPartitionsRemainIndependentWithSharedStore()
        {
            var store = new MemoryIdempotencyStore(); var calls = 0;
            await With(new Coordinator(store), Policy(partition: "first"), c => Write(c, (++calls).ToString()), async first =>
            {
                using var initial = await Send(first);
                await With(new Coordinator(store), Policy(partition: "second"), c => Write(c, (++calls).ToString()), async second =>
                { using var replay = await Send(second); Assert.That(await replay.Content.ReadAsStringAsync(), Is.EqualTo("2")); Assert.That(calls, Is.EqualTo(2)); });
            });
        }
        [Test]
        public async Task ChangedVaryRequirementsAreMergedOnReplay()
        {
            var requests = 0; var calls = 0;
            await With(new Coordinator(), Policy(), c => Write(c, (++calls).ToString()), async client =>
            {
                using var first = await Send(client); using var retry = await Send(client);
                Assert.That(retry.Headers.Vary, Is.EquivalentTo(new[] { "X-Variant", "Accept" })); Assert.That(calls, Is.EqualTo(1));
            }, c => c.Response.Headers["Vary"] = ++requests == 1 ? "X-Variant" : "Accept");
        }
        [Test]
        public async Task OwnerCancellationBecomesUncertainInsteadOfReexecution()
        {
            var entered = Signal(); var calls = 0;
            await With(new Coordinator(), Policy(wait: TimeSpan.Zero), async c =>
            { Interlocked.Increment(ref calls); entered.TrySetResult(); await Task.Delay(Timeout.Infinite, c.CancellationToken); }, async client =>
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var pending = Send(client, token: cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Assert.CatchAsync<OperationCanceledException>(async () => { using var ignored = await pending; });
                using var retry = await Send(client); Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Conflict)); Assert.That(calls, Is.EqualTo(1));
            });
        }
        [Test]
        public async Task ParticipantOverflowFailsClosedBeforeAnotherClaim()
        {
            var store = new MemoryIdempotencyStore(); var coordinator = new Coordinator(store, maxConcurrentRequests: 1); var entered = Signal(); var release = Signal(); var calls = 0;
            await With(coordinator, Policy(), async c => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; await Write(c, "done"); }, async client =>
            {
                var pending = Send(client); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try { using var overflow = await Send(client, key: "two"); Assert.That(overflow.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable)); Assert.That(store.Count, Is.EqualTo(1)); }
                finally { release.TrySetResult(); }
                using var first = await pending; Assert.That(calls, Is.EqualTo(1));
            });
        }
        [TestCase(false)]
        [TestCase(true)]
        public void UnknownControllerPolicyFailsAtRegistration(bool generated)
        {
            var module = new WebApiModule("/"); var state = new ControllerState();
            module.ConfigureAuthorization(new AuthorizationOptions().AddPolicy("active", (_, _) => Task.FromResult(true)));
            Assert.Throws<InvalidOperationException>(() =>
            { if (generated) module.RegisterController(IdempotencyBindings.WriteController, () => new WriteController(state)); else module.RegisterController(() => new WriteController(state)); });
        }
        [Test]
        public async Task UnknownLengthBodyIsBoundedBeforeAcquiring()
        {
            var calls = 0; var store = new MemoryIdempotencyStore();
            await With(new Coordinator(store), Policy(request: 1), c => Write(c, (++calls).ToString()), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/write") { Content = new UnknownLengthContent() }; request.Headers.Add("Idempotency-Key", "one");
                using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge)); Assert.That(calls, Is.Zero); Assert.That(store.Count, Is.Zero);
            });
        }
        private sealed class UnknownLengthContent : HttpContent
        {
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[10], 0, 10);
        }
        [TestCase("\0", TestName = "PersistedHeadersRejectNullCharacter")]
        [TestCase("\u007f", TestName = "PersistedHeadersRejectDeleteCharacter")]
        [TestCase("\r", TestName = "PersistedHeadersRejectCarriageReturnCharacter")]
        [TestCase("\n", TestName = "PersistedHeadersRejectLineFeedCharacter")]
        [TestCase("\u0001", TestName = "PersistedHeadersRejectControlOneCharacter")]
        public void PersistedHeadersRejectControlCharacters(string control)
            => Assert.Throws<ArgumentException>(() => new IdempotencyResponse(200, Array.Empty<byte>(), headers: new[] { new KeyValuePair<string, string>("X-Value", "before" + control + "after") }));
        [TestCase(204)]
        [TestCase(304)]
        public void BodylessStatusesCannotRecordBodyBytes(int status)
            => Assert.Throws<ArgumentException>(() => new IdempotencyResponse(status, new byte[] { 1 }));
        [Test]
        public void ResultAccountingIncludesCharacterEncodingMetadata()
        {
            var response = new IdempotencyResponse(200, new byte[] { 1 }, characterEncoding: "utf-8");
            Assert.That(response.SizeBytes, Is.EqualTo(11));
        }
        [Test]
        public void EncodingMetadataCannotExceedTheResponseMetadataCap()
            => Assert.Throws<ArgumentException>(() => new IdempotencyResponse(200, Array.Empty<byte>(), headers: new[] { new KeyValuePair<string, string>("X", new string('a', 8189)) }, characterEncoding: "utf-8"));
        [Test]
        public async Task WaiterCannotStartAnotherExecutionIfTheOriginalRecordExpiresBetweenPolls()
        {
            var calls = 0; var store = new ExpiryGapStore();
            await With(new Coordinator(store), Policy(), c => Write(c, (++calls).ToString()), async client =>
            { using var response = await Send(client); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict)); Assert.That(calls, Is.Zero); Assert.That(store.MarkedUncertain, Is.True); });
        }
        // Models an existing execution completing and its terminal record expiring between polls.
        private sealed class ExpiryGapStore : IIdempotencyStore
        {
            private readonly MemoryIdempotencyStore _inner = new MemoryIdempotencyStore();
            private int _calls;
            internal bool MarkedUncertain;
            public Task<IdempotencyRecord> AcquireAsync(string key, string fingerprint, TimeSpan retention, CancellationToken token)
                => Interlocked.Increment(ref _calls) == 1 ? Task.FromResult(new IdempotencyRecord(IdempotencyState.InProgress)) : _inner.AcquireAsync(key, fingerprint, retention, token);
            public Task<bool> CompleteAsync(string key, string owner, IdempotencyResponse response, CancellationToken token) => _inner.CompleteAsync(key, owner, response, token);
            public async Task<bool> FailAsync(string key, string owner, CancellationToken token) { MarkedUncertain = await _inner.FailAsync(key, owner, token); return MarkedUncertain; }
        }
        private sealed class FaultStore : IIdempotencyStore
        {
            private readonly MemoryIdempotencyStore _inner = new MemoryIdempotencyStore();
            private readonly string _mode;
            private int _acquires;
            internal FaultStore(string mode) => _mode = mode;
            public async Task<IdempotencyRecord> AcquireAsync(string key, string fingerprint, TimeSpan retention, CancellationToken cancellationToken)
            {
                var result = await _inner.AcquireAsync(key, fingerprint, retention, cancellationToken);
                if (_mode == "acquire-after" && Interlocked.Increment(ref _acquires) == 1) throw new InvalidOperationException("Claim persisted but acknowledgement was lost.");
                return result;
            }
            public async Task<bool> CompleteAsync(string key, string ownerToken, IdempotencyResponse response, CancellationToken cancellationToken)
            {
                if (_mode == "complete-refused") return false;
                if (_mode == "complete-before") throw new InvalidOperationException("Result was not persisted.");
                var result = await _inner.CompleteAsync(key, ownerToken, response, cancellationToken);
                if (_mode == "complete-after") throw new InvalidOperationException("Result persisted but acknowledgement was lost.");
                return result;
            }
            public Task<bool> FailAsync(string key, string ownerToken, CancellationToken cancellationToken) => _inner.FailAsync(key, ownerToken, cancellationToken);
        }
    }
}
