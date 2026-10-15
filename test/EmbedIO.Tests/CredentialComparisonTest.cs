using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The credential comparison runs in time that does not depend on how many characters
    // matched, and an unknown user costs the same work as a known one. Timing itself is not
    // asserted here; these cases pin that the comparison still decides correctly, including
    // the lengths and the absent-account path that the fixed-time form has to get right.
    public class CredentialComparisonTest
    {
        [TestCase("operator", "s3cret", HttpStatusCode.OK, TestName = "the right password is accepted")]
        [TestCase("operator", "s3crew", HttpStatusCode.Unauthorized, TestName = "a wrong password of the same length is refused")]
        [TestCase("operator", "s3cretx", HttpStatusCode.Unauthorized, TestName = "a longer wrong password is refused")]
        [TestCase("operator", "s3cre", HttpStatusCode.Unauthorized, TestName = "a shorter wrong password is refused")]
        [TestCase("operator", "", HttpStatusCode.Unauthorized, TestName = "an empty password is refused when the account has one")]
        [TestCase("nobody", "s3cret", HttpStatusCode.Unauthorized, TestName = "an unknown user is refused")]
        [TestCase("nobody", "", HttpStatusCode.Unauthorized, TestName = "an unknown user with an empty password is refused")]
        [TestCase("blank", "", HttpStatusCode.OK, TestName = "an account whose password is empty accepts an empty password")]
        [TestCase("blank", "anything", HttpStatusCode.Unauthorized, TestName = "an account whose password is empty refuses a password")]
        public async Task CredentialsDecideAsConfigured(string userName, string password, HttpStatusCode expected)
        {
            await TestWebServer.UseAsync(
                server =>
                {
                    var module = new BasicAuthenticationModule("/");
                    module.Accounts["operator"] = "s3cret";
                    module.Accounts["blank"] = string.Empty;
                    server.WithModule(module);
                    server.OnAny("/", context => context.SendStringAsync("in", "text/plain", WebServer.Utf8NoBomEncoding));
                },
                async client =>
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
                    using var response = await client.GetAsync("/").ConfigureAwait(false);
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                }).ConfigureAwait(false);
        }

        // A nonexistent account is compared against an internal sentinel. Presenting a password
        // that could collide with it must still be refused, because the result is combined with
        // whether the account exists at all.
        [Test]
        public async Task ASentinelShapedPasswordDoesNotAdmitAnUnknownUser()
        {
            await TestWebServer.UseAsync(
                server =>
                {
                    var module = new BasicAuthenticationModule("/");
                    module.Accounts["operator"] = "s3cret";
                    server.WithModule(module);
                    server.OnAny("/", context => context.SendStringAsync("in", "text/plain", WebServer.Utf8NoBomEncoding));
                },
                async client =>
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("nobody:" + new string('\0', 32))));
                    using var response = await client.GetAsync("/").ConfigureAwait(false);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                }).ConfigureAwait(false);
        }
    }
}
