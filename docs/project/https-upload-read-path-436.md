# HTTPS 1 MiB uploads on the v2 engine (issue #436, October 2026)

Issue #436 tracks a regression recorded in the
[second-host load baseline](http-engine-load-baseline-evo-x1.md): at engine
revision `a4f7105` the v1 core served HTTPS 1 MiB uploads 18% faster, with 27%
less CPU per request, and the cause was never attributed to methods because that
host had no stack sampler. This document re-measures the scenario on the 2.0.0
release, attributes the server CPU with a stack sampler, counts transport reads
per request on both engines, and records the outcome. The harness and its method
are described in [test/EmbedIO.LoadBenchmark](../../test/EmbedIO.LoadBenchmark/README.md).

## Summary

- **The regression is not present at 2.0.0 on this host.** The v2 engine (main
  `8083fed5`) serves HTTPS 1 MiB uploads at 1.12x the v1.0.3 core's requests per
  second with 4.5% less CPU per request, 76% fewer allocated bytes and a p99
  latency 43% lower. Every candidate round (8,924-8,950 req/s) is above every
  baseline round (7,699-8,386 req/s). The plain-text upload and the small HTTPS
  request controls move the same way.
- **Read counts are identical.** Both engines issue 65.5 body reads per 1 MiB
  over TLS (64.5 over plain TCP), all of 16 KiB except two short reads per
  request, after one 8 KiB head read. The hypothesis in the issue, that v2 issues
  more and smaller `SslStream` reads per MiB, is refuted.
- **Where the time goes.** In the v2 server, 84% of busy thread time is inside
  the request body read; within it the socket receive accounts for 46% of busy
  time and SChannel decryption (`SslStreamPal.DecryptMessage`) for 36%. EmbedIO's
  own frames are below 1% exclusive. Nothing in the engine's read path is a
  candidate for removal; the cost is the kernel loopback receive and TLS
  decryption, which both engines pay.
- **The two engines read differently, and the difference favours v2.** The
  v1.0.3 `RequestStream` overrides only the synchronous `Read`, so the BCL's
  default `ReadAsync` runs each read as a thread-pool work item that blocks in
  `NetworkStream.Read` (about 70 work items per request). The v2 engine reads
  asynchronously through `SslStream.ReadAsync` and completes about 3 work items
  per request.
- No code change results from this investigation.

## Host and provenance

| Item | Value |
| --- | --- |
| Host | AMD Ryzen 7 9800X3D (8 cores, 16 logical), 64 GiB, Windows 11 Pro 10.0.26300, Ultimate Performance power scheme, shared development workstation |
| CPU sets | Server logical CPUs 0-3, client 4-11, inherited at process creation |
| Toolchain | .NET SDK 10.0.402 (private install), runtime 10.0.13 for every process |
| Candidate | main `8083fed5` (v2.0.0), core built from the checkout, SHA-256 `79ff36d8...fb88` |
| Baseline | `v1.0.3` (`0f625a34`), core built from `git archive`, SHA-256 `bd2f5d40...f4ab`. The runner is compiled against assembly version 2.0.0.0 and the .NET loader refuses a lower version, so the baseline core was built from the unchanged archived source with `-p:AssemblyVersion=2.0.0.0`; no source line differs from the tag |
| Runner | Identical copies except `EmbedIO.dll`, SHA-256 `77294d91...38f9` |
| Sampler | dotnet-trace 10.0.745401 (nuget.org package SHA-512 `1C8BD456...AD01`), `dotnet-sampled-thread-time` profile, which is the only stack sampler dotnet-trace offers on Windows (`cpu-sampling` is Linux-only) |
| GC | Server GC, concurrent, every engine |
| Schedule | 3 rounds, engine order alternating, 5 s warmup, 15 s measurement, 2 s idle, fresh server and client processes per sample |

Both slots and the benchmark lock of the shared workstation were held for the
measurement window (15:11-15:38 UTC); the host was otherwise idle apart from
desktop applications.

## Comparison (v1.0.3 -> v2.0.0)

Medians of three rounds; min-max in parentheses; ratios are candidate / baseline.

| Scenario | Engine | Req/s (min-max) | CPU us/req | B/req | p50 ms | p99 ms | Pool work items/req |
| --- | --- | --- | --- | --- | --- | --- | --- |
| h1-tls-upload1m-c16 | v2.0.0 | 8,937 (8,924-8,950) | 438.1 | 6,657 | 1.690 | 5.888 | 3.0 |
|  | v1.0.3 | 7,957 (7,699-8,386) | 458.7 | 28,123 | 1.638 | 10.342 | 70.0 |
|  | ratio | 1.123 | 0.955 | 0.237 | 1.031 | 0.569 |  |
| h1-plain-upload1m-c16 | v2.0.0 | 14,278 (14,035-14,340) | 273.5 | 6,203 | 1.101 | 1.664 |  |
|  | v1.0.3 | 13,433 (13,429-13,446) | 289.1 | 27,928 | 1.152 | 2.022 |  |
|  | ratio | 1.063 | 0.946 | 0.222 | 0.956 | 0.823 |  |
| h1-tls-small-c64 | v2.0.0 | 202,974 (202,378-204,578) | 19.2 | 5,176 | 0.297 | 0.614 |  |
|  | v1.0.3 | 119,578 (118,591-120,783) | 27.5 | 10,689 | 0.473 | 1.190 |  |
|  | ratio | 1.697 | 0.696 | 0.484 | 0.628 | 0.516 |  |

The scenario prefix also selected `h1-plain-upload1m-c16-close100`, which is not
part of this comparison: the v1.0.3 core failed its warmup three times
("Server ignored Connection: close") and one v2 sample lost a connect attempt
(the known reconnect-heavy signature). Those samples are retained with the raw
data and excluded here.

Each 1 MiB upload costs the v2 server 438 us over TLS and 274 us over plain
TCP; the 165 us difference is the TLS receive and decryption cost for 1 MiB.

## Transport reads per request

Both cores were rebuilt once with a temporary counter at the transport read
(v2 `RequestStream.AccountTransportRead` and the head reads; v1.0.3
`RequestStream.Read` and the head reads), run under the same harness for one
round, and the counters written at process exit. The counters are not part of
the source. Reads per request are derived from bytes, so warmup requests do not
distort the ratio.

| Engine | Scenario | Head read bytes | Body reads per 1 MiB | Body read sizes |
| --- | --- | --- | --- | --- |
| v2.0.0 | h1-tls-upload1m-c16 | 8,189 | 65.5 | 16 KiB except one read of at most 4 KiB and one of 4-8 KiB per request |
| v1.0.3 | h1-tls-upload1m-c16 | 8,189 | 65.5 | same distribution |
| v2.0.0 | h1-plain-upload1m-c16 | 8,192 | 64.5 | 16 KiB except two short reads per 1,000 requests |
| v1.0.3 | h1-plain-upload1m-c16 | 8,192 | 64.5 | same distribution |

The handler reads with a 16 KiB buffer, the head read uses the connection's
8 KiB buffer on both engines, and `SslStream` returns at most one decrypted
record per call, so the read pattern is set by the handler and the record size,
not by the engine.

## CPU attribution

One 30 s sample per engine; dotnet-trace attached to the server for 12 s during
the measurement window. `dotnet-sampled-thread-time` samples wall time per
thread, so idle pool threads (`LowLevelLifoSemaphore.WaitForSignal`,
`WaitHandle.WaitOneNoCheck`, `IOCompletionPoller.Poll`) and the harness's
console reader (`Kernel32.ReadFile`) dominate the raw report. The busy figures
below exclude every sample whose stack contains one of those wait frames, so a
percentage is a share of busy thread time, not of wall time.

### v2.0.0 (`8083fed5`), 8,792 req/s during the sample

Raw exclusive shares of thread time: `SocketAsyncEventArgs.DoOperationReceiveSingleBuffer`
16.8%, `SslStreamPal.DecryptMessage` 13.3%, `Buffer.MemmoveInternal` 2.2%,
`Monitor.Enter_Slowpath` 1.2%, `DoOperationSendSingleBuffer` 1.1%; the largest
EmbedIO frame is `HttpListenerContext..ctor` at 0.17%.

Inclusive shares of busy thread time (36.8% of thread time was busy):

| Frame | Share of busy |
| --- | --- |
| `RequestStream.ReadConnectionAsync` (the body read) | 83.8% |
| `SslStream.ReadAsyncInternal` | 85.5% |
| `Socket.ReceiveAsync` / `DoOperationReceiveSingleBuffer` | 45.7% |
| `SslStreamPal.DecryptMessage` | 36.2% |
| `Buffer.MemmoveInternal` | 6.0% |
| Response write (`ResponseStream.WriteBodyCoreAsync` to `SslStream.WriteAsyncInternal`) | 4.1% |
| `Monitor.Enter_Slowpath` | 3.1% |
| `HttpConnection.PrepareNextRequest` | 0.7% |

The async read path (`RequestStream` -> `RawRequestBodyStream` -> `SslStream`)
adds no measurable frame of its own: the state machines appear only as callers
of the socket receive and the decrypt.

### v1.0.3 (`0f625a34`), 8,071 req/s in the preceding full sample

Raw exclusive shares: `NetworkStream.Read` 20.1% (the synchronous receive,
including its blocking time), `SslStreamPal.DecryptMessage` 8.1%,
`PortableThreadPool.WorkerThreadStart` 5.4%, `DoOperationReceiveSingleBuffer`
2.6%, `SafeDeleteContext.AcceptSecurityContext` 1.8% (TLS handshakes: v1 closes
every connection after 100 requests), `Monitor.Enter_Slowpath` 1.6%.

Inclusive shares of busy thread time (42.9% busy; the synchronous receive counts
as busy even while it waits in the kernel):

| Frame | Share of busy |
| --- | --- |
| `SslStream.ReadAsyncInternal` (`SyncReadWriteAdapter`, on a pool thread) | 67.8% |
| `NetworkStream.Read` | 46.7% |
| `SslStreamPal.DecryptMessage` | 18.7% |
| TLS handshake (`AcceptSecurityContext`) | 4.1% |
| Connection close (`HttpConnection.Close`, `SslStream.Dispose`) | 2.0% |
| Response write | 2.8% |
| `Monitor.Enter_Slowpath` | 3.7% |

The client of the traced v1.0.3 sample lost one connect attempt late in its
window (the reconnect signature recorded in the second-host baseline), so that
sample is marked failed by the harness; the server was under full load during
the 12 s trace window. The v1.0.3 throughput and CPU figures above come from the
preceding complete sample of the same run.

## Verdict

- The HTTPS upload regression recorded at `a4f7105` does not exist at 2.0.0 on
  this host: v2 leads v1.0.3 on throughput, CPU per request, allocation and p99.
- The server time of a 1 MiB HTTPS upload is the kernel loopback receive and
  SChannel decryption. The engine's read path (`TcpSessionLifetime`, the 8 KiB
  head buffer, `RequestStream`, the body-limit counting view) contributes no
  frame above 1% and issues the same number and sizes of reads as the v1 core.
- No change to the read path is proposed. The remaining TLS cost
  (165 us per MiB over the plain-text upload) is .NET's `SslStream` over
  SChannel and is paid equally by both engines.

## Not verified

- The `a4f7105` revision itself was not re-measured on this host, so whether
  the recorded gap was closed by later engine changes (#381, #384, #396, #423)
  or was specific to the EVO-X1 host is not established. Either way, 2.0.0
  carries no gap.
- Linux and macOS were not measured; `SslStream` there uses OpenSSL or Apple
  Network Framework, and the receive path differs.
- The profile is a wall-time thread sampler, not a kernel CPU sampler; the
  busy-time shares are an approximation of CPU shares.

## Reproduction

Raw samples, the census output, the `.nettrace` files, speedscope conversions
and the top-N reports are retained on the measurement host under the ignored
`TestResults/issue-436` directory of the investigation worktree.

```sh
python scripts/prepare_load_benchmark.py --baseline v1.0.3 --output TestResults/issue-436/prep-v103
# rebuild the exported v1.0.3 core with -p:AssemblyVersion=2.0.0.0 and copy it over runners/baseline/EmbedIO.dll
dotnet TestResults/issue-436/prep-v103/runners/candidate/EmbedIO.LoadBenchmark.dll run \
  --output TestResults/issue-436/compare-1 --baseline-dir TestResults/issue-436/prep-v103/runners/baseline \
  --engines candidate,baseline --scenarios h1-tls-upload1m-c16,h1-plain-upload1m-c16,h1-tls-small-c64 \
  --rounds 3 --warmup 5 --duration 15 --idle 2 --server-cpus 0-3 --client-cpus 4-11
# trace: dotnet-trace collect -p <server pid> --profile dotnet-sampled-thread-time --duration 00:00:12
```
