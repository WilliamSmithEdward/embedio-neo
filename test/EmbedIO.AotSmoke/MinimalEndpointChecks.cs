using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Endpoints;
using EmbedIO.OpenApi;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke
{
    internal static class MinimalEndpointChecks
    {
        internal static EndpointModule CreateModule()
        {
            var module = new EndpointModule(ResponseSerializer.None(false));
            var group = module.Root
                .MapGroup("/minimal")
                .MapGroup("/v1");
            group
                .MapGet("/query", MinimalSmokeHandlers.QueryEndpoint)
                .WithName("minimal-query");
            group.MapGet("/route/{id}", MinimalSmokeHandlers.RouteEndpoint);
            group.MapGet("/context", MinimalSmokeHandlers.ContextEndpoint);
            group
                .MapGet("/protected", MinimalSmokeHandlers.ContextEndpoint)
                .RequireAuthorization();
            var document = new OpenApiDocumentBuilder("Minimal", "1")
                .AddEndpoints(module)
                .Build(RequestSmokeJson.Default.Options);

            if (
                document.Root["paths"]?["/minimal/v1/query"]?["get"]?["parameters"] == null
                || module.Links.GetPath("minimal-query") != "/minimal/v1/query"
            ) throw new InvalidOperationException("AOT minimal metadata failed.");

            return module;
        }

        internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks)
        {
            if (await client.GetStringAsync(url + "minimal/v1/query?page=2&name=sample") != "2|sample")
                throw new InvalidOperationException("AOT minimal DTO failed.");

            if (await client.GetStringAsync(url + "minimal/v1/route/book-8") != "8")
                throw new InvalidOperationException("AOT minimal custom ID failed.");

            if (await client.GetStringAsync(url + "minimal/v1/context") != "GET")
                throw new InvalidOperationException("AOT minimal framework/ValueTask binding failed.");

            using var denied = await client.GetAsync(url + "minimal/v1/protected");

            if (denied.StatusCode != HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("AOT minimal authorization failed.");

            using var invalid = await client.GetAsync(url + "minimal/v1/query?page=0");

            if (invalid.StatusCode != HttpStatusCode.BadRequest)
                throw new InvalidOperationException("AOT minimal validation failed.");

            checks["minimal_endpoints"] =
                "nested exact routes, generated DTO/custom IDs, framework/ValueTask binding, authorization, validation, named links and OpenAPI";
        }
    }

    [GeneratedEndpointBinding]
    internal static partial class MinimalSmokeHandlers
    {
        public static string Query([QueryObject, Validate] RequestSmokeInput input) =>
            input.Page.ToString(CultureInfo.InvariantCulture) + "|" + input.Name;

        public static string Route([RouteField] RequestSmokeId id) => id.Value.ToString(CultureInfo.InvariantCulture);

        public static ValueTask<string> Context(IHttpContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(context.Request.HttpMethod);
        }
    }
}
