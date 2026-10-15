using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using EmbedIO.Actions;
using System.Reflection;
using EmbedIO.Testing;


using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class ServerTelemetryObserverRegressionTest
    {
        [TestCase("http.server.active_requests")]
        [TestCase("http.server.request.duration")]
        [TestCase("http.server.response.body.size")]
        public async Task ThrowingCompletionMetricObserverStillStopsRequestActivity(string instrumentName)
        {
            var name = "observer-regression-" + Guid.NewGuid().ToString("N");
            var observed = 0;
            var stopped = 0;
            using var spans = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "EmbedIO.Server",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (Equals(activity.GetTagItem("embedio.server.name"), name)) stopped++;
                },
            };
            ActivitySource.AddActivityListener(spans);
            using var meters = new MeterListener();
            meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "EmbedIO.Server" && instrument.Name == instrumentName)
                    listener.EnableMeasurementEvents(instrument);
            };
            meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (instrument.Name == "http.server.active_requests" && value >= 0) return;
                foreach (var tag in tags)
                    if (tag.Key == "embedio.server.name" && Equals(tag.Value, name))
                    {
                        observed++;
                        throw new InvalidOperationException("Deliberate observer failure.");
                    }
            });
            meters.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "embedio.server.name" && Equals(tag.Value, name))
                    {
                        observed++;
                        throw new InvalidOperationException("Deliberate observer failure.");
                    }
            });
            meters.Start();
            var telemetryType = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.ServerTelemetry", true) ?? throw new AssertionException("Missing telemetry type.");
            const BindingFlags internalStatic = BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags internalInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            var create = telemetryType.GetMethod("Create", internalStatic) ?? throw new AssertionException("Missing telemetry factory.");
            var start = telemetryType.GetMethod("Start", internalInstance) ?? throw new AssertionException("Missing telemetry start method.");
            var finish = telemetryType.GetMethod("Stop", internalInstance) ?? throw new AssertionException("Missing telemetry stop method.");
            await TestWebServer.UseAsync(server => server.OnAny(context =>
            {
                var previous = Activity.Current;
                var telemetry = create.Invoke(null, new object[] { name });
                var scope = start.Invoke(telemetry, new object[] { context.GetImplementation() }) ?? throw new AssertionException("Missing request telemetry scope.");
                var activityProperty = scope.GetType().GetProperty("Activity", internalInstance) ?? throw new AssertionException("Missing request activity property.");
                var activity = (Activity?)activityProperty.GetValue(scope);
                Assert.That(activity, Is.Not.Null);
                try
                {
                    Assert.DoesNotThrow(() => finish.Invoke(telemetry, new[] { context.GetImplementation(), scope }));
                    Assert.That(observed, Is.EqualTo(1), "The intended completion observer must throw.");
                    Assert.That(stopped, Is.EqualTo(1), "The request activity must still finish.");
                    Assert.That(Activity.Current, Is.SameAs(previous), "Restore the ambient parent activity.");
                }
                finally { activity?.Stop(); }
                return context.SendStringAsync("ok", "text/plain", WebServer.DefaultEncoding);
            }), async client => Assert.That(await client.GetStringAsync("/"), Is.EqualTo("ok")));
        }
    }
}
