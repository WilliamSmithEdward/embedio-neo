using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke
{
    internal static class SecurityHeaderChecks
    {
        internal static WebApiModule CreateModule()
        {
            var module = new WebApiModule("/security-headers", ResponseSerializer.None(false));
            module.ConfigureSecurityHeaders(new Dictionary<string, SecurityHeadersPolicy>
            {
                ["static"] = new("default-src 'none'"),
                ["nonce"] = new("script-src 'nonce-{nonce}'")
            });
            module.RegisterController(SecurityHeaderSmokeBindings.SecurityHeaderSmokeController);
            return module;
        }

        internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks)
        {
            string? previous = null;

            for (var i = 0; i < 3; i++)
            {
                using var response = await client.GetAsync(url + "security-headers/nonce");
                response.EnsureSuccessStatusCode();
                var nonce = await response.Content.ReadAsStringAsync();

                if (
                    Convert.FromBase64String(nonce).Length != 32
                        || nonce == previous
                        || response.Headers
                            .GetValues("Content-Security-Policy")
                            .Single()
                            != "script-src 'nonce-" + nonce + "'"
                        || response.Headers.CacheControl?.NoStore
                    != true
                )
                    throw new InvalidOperationException("Generated security-header nonce binding failed.");

                previous = nonce;
            }

            using var plain = await client.GetAsync(url + "security-headers/static");

            if (
                plain.Headers
                    .GetValues("Content-Security-Policy")
                    .Single()
                != "default-src 'none'"
            )
                throw new InvalidOperationException("Generated class security-header policy failed.");

            checks["security_headers"] = "generated class/action overrides, nonce/body binding, freshness and no-store";
        }
    }

    [GeneratedControllerBinding(typeof(SecurityHeaderSmokeController))]
    internal static partial class SecurityHeaderSmokeBindings { }

    [SecurityHeaders("static")]
    internal sealed class SecurityHeaderSmokeController : WebApiController
    {
        [Route(HttpVerbs.Get, "/static")]
        public string Plain() => HttpContext.RequestedPath;

        [Route(HttpVerbs.Get, "/nonce")]
        [SecurityHeaders("nonce")]
        public string Nonce() => HttpContext.GetSecurityHeadersNonce();
    }
}
