using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue598_AsyncOutboundRequest
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task SuspendedOutboundRequestCompletesBeforeJsonResponse(bool manualResponse, bool bufferResponse)
        {
            await ExerciseRequest(manualResponse, bufferResponse, false);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task OutboundHttpFailureCanBeMappedToExplicitJsonError(bool bufferResponse)
        {
            await ExerciseRequest(false, bufferResponse, true);
        }

        private static async Task ExerciseRequest(bool manualResponse, bool bufferResponse, bool failUpstream)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var outbound = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var serializer = ResponseSerializer.Json(bufferResponse);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/backend", HttpVerbs.Get, async context =>
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    context.Response.StatusCode = failUpstream ? 503 : 200;
                    await context.SendStringAsync("terminal-42", "text/plain", WebServer.Utf8NoBomEncoding);
                }))
                .WithWebApi("/v1", serializer, module => module.WithController(() =>
                    new TerminalsController(outbound, new Uri(url + "backend"), serializer)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
                using var incoming = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var pending = incoming.GetAsync(url + "v1/" + (manualResponse ? "manual" : "getterminals"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                // The backend has received the request but cannot complete it yet.
                // This exercises the asynchronous path, not a cached/completed Task.
                Assert.That(pending.IsCompleted, Is.False);
                release.SetResult();
                using var response = await pending;
                Assert.That(response.StatusCode, Is.EqualTo(failUpstream ? HttpStatusCode.BadGateway : HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = document.RootElement;
                Assert.That(root.GetProperty("Result").GetBoolean(), Is.EqualTo(!failUpstream));
                Assert.That(root.GetProperty("Terminal").GetString(), Is.EqualTo(failUpstream ? string.Empty : "terminal-42"));
                Assert.That(root.GetProperty("ErrorMessage").GetString(), Is.EqualTo(failUpstream ? "Upstream request failed." : string.Empty));
                Assert.That(root.TryGetProperty("Status", out _), Is.False, "The Task itself must not be serialized.");
            }
            finally
            {
                release.TrySetResult();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public sealed class TerminalsController : WebApiController
        {
            private readonly HttpClient _client;
            private readonly Uri _endpoint;
            private readonly ResponseSerializerCallback _serializer;

            public TerminalsController(HttpClient client, Uri endpoint, ResponseSerializerCallback serializer)
            {
                _client = client;
                _endpoint = endpoint;
                _serializer = serializer;
            }

            [Route(HttpVerbs.Get, "/getterminals")]
            public async Task<TerminalsResponse> GetTerminals()
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
                try
                {
                    using var response = await _client.SendAsync(request, CancellationToken);
                    response.EnsureSuccessStatusCode();
                    return new TerminalsResponse { Result = true, Terminal = await response.Content.ReadAsStringAsync() };
                }
                catch (HttpRequestException)
                {
                    HttpContext.Response.StatusCode = 502;
                    return new TerminalsResponse { ErrorMessage = "Upstream request failed." };
                }
            }

            [Route(HttpVerbs.Get, "/manual")]
            public async Task WriteTerminals()
            {
                var result = await GetTerminals();
                await HttpContext.SendDataAsync(_serializer, result);
            }
        }

        public sealed class TerminalsResponse
        {
            public bool Result { get; set; }
            public string Terminal { get; set; } = string.Empty;
            public string ErrorMessage { get; set; } = string.Empty;
        }
    }
}
