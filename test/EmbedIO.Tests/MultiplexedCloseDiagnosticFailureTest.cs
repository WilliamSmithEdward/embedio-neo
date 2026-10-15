using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        [NonParallelizable]
        public async Task ContextCompletionSettlesWhenCloseDiagnosticListenerThrows(bool callbackThrows, bool diagnosticThrows, bool canceledResponse)
        {
            var observed = new TaskCompletionSource<(Task Completion, int Callbacks, Exception? Failure, bool LinksDetached)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var trace = new ThrowingCloseDiagnostic(diagnosticThrows);
            var source = Diagnostics.Log.Source;
            var previous = source.Switch.Level;
            source.Listeners.Add(trace);
            source.Switch.Level = SourceLevels.Error;
            try
            {
                await WithServer(exchange =>
                {
                    var context = Adapter(exchange);
                    using var firstStop = new CancellationTokenSource();
                    using var secondStop = new CancellationTokenSource();
                    context.CancellationToken = firstStop.Token;
                    var firstToken = context.CancellationToken;
                    context.CancellationToken = secondStop.Token;
                    var secondToken = context.CancellationToken;
                    var callbacks = 0;
                    context.OnClose(_ => ++callbacks);
                    context.OnClose(_ =>
                    {
                        ++callbacks;
                        if (callbackThrows) throw new InvalidOperationException("close callback fixture");
                    });
                    if (canceledResponse) secondStop.Cancel();
                    Exception? failure = null;
                    try { context.Close(); }
                    catch (InvalidOperationException error) { failure = error; }
                    catch (OperationCanceledException error) { failure = error; }
                    var completion = (Task)(context.GetType().GetProperty("Completion", Flags)?.GetValue(context)
                        ?? throw new AssertionException("Missing context completion."));
                    context.Close(); // Already closed: callbacks must not run twice.
                    firstStop.Cancel();
                    secondStop.Cancel();
                    observed.TrySetResult((completion, callbacks, failure,
                        !firstToken.IsCancellationRequested && (canceledResponse || !secondToken.IsCancellationRequested)));
                    return Task.CompletedTask;
                }, async client =>
                {
                    using var response = await client.GetAsync("diagnostic-close");
                    var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(trace.Triggered, Is.EqualTo(callbackThrows));
                    Assert.That(result.Completion.IsCompleted, Is.True,
                        "Context completion must settle even if reporting a close callback failure throws.");
                    Assert.That(result.Callbacks, Is.EqualTo(2), "All close callbacks must run exactly once.");
                    Assert.That(result.LinksDetached, Is.True, "Current and replaced links must detach at close.");
                    if (canceledResponse)
                    {
                        Assert.That(result.Failure, Is.InstanceOf<OperationCanceledException>());
                        Assert.That(result.Completion.IsFaulted, Is.True);
                        Assert.That(result.Completion.Exception?.InnerException, Is.SameAs(result.Failure),
                            "Diagnostics must not replace the original response failure.");
                    }
                    else
                    {
                        Assert.That(result.Failure, Is.Null);
                        Assert.That(result.Completion.IsCompletedSuccessfully, Is.True);
                    }
                });
            }
            finally
            {
                source.Listeners.Remove(trace);
                source.Switch.Level = previous;
            }
        }

        private sealed class ThrowingCloseDiagnostic(bool throwOnDiagnostic) : TraceListener
        {
            internal bool Triggered { get; private set; }
            public override void Write(string? message) { }
            public override void WriteLine(string? message) { }
            public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
                int id, string? format, params object?[]? args)
            {
                if (args is not { Length: >= 2 } || args[1] is not string message
                    || !message.Contains("Exception thrown by a HTTP context close callback.", StringComparison.Ordinal)) return;
                Triggered = true;
                if (throwOnDiagnostic) throw new InvalidOperationException("close diagnostic fixture");
            }
        }
    }
}
