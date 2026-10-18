using System;
using System.Linq;
using System.Net.Http;
using EmbedIO.Security;

namespace EmbedIO.Tests
{
    internal static class SecurityHeadersFixtures
    {
        internal static SecurityHeadersPolicy NoncePolicy() =>
            new(
                "default-src 'self'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'",
                "script-src 'nonce-{nonce}'"
            );

        internal static string Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values)
                ? string.Join(", ", values)
                : response.Content.Headers.TryGetValues(name, out values) ? string.Join(", ", values) : "";
    }
}
