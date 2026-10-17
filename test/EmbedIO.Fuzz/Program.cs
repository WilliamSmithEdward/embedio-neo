using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Utilities;

if (FsCheckProperties.Run(args)) return;
if (await BrowserProtectionFuzz.SelfTestAsync(args)) return;
if (await EventHubFuzz.SelfTestAsync(args)) return;
if (await HealthReadinessFuzz.SelfTestAsync(args)) return;
if (await RequestAbortFuzz.SelfTestAsync(args)) return;
if (await HeaderPolicyFuzz.SelfTestAsync(args)) return;
if (await ExtendedFuzzSelfTest.RunAsync(args)) return;
if (await StateMachineSelfTest.RunAsync(args)) return;
if (await ResponsePipelineFuzz.SelfTestAsync(args)) return;

if (await StateMachineCampaign.RunAsync(args, new Dictionary<string, Func<StateMachineTrace, CancellationToken, Task>>
{
    ["websocket-budget"] = SharedBudgetFuzz.RunAsync,
    ["rate-limits"] = SharedRateLimitFuzz.RunAsync,
    ["routes"] = SharedRoutesFuzz.RunAsync,
    ["tunnels"] = SharedTunnelFuzz.RunAsync,
    ["sessions"] = SharedSessionFuzz.RunAsync,
    ["auth-state"] = AuthStateFuzz.RunAsync,
    ["auth-jwt"] = JwtAuthFuzz.RunAsync,
    ["auth-http1"] = AuthStateFuzz.Http1Async,
    ["auth-http2"] = AuthStateFuzz.Http2Async,
    ["forwarded-policy"] = HeaderPolicyFuzz.ForwardedAsync,
    ["cors-policy"] = HeaderPolicyFuzz.CorsAsync,
    ["conditional-ranges"] = HeaderPolicyFuzz.ConditionalAsync,
    ["request-codings"] = RequestCodingFuzz.RunAsync,
    ["multipart-lifetime"] = MultipartLifetimeFuzz.RunAsync,
    ["server-lifecycle"] = LifecycleFuzz.RunAsync,
    ["file-mutation"] = FileMutationFuzz.RunAsync,
    ["json-streaming"] = JsonStreamingFuzz.RunAsync,
    ["event-hub"] = EventHubFuzz.RunAsync,
    ["health-readiness"] = HealthReadinessFuzz.RunAsync,
    ["request-abort"] = RequestAbortFuzz.RunAsync,
    ["browser-protection"] = BrowserProtectionFuzz.RunAsync,
    ["request-validation"] = RequestValidationFuzz.RunAsync,
    ["controller-parity"] = ControllerParityFuzz.RunAsync,
    ["permanent-bans"] = PermanentBanFuzz.RunAsync,
    ["response-http1"] = ResponsePipelineFuzz.Http1Async,
    ["response-http2"] = ResponsePipelineFuzz.Http2Async,
    ["response-http3"] = ResponsePipelineFuzz.Http3Async,
    ["http1-connections"] = ConnectionIsolationFuzz.Http1Async,
    ["http2-connections"] = ConnectionIsolationFuzz.Http2Async,
    ["http3-connections"] = ConnectionIsolationFuzz.Http3Async,
})) return;

if (HpackInterop.Run(args) || Http1HeadFuzz.Run(args) || await Http2TransportFuzz.RunAsync(args) || await Http3TransportFuzz.RunAsync(args) || await WebSocketFuzz.RunAsync(args)) return;

// Custom mutation/property mode. Its scope is URL paths and query
// data; it does not claim to fuzz the listener or WebSocket frame parser.
var seed = args.Length > 0 ? int.Parse(args[0]) : 1729;
var iterations = args.Length > 1 ? int.Parse(args[1]) : 100_000;
if (iterations < 1 || iterations > 1_000_000)
    throw new ArgumentOutOfRangeException(nameof(iterations));
var random = new Random(seed);
var corpus = new List<string>(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "corpus.txt")));
const string alphabet = "%&=/?[]#+\0\r\n\t abcXYZ0123456789é€";
string input = string.Empty;
try
{
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        input = corpus[random.Next(corpus.Count)];
        for (var mutation = random.Next(1, 12); mutation > 0; mutation--)
        {
            var position = random.Next(input.Length + 1);
            if (position < input.Length && random.Next(3) == 0)
                input = input.Remove(position, 1);
            else
                input = input.Insert(position, alphabet[random.Next(alphabet.Length)].ToString());
        }
        if (input.Length > 1024)
            input = input.Substring(0, 1024);
        foreach (var groupFlags in new[] { false, true })
        {
            var mutable = UrlEncodedDataParser.Parse(input, groupFlags);
            var immutable = UrlEncodedDataParser.Parse(input, groupFlags, false);
            if (mutable.Count != immutable.Count)
                throw new InvalidOperationException("Mutability changed parsed key count.");
            foreach (var key in mutable.AllKeys)
            {
                if (mutable[key] != immutable[key])
                    throw new InvalidOperationException("Mutability changed parsed values.");
            }
        }
        var path = "/" + input;
        foreach (var basePath in new[] { false, true })
        {
            var normalized = UrlPath.Normalize(path, basePath);
            if (!UrlPath.IsValid(normalized) || UrlPath.Normalize(normalized, basePath) != normalized)
                throw new InvalidOperationException("Path normalization is not valid and idempotent.");
        }
        if (iteration % 1000 == 0 && corpus.Count < 1000)
            corpus.Add(input);
    }
    Console.WriteLine($"Passed {iterations} mutations; seed {seed}.");
}
catch (Exception)
{
    Console.Error.WriteLine($"Seed {seed}; failing input: {JsonSerializer.Serialize(input)}");
    throw;
}
