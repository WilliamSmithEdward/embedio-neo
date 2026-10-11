# JSON and plaintext benchmark endpoints

[Upstream #495](https://github.com/unosquare/embedio/issues/495), by
Kaliumhexacyanoferrat, introduced EmbedIO's TechEmpower JSON/plaintext test suite,
reported difficulty with keep-alive and proposed further tests and optimization.
rdeago thanked the contributor and planned to revisit the benchmark after v3.5.

The inspected [external fixture](https://github.com/TechEmpower/FrameworkBenchmarks/tree/57d92fbec6f8fd7431bc77326dd0484e60c96e20/frameworks/CSharp/embedio)
targets upstream EmbedIO 3.5.2 and .NET 9, using SWAN. It is not a Neo benchmark.
FrameworkBenchmarks is archived. This work does not submit an external entry,
update a leaderboard or claim a newer benchmark round.

## Run the isolated Neo host

From a checkout of this repository:

```sh
dotnet restore test/EmbedIO.Performance/EmbedIO.Performance.csproj --locked-mode
dotnet build test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --benchmark-endpoints
```

The default listener is EmbedIO mode on `http://127.0.0.1:8080/`. The two exact
GET routes are `/json` and `/plaintext`; query strings do not change the route.
Use a separate terminal to inspect the responses:

```sh
curl --http1.1 -i http://127.0.0.1:8080/json
curl --http1.1 -i http://127.0.0.1:8080/plaintext
curl --http1.1 -i -H "Accept-Encoding: gzip" http://127.0.0.1:8080/json
```

Expect HTTP 200, `Content-Length`, `Server` and `Date` headers, with bodies
`{"message":"Hello, World!"}` and `Hello, World!` respectively. JSON is
serialized from a freshly instantiated object on every request, rather than
cached as a constant response. Neither endpoint compresses the payload, even
when the client advertises gzip. The fixed plaintext bytes are reusable.

Ctrl+C cancels the host and disposes the listener. Existing performance commands
retain their behavior; this host is a separate opt-in command. Its shared
endpoint fixture and regression tests are outside every production package.
There are no new runtime packages.

Neo v2 removes the Microsoft listener. The host rejects the retired `--microsoft`
selector; omit it to use the retained engine. Historical native results below
describe their recorded revisions, not current v2 support. To select another prefix, add
`--url http://127.0.0.1:8081/`. Binding beyond loopback is an explicit deployment
choice; plan network access controls rather than copying a wildcard by default.

## Confirmed managed pipelining defect

Keep-alive and pipelining are different: sequential keep-alive waits for each
response before sending another request; pipelining sends multiple requests
before reading all their responses. The
[official test overview](https://github.com/TechEmpower/FrameworkBenchmarks/wiki/Project-Information-Framework-Tests-Overview)
uses pipelining for plaintext and per-request serialization for JSON.

Both listeners pass sequential keep-alive. Before this fix, the two initial
16-request pipeline cases failed in managed mode with a reset after its existing
15-second subsequent-request timeout. Windows native mode passed the same cases.
Native Unix has a separate runtime limitation described below. The managed listener had already read bytes for later requests, then discarded them
when resetting after the first response.

The listener now preserves bytes beyond the completed request body and parses
that buffered successor before reading more socket data. A Content-Length body
must finish or be drained before its remaining buffer becomes the next request.
This also handles a next header partly in memory and partly in a later read.
The response framing, keep-alive reuse limit and timeouts are unchanged; no
unlimited connection lifetime is introduced. This demonstrates a current defect,
not the exact cause of the original reporter's 2020 difficulty.

The 26 real TCP/HTTP(S) cases cover both listeners: sequential reuse, 16-request
plaintext/mixed pipelines, ordered complete bodies and headers, no gzip,
explicit close and fresh connections, 32 concurrent clients, unknown child
routes, full/partial/unread Content-Length bodies, split successor headers, batches
spanning the receive buffer and managed HTTPS.
Run them with:

```sh
dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Issue495_" --report-trx --results-directory TestResults/benchmark-endpoints --timeout 2m --minimum-expected-tests 26
```

## Native Unix runtime limitation

Linux and macOS CI on .NET 10.0.12 found six native `System.Net.HttpListener`
pipeline/body-boundary failures. A standalone reproduction without EmbedIO on
Ubuntu 24.04.5 also answered the first of two pipelined requests and reset the
connection. The [runtime parser](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.HttpListener/src/System/Net/Managed/HttpConnection.cs)
resets request state without preserving buffered successor bytes. Ordinary
sequential keep-alive still passes; it is not equivalent to pipelining.

The managed listener's corrected pipeline cases pass on both platforms. Select
`HttpListenerMode.EmbedIO` explicitly for Unix workloads requiring pipelining;
this is the benchmark host's default mode. [Tracking issue #158](https://github.com/WilliamSmithEdward/embedio-neo/issues/158)
records the investigation and the owner-approved decision to document this runtime
limitation and recommend the existing managed mode.
No parser shim or automatic mode/default switch is implemented. Do not interpret
Windows native results as Unix native validation.

The six affected native Unix test cases are explicitly reported as platform
skips in ordinary CI. All managed cases remain required on every desktop
platform, and all native Windows cases remain required. The failed .NET 10.0.12
runs and standalone reproduction establish the limitation; skips do not claim a
native runtime fix. Sequential native Unix keep-alive, framing, concurrent clients
and close/reconnect checks still execute normally.

The original failing tests are retained as opt-in runtime probes. On Linux or
macOS, run them against a runtime being evaluated with:

```sh
EMBEDIO_TEST_NATIVE_UNIX_PIPELINING=1 dotnet test --project test/EmbedIO.Tests/EmbedIO.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Issue495_" --report-trx --results-directory TestResults/native-unix-pipeline-probe --timeout 2m --minimum-expected-tests 26
```

Expect the six native cases to fail on the verified .NET 10.0.12 runtime. A future
passing probe warrants a reviewed update to the documented support and test
guard; detecting a new version alone does not establish correctness. This test
variable does not change library behavior or listener selection.

## Measure a stated workload

The host lets an application-owned load generator exercise actual routing and
serialization. Record the exact source, runtime/OS, listener mode, warmup,
concurrency, requests, payload and connection policy. Run the generator separately
from the host when measuring server resources. Compare the same environment and
workload before and after; report errors, throughput and latency distributions,
not just the highest observed request rate. Correct pipelining alone is not a
claim of competitive throughput or reduced CPU/allocation cost.

For existing bounded in-process HTTP/HTTPS measurements, see
[listener lifetimes and end-to-end workloads](listener-connection-lifetimes.md).
Those allocation measurements include client and server, and use their documented
payloads rather than these JSON/plaintext routes.

## Additional test types

The original title proposed more test types without supplying a database or
implementation. Database queries/updates, fortunes and database-backed caching
exercise database drivers, pooling, schema and sometimes rendering. A synthetic
in-memory response is not a valid substitute. These types are not added to the
core or represented as benchmark results here. A future database adapter and
benchmark require their own agreed dependency/ownership design, correctness
checks and reproducible database environment. The useful supported scope of
this report is a maintained JSON/plaintext host and corrected pipelining.
