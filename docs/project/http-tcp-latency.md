# Managed TCP write latency

This Neo v2 development increment sets TCP_NODELAY on each accepted managed TCP
socket before NetworkStream/TLS construction. It applies to HTTP/1, HTTP/2 and
protocol handoffs over that socket. The Microsoft listener and UDP/QUIC provider
are unchanged. There is no public API, target, dependency, framing, request-count
or timeout change.

The previous connection constructor left the socket's default Nagle policy
active. Small writes can wait for acknowledgements of earlier bytes, including
when a response head and its body are submitted separately. See the
[Socket.NoDelay contract](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.nodelay?view=net-10.0).
Protocol writers retain their own batching; this socket option does not change
HTTP frame boundaries or bytes.

## Reproduction and controlled comparison

The Linux job for HTTP/3 cancellation PR #252, run 38078186731, exhausted its
five-minute suite budget after 4841 of 4940 reported cases, with no failed case.
Its completed-case durations included 10.43 and 10.72 seconds for the plain/TLS
keep-alive cases and 14.34 seconds for the HTTP/2 reset case. These were candidates
for production latency investigation, not assertions to remove or shorten.

The comparison uses engine base 38edf2d and changes only HttpConnection's socket
option. It uses the same compiled test assembly, swapping only EmbedIO.dll in
fresh Linux containers on one host. The unchanged cases perform 256 sequential
8 KiB uploads per plain/TLS connection with body, reuse, header and cleanup
assertions; the reset case retains all 500 seeded reset/PING trials and requires
that no HTTP/2 connection is lost.

Pinned image: sha256:cad57be0903a303f62b6492f695d658256f0c728af9a9c5454c839093fb29df3,
SDK 10.0.401, runtime 10.0.12, four container CPUs and 6 GiB memory. The compiled
IL is built on Windows. A shared workload lock excludes concurrent tests and
benchmarks. Two comparison rounds run candidate then baseline, in fresh
processes/containers; an initial baseline establishes the reproduction before
editing the constructor. No sample or failed attempt is silently retried.

| Unchanged test | Baseline round 1 / 2, seconds | TCP_NODELAY round 1 / 2, seconds |
| --- | --- | --- |
| Plain keep-alive, 256 requests | 11.263 / 11.273 | 0.161 / 0.153 |
| TLS keep-alive, 256 requests | 11.487 / 11.435 | 0.227 / 0.260 |
| HTTP/2, 500 reset/PING trials | 16.092 / 16.073 | 1.442 / 1.405 |

All cases pass with zero skips in every run. Whole group duration is about
39.3 seconds on the baseline and 2.3 seconds on the candidate. This supports a
socket-delay explanation for these regression workloads; no packet trace was
captured to independently attribute each delay to a particular acknowledgement.
It is not a sustained throughput benchmark or a guarantee for another network.
Packet counts, congested networks and broader CPU/load effects remain unmeasured.

Baseline core SHA-256:
2DA96E3A744595B072798D0571269EF4E23F3A0C4ADA7C7ABF385FB14D1A0495.
Candidate core SHA-256:
FF4902180117C47D755F9F56D428A28D5A5391B74F69002CA3E85E9C4D1445EB.
Logs, before/after cores and TRX files are under ignored TestResults/tcp-latency.

## Acceptance and coordination

Both target builds are warning-free. The full Windows suite passes all 4903
reported cases (4898 passed, five expected skips), in 3 minutes 22 seconds.
A coverage-enabled Linux source build on a Windows-mounted directory reported
all 4903 cases in 2 minutes 39 seconds, with one failure in the case-sensitive
file fixture: its assembly-relative data directory was on the case-insensitive
mount, while its probe used Linux /tmp. That run is retained as failed setup
evidence, not acceptance. After retaining a missing-cache build attempt and a
CRLF command-text setup
failure, the corrected Linux-filesystem run with locked restore and coverage
passes all 4903 cases (4874 passed, 29 expected platform skips), zero failures,
in 2 minutes 36 seconds; both target builds have zero warnings or errors.
Hosted Linux and Windows regression jobs pass on 1fdbc4a. The macOS job
reports 3918 of 4903 cases, zero failed cases, before the unchanged five-minute
budget expires; it remains a failed acceptance gate under investigation. Its
TRX reports 282.22 seconds of completed-case time across 3918 results; this
is evidence of accumulated runtime, not a captured single stuck case. The
existing five-minute suite
budget and discovery floor of 4903 remain enforced; no assertions, counts,
repeats or per-case deadlines are reduced.

The response-write agent owns ResponseStream batching. This increment changes
only connection setup and must be measured together with that work after
integration. Existing before/after response-write numbers do not include this
socket change. Program #181 remains open and Neo v2 is unreleased.
## Combined HTTP/1 response-write integration

The isolated integration branch combines TCP candidate 09e3df0 with the reviewed
HTTP/1 response-write PR #256 at df5d5df on current engine base 40e0251. The
production HttpConnection and ResponseStream files are byte-identical to their
respective reviewed heads; the v2 guide is retained. The 32 new response-write
cases raise the discovery floor to 4935 in CI, CONTRIBUTING and the native-close
experiment. At initial reconciliation the five-minute budget, assertions,
iterations, coverage and raw
rebind classifier are unchanged.

The individual measurements above and the response-write report do not measure
this combined engine. Fresh combined Windows and pinned Linux source/coverage
validation and exact-head hosted checks remain required. Neither constituent PR
is integrated into the engine by this reconciliation alone.
William approved an eight-minute full-suite CI budget on Linux/macOS on
2026-10-10, retaining five minutes on Windows. The combined branch applies that
approval with the 4935 discovery floor; per-case deadlines, iterations,
assertions, coverage and the narrow raw-rebind classifier remain unchanged.
The queued local comparison still uses five minutes on both platforms.
