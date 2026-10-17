using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EmbedIO.Authentication;
using EmbedIO.Authorization;
using EmbedIO.OpenApi;
using EmbedIO.Jwt;
using Microsoft.IdentityModel.Tokens;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke;

internal static class AuthorizationChecks
{
    internal static BearerAuthenticationModule CreateBearer() => new("/auth", (token, _) =>
        Task.FromResult<ClaimsPrincipal?>(token == "smoke" ? new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "smoke"), new Claim(ClaimTypes.Role, "reader") }, "Bearer")) : null));

    internal static WebApiModule CreateModule()
    {
        var module = new WebApiModule("/auth", ResponseSerializer.Json(SmokeJson.Default.Options));
        module.ConfigureAuthorization(new AuthorizationOptions().AddPolicy("smoke", (context, _) => Task.FromResult(context.User.Identity?.Name == "smoke")));
        module.RegisterController(AuthorizationControllers.AuthorizedSmokeController);
        return module;
    }

    internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks)
    {
        await CheckJwtAsync(checks);
        using (var denied = await client.GetAsync(url + "auth/secure"))
            Require(denied.StatusCode == HttpStatusCode.Unauthorized, "AOT authorization did not deny an anonymous request.");
        using (var request = new HttpRequestMessage(HttpMethod.Get, url + "auth/secure"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "smoke");
            using var accepted = await client.SendAsync(request);
            Require(accepted.StatusCode == HttpStatusCode.OK, "AOT inherited authorization rejected a valid identity.");
        }
        using (var anonymous = await client.GetAsync(url + "auth/public"))
            Require(anonymous.StatusCode == HttpStatusCode.OK, "AOT inherited AllowAnonymous was lost.");

        using (var hooked = await client.GetAsync(url + "auth/hooked"))
        {
            Require(hooked.StatusCode == HttpStatusCode.Accepted && hooked.Headers.GetValues("X-Hook").Single() == "async",
                "AOT direct asynchronous hook or enum status was lost.");
            Require(hooked.Headers.CacheControl?.NoStore != true, "Replace unexpectedly ran the controller hook.");
        }
        using (var skipped = await client.GetAsync(url + "auth/skipped"))
            Require(skipped.StatusCode == HttpStatusCode.NoContent && !skipped.Headers.Contains("X-Hook"), "AOT Skip ran the class hook.");
        checks["before_handler_generated"] = "async class Replace, action Skip, enum statuses, authorization before hooks";

        var document = new OpenApiDocumentBuilder("AOT authorization", "1")
            .AddController("/auth", AuthorizationControllers.AuthorizedSmokeController)
            .AddBearerAuthentication("bearer", CreateBearer())
            .Build(SmokeJson.Default.Options);
        var secure = document.Root["paths"]?["/auth/secure"]?["get"];
        Require(secure?["security"]?[0]?["bearer"]?.AsArray().Count == 0
            && secure?["responses"]?["401"] != null && secure?["responses"]?["403"] != null,
            "AOT OpenAPI lost authorization metadata.");
        Require(document.Root["paths"]?["/auth/public"]?["get"]?["x-embedio-authorization"]?["allowAnonymous"]?.GetValue<bool>() == true,
            "AOT OpenAPI lost inherited anonymous metadata.");
        Require(document.Root["paths"]?["/auth/hooked"]?["get"]?["responses"]?["202"] != null,
            "AOT OpenAPI lost enum response metadata.");
        checks["authorization_inherited_generated"] = "401, authenticated 200, anonymous override 200, OpenAPI";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CheckJwtAsync(SortedDictionary<string, string> checks)
    {
        // Public test-only key and payload; token issuance is not part of the production API.
        var key = Encoding.ASCII.GetBytes("aot-smoke-public-test-key-32bytes!");
        var options = new JwtBearerOptions { Issuer = "aot-smoke", Audience = "aot-client" };
        options.SigningKeys.Add(new SymmetricSecurityKey(key));
        options.AllowedAlgorithms.Add("HS256");
        options.AllowedTokenTypes.Add("JWT");
        var validator = new JwtBearerTokenValidator(options);
        var unsigned = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")) + "."
            + Encode(Encoding.UTF8.GetBytes("{\"iss\":\"aot-smoke\",\"aud\":\"aot-client\",\"sub\":\"smoke\",\"role\":\"reader\",\"nbf\":1700000000,\"exp\":4102444800}"));
        var signature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(unsigned));
        var principal = await validator.ValidateAsync(unsigned + "." + Encode(signature), CancellationToken.None);
        Require(principal?.Identity?.IsAuthenticated == true && principal.Identity.Name == "smoke" && principal.IsInRole("reader"),
            "AOT JWT signature or claims validation failed.");
        foreach (var header in new[] { "{\"alg\":\"HS256\",\"typ\":\"at+jwt\"}", "{\"alg\":\"HS256\"}" })
        {
            var other = Encode(Encoding.UTF8.GetBytes(header)) + "." + unsigned.Split('.')[1];
            var otherSignature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(other));
            Require(await validator.ValidateAsync(other + "." + Encode(otherSignature), CancellationToken.None) == null,
                "AOT JWT accepted a wrong or missing token type.");
        }
        checks["jwt_token_types"] = "configured JWT accepted; signed at+jwt and missing typ rejected";
        signature[0] ^= 1;
        Require(await validator.ValidateAsync(unsigned + "." + Encode(signature), CancellationToken.None) == null,
            "AOT JWT accepted a modified signature.");
        checks["jwt_validation"] = "HS256 signature and claims accepted; modified signature rejected";
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

[Authorize(Roles = "reader")]
internal abstract class AuthorizedSmokeBase : WebApiController
{
    [Authorize("smoke")]
    [Route(HttpVerbs.Get, "/secure")]
    public virtual string Secure() => "secure";

    [AllowAnonymous]
    [Route(HttpVerbs.Get, "/public")]
    public virtual string Public() => "public";
}

[Authorize]
[BeforeHandler(nameof(Prepare), Behavior = BeforeHandlerBehavior.Replace)]
internal sealed class AuthorizedSmokeController : AuthorizedSmokeBase
{
    public async Task Prepare(CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        Response.Headers["X-Hook"] = "async";
    }

    [AllowAnonymous, Route(HttpVerbs.Get, "/hooked"), OpenApiResponse(HttpStatusCode.Accepted)]
    public void Hooked() => Response.SetEmptyResponse(HttpStatusCode.Accepted);

    [AllowAnonymous, Route(HttpVerbs.Get, "/skipped")]
    [BeforeHandler(Behavior = BeforeHandlerBehavior.Skip)]
    public void Skipped() => Response.SetEmptyResponse(HttpStatusCode.NoContent);

    public override string Secure() => "secure";
    [Authorize("smoke")]
    public override string Public() => "public";
}

[GeneratedControllerBinding(typeof(AuthorizedSmokeController))]
internal static partial class AuthorizationControllers;
