# Bounded SSE event channels

This feature requires EmbedIO-Neo 2.6.0 or later.
The existing `OpenEventStream()` writer remains unchanged.

Use a `ServerSentEventHub` when an application needs to broadcast progress,
notifications or status changes to multiple HTTP clients. Own one hub per channel
and authorization scope. The hub starts no background work until an HTTP
subscription is actively being written; ordinary endpoints are unaffected.

## A complete server and browser client

This .NET 10 example publishes a tick once a second. It listens on loopback and
uses generated controller bindings; no JSON reflection metadata is needed for
string event data. Keep the hub alive for the application's lifetime, rather
than creating one per request.

```csharp
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
using var hub = new ServerSentEventHub();
using var server = new WebServer("http://localhost:9696/")
    .WithModule(new WebApiModule("/api")
        .WithController(EventBindings.EventsController, () => new EventsController(hub)));
var running = server.RunAsync(stop.Token);
try
{
    for (var tick = 1; ; tick++)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
        hub.Publish(tick.ToString(CultureInfo.InvariantCulture), "tick");
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
finally { stop.Cancel(); await running; }

[GeneratedControllerBinding(typeof(EventsController))]
internal static partial class EventBindings { }
public sealed class EventsController : WebApiController
{
    private readonly ServerSentEventHub _hub;
    public EventsController(ServerSentEventHub hub) => _hub = hub;
    [Route(HttpVerbs.Get, "/events")]
    public ServerSentEventStream Events() => _hub.Stream();
}
```

A browser page on that origin can listen with:

```javascript
const events = new EventSource("/api/events");
events.addEventListener("tick", event => {
    console.log(event.lastEventId, event.data);
});
events.onerror = () => {
    if (events.readyState === EventSource.CLOSED) {
        console.log("Stream ended; resynchronize application state before reconnecting.");
    }
};
// Call events.close() when the view is removed.
```

The browser reconnects after ordinary connection loss and supplies its last
received event ID automatically. A newly created `EventSource` has no previous
cursor. A 409 response fails that connection and requires an application resync;
creating another connection with no cursor does not recover missed events.
Native `EventSource` does not expose error response bodies/statuses or let you
set arbitrary request headers. A fetch-based streaming client can inspect 409
and send `Last-Event-ID` explicitly. Standard behavior is described in the
[HTML event-stream specification](https://html.spec.whatwg.org/multipage/server-sent-events.html).

## Replay and delivery contract

`Publish(data, eventName)` returns an opaque ID scoped to that hub instance.
It validates the entire framed UTF-8 event before assigning its ID. Publication
and replay subscription share one ordering boundary: accepted retained events
after the cursor are queued before subsequent live events, without a handoff gap.
Concurrent publishers receive one channel order, seen consistently by subscribers.

`hub.Stream()` creates a result without subscribing. When written, it reads
`Last-Event-ID` after the ordinary controller authorization/hooks have run.
The result supports synchronous and `Task<ServerSentEventStream>` controller
methods, generated/reflection bindings, and owned controller cleanup after the
complete stream lifetime. Existing non-streaming controller disposal behavior
is preserved. Direct modules can `await hub.Stream().WriteAsync(context, token)`.
OpenAPI describes `text/event-stream` with a streaming extension.

A missing/empty cursor receives future events only. A supplied cursor must be
canonical, belong to this hub lifetime and have no missing events after it.
Unknown, future, evicted or expired history returns **409** before response
commitment. Replay that cannot fit this subscriber's queue also returns 409.
The application must obtain a consistent snapshot and arrange its snapshot/event
boundary; the hub does not implement snapshot transactions. A cursor immediately
before the oldest retained event is accepted because every later event is present.
A cursor with no retained history, even the most recent cursor, is rejected
conservatively. Disabling replay rejects every nonempty reconnect cursor.

Delivery is in-memory and best effort. An ID proves publication, not receipt or
application processing. Disconnects can cause redelivery; clients should deduplicate
where necessary. Hub replacement/process restart changes the epoch and loses
history. There is no durable storage, acknowledgment, cluster fanout, exactly-once
processing or replay provider in this first version.

## Limits and slow clients

Default `ServerSentEventHubOptions`:

| Setting | Default |
| --- | --- |
| `MaxEventBytes` | 65,536 complete framed UTF-8 bytes |
| `MaxReplayEvents` | 256; zero disables replay |
| `MaxReplayBytes` | 1,048,576 framed bytes |
| `ReplayWindow` | Five minutes |
| `MaxSubscribers` | 128 owned subscriptions |
| `MaxQueuedEvents` | 64 per subscriber |
| `MaxQueuedBytes` | 262,144 framed bytes per subscriber |
| `HeartbeatInterval` | Fifteen seconds; infinite disables heartbeats |
| `WriteTimeout` | Thirty seconds per write/flush frame |

Count, byte and age limits all apply; the first reached wins. `MaxQueuedBytes`
must fit `MaxEventBytes`. A large event that cannot fit replay clears retained
history rather than leaving a silently missing event inside the replay window.
Expiry is checked on publication/subscription; inactive expired entries can remain
allocated until another operation or hub completion/disposal, within the same
count/byte bounds. Subscriber queues have their own count/byte limits and do not
expire by replay age.

Heartbeat and write deadlines must be at least one millisecond (and at most
`int.MaxValue` milliseconds); only the heartbeat supports infinite.

Byte limits count wire frames. Managed strings, objects and queue references add
heap overhead. Frames are immutable and shared across subscribers. Each active
writer can additionally hold one in-flight frame, bounded by `MaxEventBytes`.
Application input strings, HTTP buffers and transport allocations are outside
these budgets. There is no unbounded channel registry: applications own explicit
hub instances and must also bound the number of hubs they create.

Publication does not await network clients. When a subscriber queue exceeds a
limit, that subscription discards its queue and fails; other subscribers continue.
Its owned slot remains occupied until disposed, so repeatedly overflowing clients
cannot bypass the subscription limit while their writes are pending. HTTP writes
are serialized and awaited with cooperative cancellation and the configured
write/flush deadline. There is no silent drop-oldest mode. A full/completed/disposed
hub returns **503** to new subscriptions.

`Complete()` stops publication/new subscriptions, drains current subscriber queues
and ends their streams. `Dispose()` discards replay/queued data and wakes readers.
Direct `Subscribe(cursor)` consumers must dispose their subscription even after
completion/overflow. `ReadAsync(token)` permits one active read, returns null at
completion, and throws `IOException` after overflow. Canceling a read leaves the
subscription and queued events intact.

## HTTP lifecycle and authorization

Responses are UTF-8, unbuffered, uncompressed and `Cache-Control: no-cache`.
An initial `: connected` comment flushes headers without inventing a data event.
Idle comments keep the transport active and help discover disconnected clients.
A request that forbids identity encoding returns 406. Fixed Content-Length is
rejected before subscribing; HEAD and no-entity statuses do not subscribe.
Errors after response commitment abort HTTP/1 or reset the multiplexed stream,
so partial delivery does not masquerade as successful completion.

The context token is a server/request cancellation token, not a universal remote
client-disconnect token. Transport writes/heartbeats discover disconnects; disabling
heartbeats can leave an idle disconnected subscription until cancellation, another
publication or shutdown. Write deadlines rely on transport cancellation support.
No default listener request timeout or authorization policy is changed: configure
an appropriate existing timeout for the lifetime you intend the stream to have.

Apply the existing bearer/policy authorization to the controller before returning
the stream. Partition private channels by their authorized security scope; a
reconnect cursor is not an authorization credential. Authorization is evaluated
when each connection is established, not repeatedly during that connection.
Applications requiring immediate revocation must cancel the associated stream.

Browser `EventSource` supports cookie credentials (`withCredentials` for eligible
cross-origin requests), not a custom bearer header. Use an authenticated fetch
streaming client for bearer headers; do not put bearer tokens in the URL. Apply
normal CORS/cookie policies and prevent unauthorized subscription/replay.

## Validation

Coverage includes replay/live handoff, malformed/foreign cursors, memory budgets,
slow-reader isolation, cancellation/disposal, heartbeat/write errors, authorization,
controller lifetime, and real HTTP/1/2/3 progressive framing. Native AOT and the
legacy target have smoke cases. The `event-hub` state-machine campaign runs three
publishers/subscribers with independent payload and queue ledgers, a retained
replay trace, and a negative oracle check that must reject injected corruption.
These tests do not establish durable delivery, cluster consistency or soak limits.
