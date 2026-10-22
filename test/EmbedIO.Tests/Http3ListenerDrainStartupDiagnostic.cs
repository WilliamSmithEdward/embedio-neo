using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        private static async Task WaitForDrainEntryAsync(WebServer server, string prefix,
            Task entered, Task running, Task response, CancellationToken token)
        {
            LogDrainStartup("wait-entry", server, prefix, entered, running, response);
            await Task.WhenAny(entered, running, response).WaitAsync(token);
            // Preserve the real startup exception before waiting for an entry
            // which a failed listener can never signal.
            if (running.IsCompleted)
            {
                await running;
                throw new AssertionException("The server stopped before the drain trigger.");
            }
            if (response.IsCompleted && !entered.IsCompleted)
            {
                await response;
                throw new AssertionException("The client completed before handler entry.");
            }
            await entered.WaitAsync(token);
            LogDrainStartup("entered", server, prefix, entered, running, response);
        }

        private static async Task FinishDrainOwnersAsync(WebServer server, string prefix,
            Task entered, Task running, Task response, Exception? primary, bool expectedResponseFailure)
        {
            var started = Stopwatch.GetTimestamp();
            var failures = new List<Exception>();
            using var budgetStop = new CancellationTokenSource();
            var budget = Task.Delay(TimeSpan.FromSeconds(5), budgetStop.Token);
            while (!running.IsCompleted || !response.IsCompleted)
            {
                var remaining = new List<Task> { budget };
                if (!running.IsCompleted) remaining.Add(running);
                if (!response.IsCompleted) remaining.Add(response);
                var winner = await Task.WhenAny(remaining);
                if (ReferenceEquals(winner, budget)) break;
            }
            if (budget.IsCompletedSuccessfully)
                failures.Add(new TimeoutException("Drain owner cleanup exceeded its one shared five-second budget."));
            else budgetStop.Cancel();

            await CollectDrainOwnerFailureAsync(running, "server", expectedFailure: false, primary, failures);
            await CollectDrainOwnerFailureAsync(response, "client", expectedResponseFailure, primary, failures);
            var terminal = running.IsCompleted && response.IsCompleted;
            if (!terminal && failures.Count == 0)
                failures.Add(new AssertionException("Drain cleanup returned with a nonterminal owner."));
            TestContext.Out.WriteLine($"Drain cleanup terminal={terminal}; running={running.Status}; client={response.Status}; elapsedTicks={Stopwatch.GetTimestamp() - started}; independentFailures={failures.Count}");
            LogDrainStartup(terminal ? "owners-terminal" : "owners-incomplete", server, prefix, entered, running, response);
            if (failures.Count == 0) return;
            if (primary != null)
                throw new AggregateException("Primary drain failure and independent cleanup failures.",
                    new[] { primary }.Concat(failures));
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException("Independent drain owner cleanup failures.", failures);
        }

        private static async Task CollectDrainOwnerFailureAsync(Task owner, string name,
            bool expectedFailure, Exception? primary, List<Exception> failures)
        {
            if (!owner.IsCompleted) return; // The shared budget failure remains genuine.
            var errors = new List<Exception>();
            if (owner.IsFaulted && owner.Exception != null)
                errors.AddRange(owner.Exception.InnerExceptions);
            else if (owner.IsCanceled)
            {
                try { await owner; }
                catch (OperationCanceledException error) { errors.Add(error); }
            }
            foreach (var error in errors)
            {
                TestContext.Out.WriteLine($"Drain {name} actual terminal error: {error}");
                if (ReferenceEquals(error, primary)) continue;
                if (expectedFailure && RecoverableDrainObservation(error)
                    && error is HttpRequestException or OperationCanceledException) continue;
                if (!failures.Any(existing => ReferenceEquals(existing, error))) failures.Add(error);
            }
        }

        private static void LogDrainStartup(string phase, WebServer server, string prefix,
            Task entered, Task? running = null, Task? response = null)
        {
            try { WriteDrainStartupSnapshot(phase, server, prefix, entered, running, response); }
            catch (Exception error) when (RecoverableDrainObservation(error))
            {
                // Observation is never allowed to replace the real failure.
                TestContext.Out.WriteLine("Drain startup observation unavailable: " + error);
            }
        }

        private static bool RecoverableDrainObservation(Exception error)
        {
            if (error is OutOfMemoryException or StackOverflowException or AccessViolationException) return false;
            if (error is AggregateException aggregate)
                return aggregate.InnerExceptions.All(RecoverableDrainObservation);
            return error.InnerException == null || RecoverableDrainObservation(error.InnerException);
        }

        private static void WriteDrainStartupSnapshot(string phase, WebServer server, string prefix,
            Task entered, Task? running, Task? response)
        {
            const BindingFlags hiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;
            var type = server.Listener.GetType();
            var session = type.GetNestedType("Session", BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing actual HTTP/3 session type.");
            var resolve = session.GetMethod("Resolve", hiddenStatic)
                ?? throw new AssertionException("Missing actual HTTP/3 resolver.");
            var addresses = resolve.Invoke(null, new object[] { new Uri(prefix).Host }) as IEnumerable<IPAddress>
                ?? throw new AssertionException("Unexpected actual resolved-address shape.");
            var endpoints = addresses.Select(address => new IPEndPoint(address, new Uri(prefix).Port)).ToArray();
            TestContext.Out.WriteLine($"Drain startup phase={phase}; prefix={prefix}; UseIpv6={EmbedIO.Net.EndPointManager.UseIpv6}; OSIPv6={Socket.OSSupportsIPv6}; resolved={string.Join(",", endpoints.Select(endpoint => endpoint.ToString()))}; entered={entered.Status}; running={running?.Status}; client={response?.Status}; state={server.State}");
        }
    }
}
