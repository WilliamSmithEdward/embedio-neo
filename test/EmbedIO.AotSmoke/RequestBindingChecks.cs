using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke
{
    internal static class RequestBindingChecks
    {
        internal static WebApiModule CreateModule() =>
            new WebApiModule("/request-binding", ResponseSerializer.None(false)).WithController(
                RequestSmokeBindings.RequestSmokeController
            );

        internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks)
        {
            if (
                await client.GetStringAsync(url + "request-binding/query?page=2&name=book&ids=book-3&ids=")
                != "2|book|3,"
            ) throw new InvalidOperationException("AOT request DTO parsing failed.");

            if (await client.GetStringAsync(url + "request-binding/route/book-9") != "9")
                throw new InvalidOperationException("AOT custom route parser failed.");

            using var request = new HttpRequestMessage(HttpMethod.Get, url + "request-binding/fields");
            request.Headers.Add("X-Count", "4");
            request.Headers.TryAddWithoutValidation("Cookie", "theme=dark");
            using var fields = await client.SendAsync(request);

            if (await fields.Content.ReadAsStringAsync() != "4|dark")
                throw new InvalidOperationException("AOT header/cookie binding failed.");

            using var form = new StringContent(
                "page=3&name=form&ids=book-6",
                Encoding.UTF8,
                "application/x-www-form-urlencoded"
            );
            using var posted = await client.PostAsync(url + "request-binding/form", form);

            if (await posted.Content.ReadAsStringAsync() != "3|form|6")
                throw new InvalidOperationException("AOT bounded form binding failed.");

            using var rejected = await client.GetAsync(url + "request-binding/query?page=2&page=3");

            if (rejected.StatusCode != HttpStatusCode.BadRequest)
                throw new InvalidOperationException("AOT duplicate scalar handling failed.");

            using var invalid = await client.GetAsync(url + "request-binding/query?page=0");

            if (invalid.StatusCode != HttpStatusCode.BadRequest)
                throw new InvalidOperationException("AOT DTO validation failed.");

            var document = new OpenApiDocumentBuilder("Bound requests", "1")
                .AddController("/request-binding", RequestSmokeBindings.RequestSmokeController)
                .Build(RequestSmokeJson.Default.Options);

            if (document.Root["paths"]?["/request-binding/form"]?["post"]?["responses"]?["415"] == null)
                throw new InvalidOperationException("AOT binding schema missing.");

            checks["request_binding"] =
                "query/form DTOs, aliases, validation, custom/nullable ID arrays, header/cookie/route fields and OpenAPI without DTO JSON metadata";
        }
    }

    [GeneratedControllerBinding(typeof(RequestSmokeController))]
    internal static partial class RequestSmokeBindings { }

    internal sealed class RequestSmokeController : WebApiController
    {
        [Route(HttpVerbs.Get, "/query")]
        public string Query([QueryObject, Validate] RequestSmokeInput input)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return Echo(input);
        }

        [Route(HttpVerbs.Post, "/form")]
        public string Form([FormObject, Validate] RequestSmokeInput input)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return Echo(input);
        }

        [Route(HttpVerbs.Get, "/route/{id}")]
        public string ReadId([RouteField] RequestSmokeId id)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return id.Value.ToString(CultureInfo.InvariantCulture);
        }

        [Route(HttpVerbs.Get, "/fields")]
        public string Fields([HeaderField("X-Count")] int count, [CookieField("theme")] string theme)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return count.ToString(CultureInfo.InvariantCulture) + "|" + theme;
        }

        private static string Echo(RequestSmokeInput input) =>
            input.Page.ToString(CultureInfo.InvariantCulture)
                + "|"
                + input.Name
                + "|"
                + string.Join(",", input.Ids.Select(id => id?.Value.ToString(CultureInfo.InvariantCulture)));
    }

    internal sealed class RequestSmokeInput
    {
        [System.ComponentModel.DataAnnotations.Range(1, 10)]
        public int Page { get; set; } = 1;
        [RequestField("name")]
        public string Name { get; set; } = "default";
        public RequestSmokeId?[] Ids { get; set; } = Array.Empty<RequestSmokeId?>();
    }

    internal readonly record struct RequestSmokeId(int Value)
    {
        public static bool TryParse(string value, IFormatProvider provider, out RequestSmokeId result)
        {
            result = default;

            if (
                !value.StartsWith("book-", StringComparison.Ordinal)
                || !int.TryParse(value.AsSpan(5), NumberStyles.None, provider, out var parsed)
            ) return false;

            result = new RequestSmokeId(parsed);
            return true;
        }
    }

    [JsonSerializable(typeof(string))]
    internal partial class RequestSmokeJson : JsonSerializerContext { }
}
