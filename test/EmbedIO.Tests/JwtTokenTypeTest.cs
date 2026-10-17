using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Jwt;
using EmbedIO.Tests.Issues;
using EmbedIO.WebApi;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class JwtBearerTokenValidatorTest
    {
        [TestCase(null)]
        [TestCase("JWT")]
        [TestCase("at+jwt")]
        [TestCase("custom-purpose")]
        public async Task EmptyTokenTypesPreserveExistingAcceptance(string? type)
        {
            var key = Key();
            Assert.That(await new JwtBearerTokenValidator(Options(key)).ValidateAsync(TypeToken(key, type)), Is.Not.Null);
        }

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("JWT", false)]
        [TestCase("AT+JWT", false)]
        [TestCase("at+jwt", true)]
        [TestCase("application/at+jwt", false)]
        [TestCase("custom-purpose", false)]
        public async Task ConfiguredTokenTypesRequireAnExactHeaderMatch(string? type, bool accepted)
        {
            var key = Key(); var options = Options(key); options.AllowedTokenTypes.Add("at+jwt");
            var principal = await new JwtBearerTokenValidator(options).ValidateAsync(TypeToken(key, type));
            Assert.That(principal != null, Is.EqualTo(accepted));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void TokenTypeConfigurationRejectsEmptyEntries(string? type)
        {
            var options = Options(Key());
            // Exercise invalid caller input without a null-forgiving operator.
            if (type == null) ((System.Collections.IList)options.AllowedTokenTypes).Add(null);
            else options.AllowedTokenTypes.Add(type);
            Assert.That(() => new JwtBearerTokenValidator(options), Throws.ArgumentException);
        }

        [Test]
        public async Task TokenTypesAreSnapshottedWithoutWeakeningAnExistingValidator()
        {
            var key = Key(); var options = Options(key); options.AllowedTokenTypes.Add("at+jwt");
            var validator = new JwtBearerTokenValidator(options);
            options.AllowedTokenTypes.Clear(); options.AllowedTokenTypes.Add("JWT");
            Assert.That(await validator.ValidateAsync(TypeToken(key, "at+jwt")), Is.Not.Null);
            Assert.That(await validator.ValidateAsync(TypeToken(key, "JWT")), Is.Null);
            var unrestrictedOptions = Options(key); var unrestricted = new JwtBearerTokenValidator(unrestrictedOptions);
            unrestrictedOptions.AllowedTokenTypes.Add("at+jwt");
            Assert.That(await unrestricted.ValidateAsync(TypeToken(key, null)), Is.Not.Null);
        }

        [Test]
        public async Task MultipleExplicitTypesAndDuplicateEntriesAreSupported()
        {
            var key = Key(); var options = Options(key);
            foreach (var type in new[] { "at+jwt", "application/at+jwt", "at+jwt" }) options.AllowedTokenTypes.Add(type);
            var validator = new JwtBearerTokenValidator(options);
            foreach (var type in new[] { "at+jwt", "application/at+jwt" }) Assert.That(await validator.ValidateAsync(TypeToken(key, type)), Is.Not.Null);
            Assert.That(await validator.ValidateAsync(TypeToken(key, "JWT")), Is.Null);
        }

        [TestCase("wrong-key")]
        [TestCase("expired")]
        [TestCase("wrong-audience")]
        [TestCase("algorithm")]
        public async Task MatchingTokenTypeDoesNotReplaceOtherTrustChecks(string failure)
        {
            var key = Key(); var options = Options(key); options.AllowedTokenTypes.Add("at+jwt");
            var encoded = failure switch
            {
                "wrong-key" => TypeToken(Key(23), "at+jwt"),
                "expired" => TypeToken(key, "at+jwt", Token(key, expires: DateTime.UtcNow.AddMinutes(-1))),
                "wrong-audience" => TypeToken(key, "at+jwt", Token(key, audience: "other-api")),
                "algorithm" => TypeToken(key, "at+jwt", Token(key, algorithm: SecurityAlgorithms.HmacSha512), "HS512"),
                _ => throw new ArgumentOutOfRangeException(nameof(failure)),
            };
            Assert.That(await new JwtBearerTokenValidator(options).ValidateAsync(encoded), Is.Null);
        }

        [TestCase("123")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("true")]
        public async Task NonStringTokenTypeIsRejected(string rawType)
        {
            var key = Key(); var options = Options(key); options.AllowedTokenTypes.Add("at+jwt");
            var encoded = SignHeader(key, "{\"alg\":\"HS256\",\"typ\":" + rawType + "}", Token(key));
            Assert.That(await new JwtBearerTokenValidator(options).ValidateAsync(encoded), Is.Null);
        }

        [Test]
        public async Task PayloadTypeCannotSubstituteForTheSignedHeader()
        {
            var key = Key(); var options = Options(key); options.AllowedTokenTypes.Add("at+jwt");
            var token = TypeToken(key, "JWT"); var parts = token.Split('.');
            // An attacker edits only the header of an otherwise valid token.
            parts[0] = Base64UrlEncoder.Encode("{\"alg\":\"HS256\",\"typ\":\"at+jwt\"}");
            Assert.That(await new JwtBearerTokenValidator(options).ValidateAsync(string.Join(".", parts)), Is.Null);
            var payload = Base64UrlEncoder.Decode(token.Split('.')[1]);
            payload = payload.Substring(0, payload.Length - 1) + ",\"typ\":\"at+jwt\"}";
            var withPayloadType = token.Split('.'); withPayloadType[1] = Base64UrlEncoder.Encode(payload);
            Assert.That(await new JwtBearerTokenValidator(options).ValidateAsync(SignHeader(key, "{\"alg\":\"HS256\"}", string.Join(".", withPayloadType))), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task WrongTokenPurposeIsRejectedBeforeControllerActivation(bool generated)
        {
            JwtProbeController.Activations = 0; JwtProbeBodyAttribute.Reads = 0;
            var key = Key(); var options = Options(key); options.AllowedTokenTypes.Add("at+jwt");
            var validator = new JwtBearerTokenValidator(options);
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            if (generated) module.WithController(JwtProbeBindings.JwtProbeController);
            else module.WithController<JwtProbeController>();
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new BearerAuthenticationModule("/api", validator.ValidateAsync), module);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            foreach (var type in new string?[] { "JWT", null })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, server.Url + "api/upload") { Content = new StringContent("not-bound") };
                request.Headers.Authorization = new("Bearer", TypeToken(key, type));
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(response.Headers.WwwAuthenticate.Single().ToString(), Does.Contain("invalid_token"));
                Assert.That(JwtProbeController.Activations, Is.Zero); Assert.That(JwtProbeBodyAttribute.Reads, Is.Zero);
            }
            using var accepted = new HttpRequestMessage(HttpMethod.Post, server.Url + "api/upload") { Content = new StringContent("posted") };
            accepted.Headers.Authorization = new("Bearer", TypeToken(key, "at+jwt"));
            using var result = await client.SendAsync(accepted);
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await result.Content.ReadAsStringAsync(), Is.EqualTo("alice:posted"));
            Assert.That(JwtProbeController.Activations, Is.EqualTo(1)); Assert.That(JwtProbeBodyAttribute.Reads, Is.EqualTo(1));
        }

        private static string TypeToken(SymmetricSecurityKey key, string? type, string? token = null, string algorithm = "HS256")
        {
            var header = new Dictionary<string, string> { ["alg"] = algorithm, ["kid"] = "trusted" };
            if (type != null) header["typ"] = type;
            return SignHeader(key, JsonSerializer.Serialize(header), token ?? Token(key), algorithm);
        }

        private static string SignHeader(SymmetricSecurityKey key, string header, string token, string algorithm = "HS256")
        {
            var input = Base64UrlEncoder.Encode(header) + "." + token.Split('.')[1];
            using HMAC hmac = algorithm == "HS512" ? new HMACSHA512(key.Key) : new HMACSHA256(key.Key);
            return input + "." + Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(input)));
        }
    }
}
