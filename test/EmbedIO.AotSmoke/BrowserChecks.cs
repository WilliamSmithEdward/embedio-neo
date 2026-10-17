using System.Net;
using EmbedIO.Security;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke;

internal static class BrowserChecks
{
    internal static WebApiModule CreateModule(BrowserRequestProtection protection)
    {
        var module = new WebApiModule("/browser", ResponseSerializer.None(false)); module.ConfigureBrowserProtection(protection);
        return module.WithController(BrowserSmokeBindings.BrowserSmokeController, () => new BrowserSmokeController(protection));
    }
    internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks)
    {
        using (var denied = await client.PostAsync(url + "browser/write", new StringContent("payload")))
            if (denied.StatusCode != HttpStatusCode.Forbidden) throw new InvalidOperationException("AOT missing token was accepted.");
        var token = await client.GetStringAsync(url + "browser/token");
        using var request = new HttpRequestMessage(HttpMethod.Post, url + "browser/write"); request.Headers.Add(BrowserRequestProtection.TokenHeaderName, token);
        using var allowed = await client.SendAsync(request);
        if (allowed.StatusCode != HttpStatusCode.OK || await allowed.Content.ReadAsStringAsync() != "accepted") throw new InvalidOperationException("AOT browser token failed.");
        using var exempt = await client.PostAsync(url + "browser/exempt", new StringContent("payload"));
        if (exempt.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("AOT browser exemption was lost.");
        checks["browser_protection"] = "generated controller attributes, cookie-bound token, denied missing token, explicit token exemption";
    }
}
[GeneratedControllerBinding(typeof(BrowserSmokeController))]
internal static partial class BrowserSmokeBindings { }
[BrowserProtection]
internal sealed class BrowserSmokeController : WebApiController
{
    private readonly BrowserRequestProtection _protection;
    internal BrowserSmokeController(BrowserRequestProtection protection) => _protection = protection;
    [Route(HttpVerbs.Get, "/token")]
    public string Token() => _protection.GetToken(HttpContext);
    [Route(HttpVerbs.Post, "/write")]
    public string Write() => "accepted";
    [Route(HttpVerbs.Post, "/exempt"), BrowserProtection(BrowserProtectionMode.IgnoreToken)]
    public string Exempt() => "explicit";
}
