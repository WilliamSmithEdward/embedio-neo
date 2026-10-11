using System;
using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RequestPreconditionsTest
    {
        public class RequestProxy : DispatchProxy
        {
            internal string Method = "QUERY";
            internal NameValueCollection Fields = new();
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
                => targetMethod?.Name switch
                {
                    "get_Headers" => Fields,
                    "get_HttpMethod" => Method,
                    _ => throw new InvalidOperationException("Unexpected precondition request access.")
                };
        }
        private static IHttpRequest Request(string method, params string[] fields)
        {
            var request = DispatchProxy.Create<IHttpRequest, RequestProxy>();
            var proxy = (RequestProxy)(object)request;
            proxy.Method = method;
            for (var index = 0; index < fields.Length; index += 2) proxy.Fields.Add(fields[index], fields[index + 1]);
            return request;
        }
        public static IEnumerable TagCases()
        {
            foreach (var method in new[] { "GET", "HEAD", "QUERY", "POST" })
            {
                var unchanged = method == "POST" ? HttpStatusCode.PreconditionFailed : HttpStatusCode.NotModified;
                yield return new object?[] { method, "If-Match", "\"v\"", "\"v\"", true, null };
                yield return new object?[] { method, "If-Match", "W/\"v\"", "\"v\"", true, HttpStatusCode.PreconditionFailed };
                yield return new object?[] { method, "If-Match", "\"v\"", "W/\"v\"", true, HttpStatusCode.PreconditionFailed };
                yield return new object?[] { method, "If-Match", "*", null, true, null };
                yield return new object?[] { method, "If-Match", "*", null, false, HttpStatusCode.PreconditionFailed };
                yield return new object?[] { method, "If-Match", "", "\"v\"", true, HttpStatusCode.PreconditionFailed };
                yield return new object?[] { method, "If-None-Match", "W/\"v\"", "\"v\"", true, unchanged };
                yield return new object?[] { method, "If-None-Match", "\"v\"", "W/\"v\"", true, unchanged };
                yield return new object?[] { method, "If-None-Match", "\"other\", W/\"v\"", "\"v\"", true, unchanged };
                yield return new object?[] { method, "If-None-Match", "\"a,b\"", "\"a,b\"", true, unchanged };
                yield return new object?[] { method, "If-None-Match", " , ,\"v\",, ", "\"v\"", true, unchanged };
                yield return new object?[] { method, "If-None-Match", "\"\"", "\"\"", true, unchanged };
                yield return new object?[] { method, "If-None-Match", "*", null, true, unchanged };
                yield return new object?[] { method, "If-None-Match", "*", null, false, null };
                yield return new object?[] { method, "If-None-Match", "\"v\"", "\"v\"", false, null };
                yield return new object?[] { method, "If-None-Match", "", "\"v\"", true, null };
                yield return new object?[] { method, "If-None-Match", "\"V\"", "\"v\"", true, null };
                yield return new object?[] { method, "If-None-Match", "\"other\"", "\"v\"", true, null };
            }
        }
        [TestCaseSource(nameof(TagCases))]
        public void StrongWeakListsAndExistenceHaveMethodSpecificResults(string method, string field, string value,
            string? current, bool exists, HttpStatusCode? expected)
            => Assert.That(Request(method, field, value).EvaluatePreconditions(current, null, exists), Is.EqualTo(expected));

        [TestCase("GET")]
        [TestCase("HEAD")]
        [TestCase("QUERY")]
        public void DateConditionsUseHttpPrecisionAndEntityTagsTakePrecedence(string method)
        {
            var modified = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(456);
            var same = modified.ToString("r", CultureInfo.InvariantCulture);
            var past = modified.AddDays(-1).ToString("r", CultureInfo.InvariantCulture);
            Assert.That(Request(method, "If-Modified-Since", same).EvaluatePreconditions("\"v\"", modified), Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(Request(method, "If-Modified-Since", past).EvaluatePreconditions("\"v\"", modified), Is.Null);
            Assert.That(Request(method, "If-Unmodified-Since", past).EvaluatePreconditions("\"v\"", modified), Is.EqualTo(HttpStatusCode.PreconditionFailed));
            Assert.That(Request(method, "If-Unmodified-Since", same).EvaluatePreconditions("\"v\"", modified), Is.Null);
            Assert.That(Request(method, "If-Match", "\"v\"", "If-Unmodified-Since", past).EvaluatePreconditions("\"v\"", modified), Is.Null);
            Assert.That(Request(method, "If-None-Match", "\"other\"", "If-Modified-Since", same).EvaluatePreconditions("\"v\"", modified), Is.Null);
            Assert.That(Request(method, "If-Match", "\"other\"", "If-None-Match", "\"v\"").EvaluatePreconditions("\"v\"", modified), Is.EqualTo(HttpStatusCode.PreconditionFailed));
            Assert.That(Request(method, "If-Modified-Since", "invalid").EvaluatePreconditions("\"v\"", modified), Is.Null);
            Assert.That(Request(method, "If-Unmodified-Since", "invalid").EvaluatePreconditions("\"v\"", modified), Is.Null);
            Assert.That(Request(method, "If-Modified-Since", same).EvaluatePreconditions(null, null), Is.Null);
            Assert.That(Request(method, "If-Modified-Since", same).EvaluatePreconditions(null, modified, false), Is.Null);
        }
        [TestCase("POST")]
        [TestCase("PUT")]
        [TestCase("PATCH")]
        [TestCase("DELETE")]
        [TestCase("query")]
        public void NonRetrievalMethodsIgnoreModifiedSince(string method)
            => Assert.That(Request(method, "If-Modified-Since", "Fri, 09 Oct 2026 00:00:00 GMT")
                .EvaluatePreconditions("\"v\"", new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero)), Is.Null);

        [TestCase("CONNECT")]
        [TestCase("OPTIONS")]
        [TestCase("TRACE")]
        public void MethodsWithoutSelectedRepresentationsIgnoreConditions(string method)
            => Assert.That(Request(method, "If-Match", "\"other\"", "If-None-Match", "*")
                .EvaluatePreconditions("\"v\"", null), Is.Null);

        [TestCase("v")]
        [TestCase("w/\"v\"")]
        [TestCase("\"space value\"")]
        [TestCase("\"unterminated")]
        [TestCase("\"v\" \"other\"")]
        [TestCase("*, \"v\"")]
        [TestCase("\"v\", malformed")]
        public void MalformedTagConditionsCannotBecomeMatchedResponses(string value)
        {
            var error = Assert.Throws<HttpException>(() => Assert.That(Request("QUERY", "If-None-Match", value)
                .EvaluatePreconditions("\"v\"", null), Is.Null));
            Assert.That(error.StatusCode, Is.EqualTo(400));
        }
        [TestCase("*")]
        [TestCase("v")]
        [TestCase("\"unterminated")]
        [TestCase("\"space value\"")]
        public void InvalidApplicationTagsAreRejected(string current)
            => Assert.Throws<ArgumentException>(() => Assert.That(Request("QUERY").EvaluatePreconditions(current, null), Is.Null));
    }
}
