using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Jwt;
using EmbedIO.Authentication;
using EmbedIO.Authorization;
using EmbedIO.Routing;
using EmbedIO.Tests.Issues;
using EmbedIO.WebApi;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public sealed partial class JwtBearerTokenValidatorTest
    {
        private const string Issuer = "https://issuer.example.test";
        private const string Audience = "https://api.example.test";

        [TestCase(false)]
        [TestCase(true)]
        public async Task JwtModuleRejectsInvalidCredentialsBeforeControllerActivationAndBodyBinding(bool generated)
        {
            JwtProbeController.Activations = 0;
            JwtProbeBodyAttribute.Reads = 0;
            var key = Key();
            var validator = new JwtBearerTokenValidator(Options(key));
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            if (generated) module.WithController(JwtProbeBindings.JwtProbeController);
            else module.WithController<JwtProbeController>();
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new BearerAuthenticationModule("/api", validator.ValidateAsync), module);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            using (var rejected = new HttpRequestMessage(HttpMethod.Post, server.Url + "api/upload"))
            {
                rejected.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(Key(23)));
                rejected.Content = new StringContent("must-not-be-bound");
                using var response = await client.SendAsync(rejected);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(response.Headers.WwwAuthenticate.Single().ToString(), Does.Contain("invalid_token"));
                Assert.That(JwtProbeController.Activations, Is.Zero);
                Assert.That(JwtProbeBodyAttribute.Reads, Is.Zero);
            }
            using var accepted = new HttpRequestMessage(HttpMethod.Post, server.Url + "api/upload");
            accepted.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(key));
            accepted.Content = new StringContent("posted");
            using var result = await client.SendAsync(accepted);
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await result.Content.ReadAsStringAsync(), Is.EqualTo("alice:posted"));
            Assert.That(JwtProbeController.Activations, Is.EqualTo(1));
            Assert.That(JwtProbeBodyAttribute.Reads, Is.EqualTo(1));
        }

        private static SymmetricSecurityKey Key(byte marker = 17)
            => new(Enumerable.Repeat(marker, 64).ToArray()) { KeyId = "trusted" };

        private static JwtBearerOptions Options(SecurityKey key, string algorithm = SecurityAlgorithms.HmacSha256)
        {
            var options = new JwtBearerOptions { Issuer = Issuer, Audience = Audience };
            options.SigningKeys.Add(key);
            options.AllowedAlgorithms.Add(algorithm);
            return options;
        }

        private static string Token(SecurityKey? key, string subject = "alice", string? issuer = Issuer,
            string? audience = Audience, string algorithm = SecurityAlgorithms.HmacSha256,
            DateTime? expires = null, DateTime? notBefore = null, bool expiration = true)
        {
            var now = DateTime.UtcNow;
            return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Subject = new ClaimsIdentity(new[] { new Claim("sub", subject), new Claim("role", "editor"), new Claim("scope", "files:write") }),
                Expires = expiration ? expires ?? now.AddMinutes(10) : null,
                NotBefore = notBefore ?? now.AddMinutes(-10),
                SigningCredentials = key == null ? null : new SigningCredentials(key, algorithm),
            });
        }

        [Test]
        public async Task SignedTokenPreservesClaimsAndAuthenticatedIdentity()
        {
            var key = Key();
            var principal = await new JwtBearerTokenValidator(Options(key)).ValidateAsync(Token(key));
            Assert.That(principal?.Identity?.IsAuthenticated, Is.True);
            Assert.That(principal?.Identity?.AuthenticationType, Is.EqualTo("Bearer"));
            Assert.That(principal?.Identity?.Name, Is.EqualTo("alice"));
            Assert.That(principal?.IsInRole("editor"), Is.True);
            Assert.That(principal?.FindFirst("scope")?.Value, Is.EqualTo("files:write"));
            Assert.That(principal?.FindFirst(ClaimTypes.NameIdentifier), Is.Null, "Claim names are not remapped.");
        }

        [TestCase("wrong-key")]
        [TestCase("wrong-issuer")]
        [TestCase("wrong-audience")]
        [TestCase("audience-slash")]
        [TestCase("missing-issuer")]
        [TestCase("missing-audience")]
        [TestCase("expired")]
        [TestCase("future")]
        [TestCase("missing-expiration")]
        [TestCase("unsigned")]
        [TestCase("algorithm")]
        [TestCase("tampered")]
        public async Task InvalidTrustOrLifetimeReturnsNoPrincipal(string kind)
        {
            var key = Key();
            var encoded = kind switch
            {
                "wrong-key" => Token(Key(23)),
                "wrong-issuer" => Token(key, issuer: "https://untrusted.example.test"),
                "wrong-audience" => Token(key, audience: "other-api"),
                "audience-slash" => Token(key, audience: Audience + "/"),
                "missing-issuer" => Token(key, issuer: null),
                "missing-audience" => Token(key, audience: null),
                "expired" => Token(key, expires: DateTime.UtcNow.AddMinutes(-1)),
                "future" => Token(key, notBefore: DateTime.UtcNow.AddMinutes(5)),
                "missing-expiration" => Token(key, expiration: false),
                "unsigned" => Token(null),
                "algorithm" => Token(key, algorithm: SecurityAlgorithms.HmacSha512),
                "tampered" => Tamper(Token(key)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            Assert.That(await new JwtBearerTokenValidator(Options(key)).ValidateAsync(encoded), Is.Null);
        }

        private static string Tamper(string token)
        {
            var parts = token.Split('.');
            parts[1] = Base64UrlEncoder.Encode(Base64UrlEncoder.Decode(parts[1]).Replace("alice", "mallory", StringComparison.Ordinal));
            return string.Join(".", parts);
        }

        [TestCase("")]
        [TestCase("not-a-token")]
        [TestCase("a.b.c")]
        [TestCase("e30.e30.")]
        [TestCase("a.b.c.d.e")]
        [TestCase("\r\n")]
        public async Task MalformedTokensReturnNoPrincipal(string token)
            => Assert.That(await new JwtBearerTokenValidator(Options(Key())).ValidateAsync(token), Is.Null);

        [Test]
        public async Task SizeBoundIsInclusive()
        {
            var key = Key();
            var token = Token(key);
            var options = Options(key);
            options.MaxTokenSizeInBytes = token.Length;
            Assert.That(await new JwtBearerTokenValidator(options).ValidateAsync(token), Is.Not.Null);
            options.MaxTokenSizeInBytes--;
            Assert.That(await new JwtBearerTokenValidator(options).ValidateAsync(token), Is.Null);
        }

        [Test]
        public async Task ExplicitClockSkewCanPermitRecentlyExpiredToken()
        {
            var key = Key();
            var options = Options(key);
            options.ClockSkew = TimeSpan.FromMinutes(2);
            Assert.That(await new JwtBearerTokenValidator(options).ValidateAsync(Token(key, expires: DateTime.UtcNow.AddMinutes(-1))), Is.Not.Null);
        }

        [Test]
        public async Task SettingsAndKeyMaterialAreSnapshotted()
        {
            var key = Key();
            var token = Token(key);
            var jwk = JsonWebKeyConverter.ConvertFromSecurityKey(key);
            var options = Options(jwk);
            var validator = new JwtBearerTokenValidator(options);
            jwk.K = Base64UrlEncoder.Encode(Key(31).Key);
            jwk.Kid = "changed";
            options.Issuer = "changed";
            options.Audience = "changed";
            options.NameClaimType = "changed";
            options.RoleClaimType = "changed";
            options.ClockSkew = TimeSpan.FromDays(100);
            options.MaxTokenSizeInBytes = 1;
            options.SigningKeys.Clear();
            options.AllowedAlgorithms.Clear();
            var principal = await validator.ValidateAsync(token);
            Assert.That(principal?.Identity?.Name, Is.EqualTo("alice"));
            Assert.That(principal?.IsInRole("editor"), Is.True);
            Assert.That(await validator.ValidateAsync(Token(key, expires: DateTime.UtcNow.AddMinutes(-1))), Is.Null);
            Assert.That(await validator.ValidateAsync(Token(Key(31))), Is.Null);
        }

        [Test]
        public async Task CancellationDoesNotBecomeInvalidCredentials()
        {
            var validator = new JwtBearerTokenValidator(Options(Key()));
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await validator.ValidateAsync("invalid", new CancellationToken(true)));
        }

        [Test]
        public async Task ConcurrentTokensProduceIndependentPrincipals()
        {
            var key = Key();
            var validator = new JwtBearerTokenValidator(Options(key));
            var tokens = Enumerable.Range(0, 40).Select(index => Token(key, "user" + index)).ToArray();
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = tokens.Select(token => Task.Run(async () =>
            {
                await start.Task;
                return await validator.ValidateAsync(token);
            })).ToArray();
            start.SetResult(true);
            var results = await Task.WhenAll(calls);
            Assert.That(results.Select(principal => principal?.Identity?.Name), Is.EqualTo(Enumerable.Range(0, 40).Select(index => "user" + index)));
            var first = results[0]?.Identity as ClaimsIdentity ?? throw new AssertionException("Missing identity.");
            first.AddClaim(new Claim("local-mutation", "yes"));
            Assert.That(results.Skip(1).All(principal => principal?.HasClaim("local-mutation", "yes") == false), Is.True);
        }

        [Test]
        public async Task RsaVerificationSurvivesCallerKeyDisposalAndRejectsHmacConfusion()
        {
            JwtBearerTokenValidator validator;
            string valid;
            string confused;
            using (var rsa = RSA.Create(2048))
            {
                var key = new RsaSecurityKey(rsa) { KeyId = "rsa" };
                var options = Options(key, SecurityAlgorithms.RsaSha256);
                options.AllowedAlgorithms.Add(SecurityAlgorithms.HmacSha256);
                validator = new JwtBearerTokenValidator(options);
                valid = Token(key, algorithm: SecurityAlgorithms.RsaSha256);
                confused = Token(new SymmetricSecurityKey(rsa.ExportSubjectPublicKeyInfo()) { KeyId = "rsa" });
            }
            Assert.That(await validator.ValidateAsync(valid), Is.Not.Null);
            Assert.That(await validator.ValidateAsync(confused), Is.Null, "RSA public bytes cannot become a trusted HMAC secret.");
        }

        [Test]
        public async Task EcVerificationUsesPublicKeySnapshot()
        {
            JwtBearerTokenValidator validator;
            string token;
            using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var key = new ECDsaSecurityKey(ec) { KeyId = "ec" };
                validator = new JwtBearerTokenValidator(Options(key, SecurityAlgorithms.EcdsaSha256));
                token = Token(key, algorithm: SecurityAlgorithms.EcdsaSha256);
            }
            Assert.That(await validator.ValidateAsync(token), Is.Not.Null);
        }

        [TestCase("encryption-use")]
        [TestCase("sign-only")]
        [TestCase("missing-material")]
        public void JwkMustContainVerificationMaterialAndPermitVerification(string kind)
        {
            var key = JsonWebKeyConverter.ConvertFromSecurityKey(Key());
            if (kind == "encryption-use") key.Use = "enc";
            else if (kind == "sign-only") key.KeyOps.Add("sign");
            else key.K = string.Empty;
            Assert.That(() => new JwtBearerTokenValidator(Options(key)), Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public async Task ExplicitNameAndRoleClaimsAreHonored()
        {
            var key = Key();
            var options = Options(key);
            options.NameClaimType = "scope";
            options.RoleClaimType = "sub";
            var principal = await new JwtBearerTokenValidator(options).ValidateAsync(Token(key));
            Assert.That(principal?.Identity?.Name, Is.EqualTo("files:write"));
            Assert.That(principal?.IsInRole("alice"), Is.True);
            Assert.That(principal?.IsInRole("editor"), Is.False);
        }

        [TestCase("issuer")]
        [TestCase("audience")]
        [TestCase("keys")]
        [TestCase("algorithms")]
        [TestCase("none")]
        [TestCase("clock")]
        [TestCase("size")]
        public void TrustConfigurationMustBeExplicit(string kind)
        {
            var options = Options(Key());
            switch (kind)
            {
                case "issuer": options.Issuer = " "; break;
                case "audience": options.Audience = ""; break;
                case "keys": options.SigningKeys.Clear(); break;
                case "algorithms": options.AllowedAlgorithms.Clear(); break;
                case "none": options.AllowedAlgorithms.Add("none"); break;
                case "clock": options.ClockSkew = TimeSpan.FromSeconds(-1); break;
                case "size": options.MaxTokenSizeInBytes = 0; break;
            }
            Assert.That(() => new JwtBearerTokenValidator(options), Throws.InstanceOf<ArgumentException>());
        }
    }

    [GeneratedControllerBinding(typeof(JwtProbeController))]
    internal static partial class JwtProbeBindings { }

    public sealed class JwtProbeController : WebApiController
    {
        internal static int Activations;
        public JwtProbeController() => Interlocked.Increment(ref Activations);

        [Route(HttpVerbs.Post, "/upload")]
        [Authorize(Roles = "editor")]
        public string Upload([JwtProbeBody] string body) => HttpContext.User.Identity?.Name + ":" + body;
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class JwtProbeBodyAttribute : Attribute, IRequestDataAttribute<WebApiController, string>
    {
        internal static int Reads;
        public async Task<string?> GetRequestDataAsync(WebApiController controller, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(controller);
            Interlocked.Increment(ref Reads);
            return await controller.HttpContext.GetRequestBodyAsStringAsync();
        }
    }
}
