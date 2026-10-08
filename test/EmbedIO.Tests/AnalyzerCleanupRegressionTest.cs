using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Testing;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class AnalyzerCleanupRegressionTest
    {
        private const BindingFlags InternalInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void UriConfigurationKeepsEscapedCaseAndStringWildcardPrefixes()
        {
            var prefix = new Uri("http://localhost:12345/MiXeD/%2F/");
            var options = new WebServerOptions().WithUrlPrefix(prefix).WithUrlPrefix("http://*:12346/Wildcard/");
            Assert.That(options.UrlPrefixes, Is.EqualTo(new[] { prefix.OriginalString, "http://*:12346/Wildcard/" }));
            var other = new WebServerOptions();
            other.AddUrlPrefix(prefix);
            Assert.That(other.UrlPrefixes.Single(), Is.EqualTo(prefix.OriginalString));
        }

        [Test]
        public void UriValidationAndRedirectOverloadsKeepEscapedRelativeTargets()
        {
            const string target = "/MiXeD/%2F?q=A%2BB";
            var relative = new Uri(target, UriKind.Relative);
            var validated = Validate.Url(nameof(target), target);
            Assert.That(validated.IsAbsoluteUri, Is.False);
            Assert.That(validated.OriginalString, Is.EqualTo(target));
            var combined = Validate.Url(nameof(target), target, new Uri("https://example.com/base/"));
            Assert.That(combined.AbsoluteUri, Is.EqualTo("https://example.com" + target));
            var fromText = new RedirectModule("/", target);
            var fromUri = new RedirectModule("/", relative);
            Assert.That(fromUri.RedirectUrl.ToString(), Is.EqualTo(fromText.RedirectUrl.ToString()));
            Assert.That(fromUri.StatusCode, Is.EqualTo(HttpStatusCode.Found));
        }

        [Test]
        public async Task RawRequestTargetKeepsEscapedCharactersAndQueryCase()
        {
            using var server = new TestWebServer().OnAny(context => context.SendStringAsync(context.Request.RawTarget, MimeType.PlainText, WebServer.DefaultEncoding));
            server.Start();
            const string target = "/MiXeD/%2F?q=A%2BB";
            Assert.That(await server.Client.GetStringAsync(target), Is.EqualTo(target));
        }

        [TestCase("ordinary")]
        [TestCase("memory")]
        [TestCase("stack")]
        [TestCase("access")]
        public async Task RequestBoundariesHandleApplicationErrorsAndPropagateFatalErrors(string kind)
        {
            Exception failure = kind switch
            {
                "memory" => new OutOfMemoryException("synthetic test failure"),
                "stack" => new StackOverflowException("synthetic test failure"),
                "access" => new AccessViolationException("synthetic test failure"),
                _ => new InvalidOperationException("synthetic test failure")
            };
            var handled = 0;
            using var server = new TestWebServer().OnAny(_ => throw failure)
                .HandleUnhandledException((context, exception) =>
                {
                    Assert.That(exception, Is.SameAs(failure));
                    handled++;
                    context.Response.StatusCode = 500;
                    return Task.CompletedTask;
                });
            server.Start();
            if (kind == "ordinary")
            {
                using var response = await server.Client.GetAsync("/");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(handled, Is.EqualTo(1));
            }
            else
            {
                await Assert.ThatAsync(() => server.Client.GetAsync("/"), Throws.TypeOf(failure.GetType()));
                Assert.That(handled, Is.Zero);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task WriteGateDisposalWaitsForActiveAndQueuedWriters(bool cancelQueued)
        {
            var type = InternalType("AsyncWriteGate");
            using var gate = (IDisposable)(Activator.CreateInstance(type, true) ?? throw new InvalidOperationException());
            using var cancel = new CancellationTokenSource();
            var first = await EnterGate(gate, CancellationToken.None);
            var queued = EnterGate(gate, cancel.Token);
            var semaphore = (SemaphoreSlim)(type.GetField("_semaphore", InternalInstance)?.GetValue(gate)
                ?? throw new MissingFieldException("_semaphore"));
            gate.Dispose();
            Assert.That(queued.IsCompleted, Is.False);
            Assert.DoesNotThrow(() => { _ = semaphore.Wait(0); });
            if (cancelQueued)
            {
                cancel.Cancel();
                await Assert.ThatAsync(async () => await queued, Throws.InstanceOf<OperationCanceledException>());
            }
            first.Dispose();
            if (!cancelQueued) (await queued).Dispose();
            Assert.Throws<ObjectDisposedException>(() => semaphore.Wait(0));
            await Assert.ThatAsync(() => EnterGate(gate, CancellationToken.None), Throws.TypeOf<ObjectDisposedException>());
        }

        [Test]
        public async Task PeriodicWorkerOwnsItsTokenUntilTheCallbackFinishes()
        {
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<CancellationToken, Task> action = async token =>
            {
                entered.TrySetResult(token);
                await release.Task;
            };
            var type = InternalType("PeriodicTask");
            using var periodic = (IDisposable)(Activator.CreateInstance(type, InternalInstance, null,
                new object[] { TimeSpan.FromMilliseconds(5), action, CancellationToken.None }, null)
                ?? throw new InvalidOperationException());
            var completion = (Task)(type.GetProperty("Completion", InternalInstance)?.GetValue(periodic)
                ?? throw new MissingMemberException("Completion"));
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                periodic.Dispose();
                Assert.That(token.IsCancellationRequested, Is.True);
                Assert.That(token.WaitHandle.WaitOne(0), Is.True);
            }
            finally { release.TrySetResult(); }
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() => _ = token.WaitHandle);
        }

        [Test]
        public void RequestWrapperDisposalLeavesTheConnectionTransportOpen()
        {
            using var transport = new MemoryStream(new byte[] { 42 });
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.RequestStream", true)
                ?? throw new TypeLoadException();
            using var request = (Stream)(Activator.CreateInstance(type, InternalInstance, null,
                new object[] { transport, Array.Empty<byte>(), 0, 0, 1L }, null)
                ?? throw new InvalidOperationException());
            request.Dispose();
            Assert.That(transport.ReadByte(), Is.EqualTo(42));
        }

        [Test]
        public void CompressionWriterAcceptsTheStreamsOptionalAsyncCallback()
        {
            using var transport = new MemoryStream();
            var type = InternalType("CompressionStream");
            using var writer = (Stream)(Activator.CreateInstance(type, new object[] { transport, CompressionMethod.None })
                ?? throw new InvalidOperationException());
            var bytes = new byte[] { 1, 2, 3 };
            var result = writer.BeginWrite(bytes, 0, bytes.Length, null, null);
            writer.EndWrite(result);
            Assert.That(transport.ToArray(), Is.EqualTo(bytes));
        }

        private static Type InternalType(string name)
            => typeof(WebServer).Assembly.GetType("EmbedIO.Internal." + name, true) ?? throw new TypeLoadException(name);

        private static async Task<IDisposable> EnterGate(object gate, CancellationToken token)
        {
            var method = gate.GetType().GetMethod("EnterAsync", InternalInstance) ?? throw new MissingMethodException("EnterAsync");
            var task = (Task)(method.Invoke(gate, new object[] { token }) ?? throw new InvalidOperationException());
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            return (IDisposable)(task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new MissingMemberException("Result"));
        }
    }
}
