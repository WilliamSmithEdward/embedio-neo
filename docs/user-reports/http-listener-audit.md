# HTTP listener audit

This audit builds on main commit `8268d3e`. It preserves public APIs, target
frameworks, listener defaults, route precedence, certificate selection and
HTTP/WebSocket contracts. No package version or runtime dependency changes are
included.

## Confirmed defects and fixes

A controlled stop/replacement interleaving reproduced stale endpoint removal:
canonical aliases retained an old endpoint after its first alias was removed.
Removing the remaining alias could erase a replacement endpoint's map entry,
and a subsequent registration failed with socket error 10048. Removal now checks
endpoint identity under the existing registration lock. HTTP and HTTPS cases
cover replacement ownership; these tests do not perform a TLS handshake.

RequestStream could read beyond a declared Content-Length after its buffered
bytes were exhausted. A three-byte body followed by another request returned
42 bytes in the baseline test. Reads now cap the transport count to the remaining
body length. Zero-count reads still validate their arguments and return without
calling the transport. Buffered content, short reads, zero-length bodies and
unknown lengths retain their existing supported semantics. This isolates body
bytes; it does not add general HTTP pipelining support.

HttpConnection.Dispose also disposed its last listener. A controlled ownership
case confirmed that this stopped the owner. A connection now disposes its own
resources without disposing the listener. The controlled disposal/read race
already passed on .NET 10, so timer exception behavior was left unchanged.

## Simplifications and measured queue improvement

The listener accept path enumerates concurrent queue entries rather than taking
a snapshot of every key for each accept. Removal still uses TryRemove; cancellation,
shutdown and stale disconnected entries retain their synchronization. Shutdown
uses one connection snapshot instead of separately copying keys into an array
and a list.

Wildcard add/remove operations share their copy-and-publish helpers. Named
prefix snapshots use dictionary copy constructors. Pending endpoint connections
use a HashSet rather than storing every connection as both key and value.
Wildcard/named ownership, duplicate-path errors, longest-path selection and the
inherited wildcard trailing-slash behavior are covered explicitly.

The queue benchmark exercises the real GetContextAsync method without opening
sockets. Contexts are pre-created, then each batch measures queue insertion,
semaphore signalling and accept draining. Seven timed rounds follow warmup;
the table gives the median round on the same Windows x64 machine with .NET
SDK 10.0.400. Allocation includes insertion and accept task/cancellation bookkeeping.

| Queued requests per batch | Before ns/request | After ns/request | Before B/request | After B/request |
| --- | ---: | ---: | ---: | ---: |
| 1 | 715.0 | 547.6 | 272 | 248 |
| 16 | 545.8 | 322.5 | 332 | 248 |
| 256 | 3795.7 | 972.3 | 1292 | 248 |

These are queue measurements, not end-to-end HTTP throughput or latency claims.
The allocation verifier budgets 300 bytes per request for these workloads.

```sh
dotnet restore test/EmbedIO.Performance/EmbedIO.Performance.csproj --locked-mode
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore -- --listener-queue --verify-allocations
```

## Compatibility review and remaining opportunities

The audit examined the managed listener, endpoint manager/accept loop, parser,
request/response streams and Microsoft listener adapter. Current main already
removes request/response buffer copies, serializes endpoint registration and
removes prefixes by identity and owner; those improvements were retained.

Two line-parser experiments were evaluated: a complete-line fast path and a
simpler LF-return loop. Differential snapshots matched the original parser in
7,098 cases (1,014 inputs at seven fragment sizes), including malformed headers,
non-ASCII bytes and unusual CR/LF sequences. The fast path improved ordinary
headers but reproducibly slowed a one-byte-fragmented large header by about 23%.
The simpler loop had unchanged allocations and inconclusive timing benefits, so
it was deferred. The production parser remains unchanged; 44 compatibility cases preserve these semantics for
future investigations. Experimental sources and comparison logs remain under
ignored TestResults/listener-audit/parser.

The following opportunities need separate evidence before implementation:

- Header deadlines are disabled after the initial read. Adding a deadline could
  reject clients previously accepted, so this audit does not change timeouts.
- The asynchronous socket accept path can recurse on synchronous completions.
  An iterative replacement needs a controlled load comparison and evidence that
  dispatch ordering and error recovery remain compatible. The macOS IPv6
  blocking-accept workaround remains intact.
- Terminal connection cleanup could release timer and TLS stream resources more
  promptly. Close has response-close recursion and keep-alive ownership to
  account for; source inspection alone does not establish a permanent leak.
- Microsoft GetContextAsync cancellation depends on listener shutdown. Changing
  it requires native accept ownership and cancellation parity validation.
- An IPv6 socket-option failure occurs outside the constructor's bind/listen
  cleanup block. That failure has not been reproduced in the tested environments.

The response-stream asynchronous-write work merged separately in PR #84 as
`d877539`. This branch includes that main state without duplicating its changes.

## Validation

Five controlled request-body/ownership cases failed on the prior code; the stale
endpoint replacement case independently failed before its fix. The first combined
focused run passed all 28 queue, endpoint and resource cases. The Windows suite
then passed with 903 successes and two existing platform skips (905 total),
including 44 additional parser characterization cases. After rebasing onto
main with PR #84, the combined Windows suite passed with 945 successes and
two existing skips (947 total). Both supported library targets built with
analyzers; existing hot-path/cold-start and new queue allocation budgets passed.
Final cross-platform CI, security and malware gates remain required before merge.

Local commands, before/after benchmark output and TRX reports live under ignored
TestResults/listener-audit (the endpoint baseline is under TestResults/endpoint-baseline).
No release is included.
