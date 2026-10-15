# Rate limiting

`RateLimitingModule` admits the requests under its base route through a limiter from
Microsoft's [System.Threading.RateLimiting](https://learn.microsoft.com/dotnet/api/system.threading.ratelimiting),
the library ASP.NET Core's rate limiting is built on. It offers fixed window, sliding window,
token bucket and concurrency limiters, each partitioned by a key the application chooses,
with optional queueing. A request that gets a permit continues to the next module; one that
does not is answered with 429 Too Many Requests.

This is new in Neo v2; the published 1.0.x packages do not include it. It adds the
`System.Threading.RateLimiting` package as a dependency of EmbedIO-Neo.

## A complete program

```csharp
using System;
using System.Threading;
using System.Threading.RateLimiting;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.RateLimiting;

// Each client: a burst of 10 requests, then one more every 6 seconds.
using var perClient = PartitionedRateLimiter.Create<IHttpContext, string>(context =>
    RateLimitPartition.GetTokenBucketLimiter(
        RateLimitingModule.ClientKey(context),
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 10,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(6),
            QueueLimit = 0,
        }));

// All clients together: at most 500 requests a second.
using var overall = PartitionedRateLimiter.Create<IHttpContext, string>(_ =>
    RateLimitPartition.GetFixedWindowLimiter(
        "all",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 500, Window = TimeSpan.FromSeconds(1), QueueLimit = 0 }));

// Uploads: four at a time, and up to eight more wait their turn.
using var uploads = PartitionedRateLimiter.Create<IHttpContext, string>(_ =>
    RateLimitPartition.GetConcurrencyLimiter(
        "uploads",
        _ => new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 8 }));

using var combined = PartitionedRateLimiter.CreateChained(overall, perClient);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

// Declared after the limiters, so it is disposed before them.
using var server = new WebServer("http://localhost:9696/")
    .WithModule(new RateLimitingModule("/", combined))
    .WithModule(new RateLimitingModule("/uploads", uploads))
    .WithModule(new ActionModule(context => context.SendStringAsync("ok", "text/plain", WebServer.DefaultEncoding)));

Console.WriteLine(new Uri("http://localhost:9696/"));
await server.RunAsync(cancellation.Token);
```

The eleventh quick request from one client is answered 429 with `Retry-After: 6`, while other
clients are unaffected. `CreateChained` requires a permit from every limiter it chains. Modules
stack: a request to `/uploads` passes both modules, so it needs a per-client token as well as an
upload slot.

## Choosing a key

The key decides who shares a budget.

- `RateLimitingModule.ClientKey(context)` keys by the connection's remote address. IPv4
  clients, including IPv4-mapped IPv6 addresses, get one partition per address. IPv6 clients
  get one per /64 prefix, since a single client typically controls a whole /64 and could
  otherwise make a new partition per request.
- Forwarded headers such as `X-Forwarded-For` are not read: a client can set them to anything.
  Behind a reverse proxy every client shares the proxy's address, so derive the client address
  from metadata you trust and pass it to `RateLimitingModule.ClientKey(IPAddress)`.
- An application identity, such as a user ID or API key your authentication validated, makes
  a budget per account. The rate-limiting module must come after the module that establishes
  the identity.

## Rejections

A rejected request gets status 429. When the limiter can say how long to wait (window and
token bucket limiters), the response also carries `Retry-After` in whole seconds, rounded up.
A concurrency limiter cannot say, so its rejections have no `Retry-After`.

- `RejectionStatusCode` changes the status, for example to 503.
- `OnRejected` writes the response body, for example as JSON. Its status and `Retry-After`
  are already set when it runs, and the request goes no further.

Both are set before the server starts.

## Permits, queues and cancellation

A permit is held until the request completes, including its response, so a concurrency
limiter counts requests in progress. With a `QueueLimit`, a request that finds no permit waits
for one, in the queue order the limiter is configured with; once the queue is full, further
requests are rejected at once.

A queued request's wait is canceled when the server stops. HTTP/2 and HTTP/3 stream
resets also cancel the request's wait. HTTP/1.1 does not promptly detect a disconnected
client while waiting for a permit, so an abandoned request can retain its queue slot
until it receives a permit. Keep queues short where clients are likely to give up.

## Memory

A partitioned limiter keeps state only for keys with recent requests: System.Threading.RateLimiting
removes a partition about ten seconds after it becomes idle. Memory therefore grows with the
number of distinct keys seen within a short period, not with every client ever seen. A burst of
requests from many addresses can still create many partitions; chaining a shared limiter, as the
program does, caps the total rate those partitions can admit, and /64 keys stop one IPv6 client
from multiplying its partitions.

## What it is not

- Rate budgets are per key and per route. They are separate from the server's
  [request limits](request-limits.md), which bound header and body sizes and admission
  deadlines for every request.
- `IPBanningModule` blocks clients by address after misbehavior. It is unchanged and can be
  used alongside rate limiting.
- The module limits what reaches the modules after it. Requests rejected earlier in the
  pipeline, and connections refused by the transport, are not counted.

## Lifetime

The module does not own its limiter. Dispose the limiter after the server has stopped, as the
program does by declaring it first. Disposing it while the server runs makes later requests
under the module's route fail with 500.
