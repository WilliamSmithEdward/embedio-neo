# Performance checks

Run the allocation checks from the repository root:

```sh
dotnet restore test/EmbedIO.Performance/EmbedIO.Performance.csproj --locked-mode
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore -- --verify-allocations
```

This dependency-free, unpackaged project is intentionally outside `EmbedIO.sln`.
Desktop CI runs it alongside the regression suite. Allocation ceilings catch
regressions; timings are informational and should be compared on the same machine.

See [the additional audit](../../docs/user-reports/performance-audit-second-pass.md) for negotiation, header, byte-order and history workloads. See [the audit report](../../docs/user-reports/performance-audit.md) for the baseline,
measurement method, coverage and limits. Omit `--verify-allocations` for baseline
measurements, using the same runner source against each revision.

## Cold-start checks

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --verify-cold-start
dotnet test/EmbedIO.Performance/bin/Release/net10.0/EmbedIO.Performance.dll --cold-start base-100
```

The verifier launches three fresh child processes per workload. Individual samples
measure the workload on its caller thread without cache/JIT warmup; they do not
include process launch or first HTTP response time. The delayed-start workload
uses a socket-free server with an intentional 50 ms preparation delay.
See [the cold-start audit](../../docs/user-reports/cold-start-audit.md) for baseline
measurements, comparison controls and startup limits.

## Listener queue checks

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-queue --verify-allocations
```

This socket-free workload measures insertion/signalling and the real accept queue at
burst sizes 1, 16 and 256. Context creation is outside measurement. The 300 B/request
ceiling guards against snapshots growing with the queued request count; timing is
informational. See [the HTTP listener audit](../../docs/user-reports/http-listener-audit.md).

## HTTP/HTTPS transport checks

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-http
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --verify-listener-http
```

The loopback client and server share a process. JSON includes throughput, latency
percentiles, process allocations, GC counts and memory. Add --retain-connections
for retained-reference cleanup diagnostics; --requests, --rounds, --concurrency
and --payload-bytes select workloads. Timing is informational. Heavy repeated
churn can exhaust Windows socket/TIME_WAIT capacity; failures abort without retries.
See [the lifetime report](../../docs/user-reports/listener-connection-lifetimes.md)
for methodology, baseline measurements and compatibility coverage.

## Listener body and header allocations

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release -- --listener-allocations
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --verify-listener-allocations
```

Measures the actual drain and header serializer with fixture setup excluded.
The consumed-body fixture stays at EOF; the unread-body fixture resets its source
position and remaining length between operations (that reset is included). Headers
must match exact expected UTF-8 bytes. Allocation budgets, rather than timing, gate CI.

Add `--request-body-bytes 65536` to `--listener-http` for POST, and select
`--body-consumption full`, `partial` or `none`. Full is the default; partial consumes
half the body. The handler validates every consumed byte. Existing GET behavior is
unchanged. `--connection-policy keep-alive`, `close` or `both` controls connection
churn; the default is both for GET/full POST and keep-alive for partial/unread POST.

Partial/unread POST requires keep-alive and fewer than 100 requests per worker,
including eight warmups. The existing connection reuse limit can force an early
close while an unread body is still being sent. A larger baseline workload hit a
TLS connection reset; it was not a valid performance sample. The library's close
and drain policy is preserved. Benchmarks do not retry failures or hide them.

```sh
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-http --request-body-bytes 65536 --body-consumption full --connection-policy keep-alive --concurrency 16 --requests 40 --rounds 5
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-build -- --listener-http --request-body-bytes 65536 --body-consumption none --concurrency 16 --requests 8 --rounds 5
```

The JSON adds requestBodyBytes and bodyConsumption. Allocation and memory figures
include the loopback client and server; timings are informational. Verification
with `--verify-listener-http --request-body-bytes 65536` uses bounded workloads and
checks terminal resource cleanup. Full consumption verifies HTTP/HTTPS with both
connection policies (four workloads); partial/unread verifies keep-alive (two).
