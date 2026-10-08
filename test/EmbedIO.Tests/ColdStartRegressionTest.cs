using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ColdStartRegressionTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task StartReturnsWhenRunCompletesWithoutReadiness(bool fault)
        {
            using var stop = new CancellationTokenSource();
            using var server = new EarlyCompletionServer(fault);
            var starting = Task.Run(() => ((IWebServer)server).Start(stop.Token));
            try { await starting.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally
            {
                stop.Cancel();
                try { await starting; } catch (OperationCanceledException) { }
            }
        }

        [Test]
        public async Task ModuleFailureReleasesPreparedResourceAndPreservesError()
        {
            using var server = new ProbeServer();
            var expected = new InvalidOperationException("module failed");
            server.Modules.Add(null, new StartupModule(() => throw expected));
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => server.RunAsync());
            Assert.That(actual, Is.SameAs(expected));
            Assert.That(server.Prepared, Is.EqualTo(1));
            Assert.That(server.Released, Is.True);
            Assert.That(server.Processed, Is.Zero);
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
        }

        [Test]
        public async Task ProcessingLoopFailureRetainsExistingCleanupOwnership()
        {
            var expected = new InvalidOperationException("after readiness");
            using var server = new ProbeServer { ProcessError = expected };
            Assert.That(await Assert.ThrowsAsync<InvalidOperationException>(() => server.RunAsync()), Is.SameAs(expected));
            Assert.That(server.Processed, Is.EqualTo(1));
            Assert.That(server.Released, Is.False);
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
        }

        [Test]
        public async Task AlreadyCanceledRunDoesNotPrepareOrStartModules()
        {
            using var server = new ProbeServer();
            using var stop = new CancellationTokenSource();
            var starts = 0;
            server.Modules.Add(null, new StartupModule(() => starts++));
            stop.Cancel();
            await server.RunAsync(stop.Token);
            Assert.That(server.Prepared, Is.Zero);
            Assert.That(starts, Is.Zero);
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
        }

        [Test]
        public async Task CancellationDuringPrepareDoesNotStartModules()
        {
            using var stop = new CancellationTokenSource();
            using var server = new ProbeServer { OnPrepare = () => stop.Cancel() };
            var starts = 0;
            server.Modules.Add(null, new StartupModule(() => starts++));
            await server.RunAsync(stop.Token);
            Assert.That(starts, Is.Zero);
            Assert.That(server.Processed, Is.Zero);
        }

        [Test]
        public async Task CancellationDuringModuleStartDoesNotStartFollowingModule()
        {
            using var stop = new CancellationTokenSource();
            using var server = new ProbeServer();
            var starts = 0;
            server.Modules.Add(null, new StartupModule(() => stop.Cancel()));
            server.Modules.Add(null, new StartupModule(() => starts++));
            await server.RunAsync(stop.Token);
            Assert.That(starts, Is.Zero);
            Assert.That(server.Processed, Is.Zero);
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
        }

        [Test]
        public async Task StartupCleanupFailureDoesNotMaskOriginalError()
        {
            using var server = new ProbeServer { FailCleanup = true };
            var expected = new InvalidOperationException("original");
            server.Modules.Add(null, new StartupModule(() => throw expected));
            Assert.That(await Assert.ThrowsAsync<InvalidOperationException>(() => server.RunAsync()), Is.SameAs(expected));
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
            Assert.That(server.Released, Is.True);
        }

        [Test]
        public async Task ConcurrentSecondRunDoesNotPrepareOrStopActiveRun()
        {
            using var server = new ProbeServer();
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
                var second = server.RunAsync(stop.Token);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.That(error, Is.Not.Null);
                Assert.That(server.Prepared, Is.EqualTo(1));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally { stop.Cancel(); await running; }
        }

        [Test]
        public async Task CompletedRunCannotBeStartedAgain()
        {
            using var server = new ProbeServer();
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await server.RunAsync(stop.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => server.RunAsync(stop.Token));
        }

        [Test]
        public async Task StartSignalsListeningAndUnsubscribes()
        {
            using var server = new ProbeServer();
            using var stop = new CancellationTokenSource();
            var states = new List<WebServerState>();
            var observedStop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, e) =>
            {
                states.Add(e.NewState);
                if (e.NewState == WebServerState.Stopped) observedStop.TrySetResult(true);
            };
            try
            {
                await Task.Run(() => server.Start(stop.Token)).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));

                var handlers = (Delegate?)((typeof(WebServerBase<TestWebServerOptions>))
                    .GetField("StateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
                    .GetValue(server);
                Assert.That((handlers ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetInvocationList(), Has.Length.EqualTo(2));
            }
            finally { stop.Cancel(); await observedStop.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            Assert.That(states, Is.EqualTo(new[] { WebServerState.Loading, WebServerState.Listening, WebServerState.Stopped }));
        }

        [Test]
        public async Task StartCancellationWhileModuleIsLoadingReturnsWithoutWaitingForCallback()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var stop = new CancellationTokenSource();
            using var server = new ProbeServer();
            server.Modules.Add(null, new StartupModule(() => { entered.Set(); release.Wait(); }));
            var starting = Task.Run(() => server.Start(stop.Token));
            try
            {
                Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
                stop.Cancel();
                await Assert.CatchAsync<OperationCanceledException>(async () => await starting.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            finally { release.Set(); await server.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        }

        private sealed class EarlyCompletionServer : TestWebServer, IWebServer
        {
            private readonly bool _fault;
            internal EarlyCompletionServer(bool fault) => _fault = fault;
            Task IWebServer.RunAsync(CancellationToken token) => _fault
                ? Task.FromException(new InvalidOperationException("before readiness"))
                : Task.CompletedTask;
        }

        private sealed class ProbeServer : WebServerBase<TestWebServerOptions>
        {
            internal int Prepared;
            internal int Processed;
            internal bool Released;
            internal bool FailCleanup;
            internal Exception? ProcessError;
            internal Action? OnPrepare;
            internal readonly TaskCompletionSource<bool> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal ProbeServer() => StateChanged += (_, e) =>
            {
                if (e.NewState == WebServerState.Stopped) Finished.TrySetResult(true);
            };

            protected override void Prepare(CancellationToken token) { Prepared++; OnPrepare?.Invoke(); }
            protected override async Task ProcessRequestsAsync(CancellationToken token)
            {
                Processed++;
                if (ProcessError != null) throw ProcessError;
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            protected override void OnFatalException()
            {
                Released = true;
                if (FailCleanup) throw new Exception("cleanup");
            }
        }

        private sealed class StartupModule : WebModuleBase
        {
            private readonly Action _start;
            internal StartupModule(Action start) : base("/") => _start = start;
            public override bool IsFinalHandler => false;
            protected override void OnStart(CancellationToken token) => _start();
            protected override Task OnRequestAsync(IHttpContext context) => Task.CompletedTask;
        }
    }
}
