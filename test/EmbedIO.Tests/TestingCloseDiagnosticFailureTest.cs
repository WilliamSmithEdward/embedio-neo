using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class TestingCloseDiagnosticFailureTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task DiagnosticFailureDoesNotInterruptTestContextCleanup(bool diagnosticThrows)
        {
            var calls = new List<int>();
            Exception? closeFailure = null;
            using var trace = new CloseDiagnosticListener(diagnosticThrows);
            var source = Diagnostics.Log.Source;
            var previous = source.Switch.Level;
            source.Listeners.Add(trace);
            source.Switch.Level = SourceLevels.Error;
            try
            {
                await TestWebServer.UseAsync(
                    server => server.OnAny(async context =>
                    {
                        context.OnClose(_ => calls.Add(1));
                        context.OnClose(_ =>
                        {
                            calls.Add(2);
                            throw new InvalidOperationException("Testing close callback fixture.");
                        });
                        context.OnClose(_ => calls.Add(3));
                        await context.SendStringAsync("body", "text/plain", new UTF8Encoding(false));
                        var implementation = (IHttpContextImpl)context;
                        try { implementation.Close(); }
                        catch (InvalidOperationException error) when (error.Message == CloseDiagnosticListener.Failure)
                        { closeFailure = error; }
                        implementation.Close();
                    }),
                    async client =>
                    {
                        using var response = await client.GetAsync("/");
                        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("body"));
                    });
                Assert.That(trace.Triggered, Is.True);
                Assert.That(calls, Is.EqualTo(new[] { 3, 2, 1 }), "Every callback must run once in reverse order despite diagnostic failure.");
                Assert.That(closeFailure, Is.Null, "Recoverable diagnostic failures must not escape context cleanup.");
            }
            finally
            {
                source.Listeners.Remove(trace);
                source.Switch.Level = previous;
            }
        }

        private sealed class CloseDiagnosticListener(bool diagnosticThrows) : TraceListener
        {
            internal const string Failure = "Testing close diagnostic fixture.";
            internal bool Triggered { get; private set; }
            public override void Write(string? message) { }
            public override void WriteLine(string? message) { }
            public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
                int id, string? format, params object?[]? args)
            {
                if (format != "HTTP context: close callback failed: {0}") return;
                Triggered = true;
                if (diagnosticThrows) throw new InvalidOperationException(Failure);
            }
        }
    }
}
