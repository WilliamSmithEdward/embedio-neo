using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Health;

internal static class LegacyHealthChecks
{
    internal static async Task<int> RunAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var url = "http://127.0.0.1:" + port + "/";
        using var server = new WebServer(HttpListenerMode.EmbedIO, url);
        var ready = 0;
        using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("application", _ => Task.FromResult(new HealthCheckResult(Volatile.Read(ref ready) == 0 ? HealthStatus.Unhealthy : HealthStatus.Healthy, "legacy-ready"))) });
        server.WithModule(new HealthCheckModule(health));
        server.WithModule(new HealthCheckModule(health, "/internal/health/", includeDetails: true,
            authorize: context => Task.FromResult(context.Request.Headers["X-Smoke-Health"] == "allowed")));
        using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var unavailable = await client.GetAsync(url + "health/ready");
            Require(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "unavailable readiness");
            Require(health.CheckLiveness().Status == HealthStatus.Healthy, "dependency-independent liveness");
            Volatile.Write(ref ready, 1); await health.WaitForReadyAsync(TimeSpan.FromSeconds(3));
            using var available = await client.GetAsync(url + "health/ready");
            Require(available.StatusCode == HttpStatusCode.OK && await available.Content.ReadAsStringAsync() == "Healthy", "healthy readiness");
            using var denied = await client.GetAsync(url + "internal/health/ready");
            Require(denied.StatusCode == HttpStatusCode.Forbidden, "protected diagnostics");
            using var request = new HttpRequestMessage(HttpMethod.Get, url + "internal/health/ready"); request.Headers.Add("X-Smoke-Health", "allowed");
            using var allowed = await client.SendAsync(request);
            using var json = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
            Require(allowed.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("checks").GetProperty("application").GetProperty("description").GetString() == "legacy-ready", "diagnostic serialization");
        }
        finally { stop.Cancel(); await running; }
        Require((await health.CheckReadinessAsync()).Status == HealthStatus.Unhealthy, "stop invalidates readiness");
        return 6;
    }
    private static void Require(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException("Legacy health check failed: " + detail); }
}
