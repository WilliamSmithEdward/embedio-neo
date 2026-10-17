using System.Net;
using System.Text.Json;
using EmbedIO.Health;

namespace EmbedIO.AotSmoke;

internal static class HealthChecks
{
    internal static HealthCheckService Create(WebServer server)
    {
        var service = new HealthCheckService(server, new[] { new HealthCheckRegistration("application", _ => Task.FromResult(new HealthCheckResult(HealthStatus.Healthy, "native-ready"))) });
        server.WithModule(new HealthCheckModule(service));
        server.WithModule(new HealthCheckModule(service, "/internal/health/", includeDetails: true,
            authorize: context => Task.FromResult(context.Request.Headers["X-Smoke-Health"] == "allowed")));
        return service;
    }
    internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks, HealthCheckService service)
    {
        await service.WaitForReadyAsync(TimeSpan.FromSeconds(3));
        using var ready = await client.GetAsync(url + "health/ready");
        if (ready.StatusCode != HttpStatusCode.OK || await ready.Content.ReadAsStringAsync() != "Healthy") throw new InvalidOperationException("AOT readiness failed.");
        using var denied = await client.GetAsync(url + "internal/health/ready");
        if (denied.StatusCode != HttpStatusCode.Forbidden) throw new InvalidOperationException("AOT health authorization failed.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url + "internal/health/ready"); request.Headers.Add("X-Smoke-Health", "allowed");
        using var allowed = await client.SendAsync(request);
        using var json = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
        if (allowed.StatusCode != HttpStatusCode.OK || json.RootElement.GetProperty("checks").GetProperty("application").GetProperty("description").GetString() != "native-ready") throw new InvalidOperationException("AOT health detail serialization failed.");
        checks["health_readiness"] = "in-process startup wait, minimal readiness, authorized reflection-free health JSON";
    }
}
