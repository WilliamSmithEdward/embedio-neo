# Managed engine load baseline on a second host (October 2026)

Separate-process measurements of managed engine revision `a4f7105` against itself
(noise control), the `main` core and ASP.NET Core Kestrel, for program #181. They
establish a repeatable baseline on a second, smaller host before newer engine
revisions are compared. They are development evidence, not a public benchmark.
The harness and its method are described in
[test/EmbedIO.LoadBenchmark](../../test/EmbedIO.LoadBenchmark/README.md); earlier
results on a different host are in the
[managed engine load comparison](http-engine-load-comparison.md).

## Summary

- **Noise.** Two byte-identical copies of the engine, measured back to back in the
  same rounds, agree on median throughput within 4% in all 29 scenarios, but a
  single round can differ by 2-58%. Allocation per request is the most repeatable
  metric (within 1% in most steady-state scenarios). Conclusions below require
  every paired round to move the same way and the median to fall outside that
  scenario's control band.
- **Gains over `main` (HTTP/1.1, established).** 16-deep pipelining +23% requests/s;
  64 and 256 keep-alive connections +15% and +33%; HTTPS small requests +46% with
  23% less CPU per request; 1 MiB chunked responses flushed every 16 KiB 2.2x
  requests/s at 45% of the CPU per request. Allocation per request is 11-64% lower
  in every HTTP/1.1 scenario, established in nine of them.
- **Regression against `main` (established).** HTTPS 1 MiB uploads: `main` is 18%
  faster (8 of 8 rounds, 15-23%), uses 27% less CPU per request (the engine uses
  36% more) and has about half the p99 latency, although the engine allocates 40%
  less. The previous host's report showed the same direction.
- **Kestrel.** Kestrel leads multiplexed small HTTP/2 requests by 2.5-3.6x, 16-deep
  pipelining by 7.9x, HTTP/2 request inspection by 2.5x and small HTTP/1.1 and
  HTTP/3 requests (with or without request inspection) by about 1.4x. The engine
  leads HTTP/1.1 1 MiB responses and uploads, plain HTTP/1.1 connection churn and
  HTTP/3 flushed streams, and uses about half Kestrel's CPU per HTTP/3 1 MiB
  response. HTTP/2 1 MiB uploads are 2x slower on the engine while its server is
  two-thirds idle.
- **Failures.** 4 of 617 comparison samples failed and are kept: three EmbedIO
  1 MiB upload samples lost a connect attempt for about 20 s during reconnect-heavy
  runs (Kestrel 0 of 30), and one Kestrel HTTP/3 sample ended with
  `H3_REQUEST_REJECTED`. No sample was retried.

## Host and provenance

| Item | Value |
| --- | --- |
| Host | GMKtec NucBox EVO-X1, AMD Ryzen AI 9 HX 370 (4 Zen 5 + 8 Zen 5c cores, 24 logical), 32 GiB LPDDR5X-7500, Windows 11 Pro 10.0.26200, Balanced power scheme, hypervisor and VBS on |
| CPU sets | Server logical CPUs 0-7 (all four Zen 5 cores, own 16 MiB L3); client 8-23 (all eight Zen 5c cores, own 8 MiB L3). Disjoint physical cores and caches, set at process creation |
| Toolchain | .NET SDK 10.0.401 (SHA-512 checked against Microsoft release metadata), runtime, ASP.NET Core and System.Net.Quic 10.0.12 (`95017c7`), the current supported .NET 10 patch; private install, no machine-wide changes |
| QUIC | Both engines load the runtime's `msquic.dll` 2.5.10.154561281 (SHA-256 `B7158D42...9157E8`) and System.Net.Quic 10.0.12 (SHA-256 `AF5B52CD...8BE73`), recorded from the loaded modules of the HTTP/3 server processes. Application HTTP/3 uses System.Net.Quic; no native QUIC provider is measured here |
| Engine | `a4f710574748090ef5bad0871d68f28bcf9f03be`, core built from `git archive`, SHA-256 `4BAFA826...39F3A4`, two identical runner copies (A and B) |
| Main | `1445c238afea7d5a237088e8f19c7a4e84eb43f1`, core built from `git archive`, SHA-256 `EE87F1D4...B75C47` |
| Kestrel | Shared framework 10.0.12, Kestrel.Core SHA-256 `62B5AB79...4CC239` |
| Runner | Harness at tree `80647c0` (engine source plus the request-inspection route described below), runner SHA-256 `E0A1FF64...590E22`. All runner copies are byte-identical except `EmbedIO.dll` |
| GC | Server GC, concurrent, every engine |
| Schedule | 5 rounds (8 for the HTTPS upload follow-up), engine order alternating, 5 s warmup, 15 s measurement (churn capped as in the harness), 2 s idle before each resource snapshot, fresh server and client processes per sample |

Both cores report informational version `1.0.3+8ad169f...` because the prepare
script builds the `git archive` export inside the checkout's working tree, so the
build stamps the enclosing checkout's commit. The source bytes are the archived
revisions; identify cores by hash, not by informational version.

The host was not quiet. A Hyper-V virtual machine ran throughout, alongside
desktop applications; before the runs the idle host averaged about 11% total CPU.
The harness records machine busy CPU minus server and client CPU per sample: a
median of 5-17 logical CPUs, depending on the scenario. That figure also contains
kernel loopback, TLS and QUIC networking that Windows charges to neither process,
so it overstates other work, but it means differences of a few percent are not
interpretable from single samples.

## Method changes for this baseline

- **Request inspection scenarios.** The existing scenarios only parse the path.
  Four scenarios (`h1-plain-request-c64`, `h1-tls-request-c64`,
  `h2-tls-request-c8x32`, `h3-request-c8x32`) add a route whose handler reads the
  method, path, three decoded query values, four request headers, a cookie, the
  connection's TLS state and remote address, and echoes them. The client sends fixed
  inputs and validates every response byte, as for the other routes. Both EmbedIO
  cores and Kestrel run the same handler work through their own request objects.
  This harness extension is not part of this documentation change; it is retained
  with the raw data as `harness-request-route.patch`.
- **Paired, control-calibrated comparisons.** Each comparison pairs two engines
  within the same round (they ran back to back). The A-versus-B control gives each
  scenario and metric a noise band: the largest absolute paired log ratio between
  the identical copies over all rounds. A difference is called established only when
  every paired round moves the same way and the median paired ratio lies outside
  that band. Everything else is reported as within noise.

## Noise control

Identical engine copies, B/A per round, 5 rounds (8 for the HTTPS upload follow-up).

| Scenario | Req/s ratio median (range) | Band: req/s | CPU/req | B/req | p99 |
| --- | --- | --- | --- | --- | --- |
| h1-plain-small-c64 | 1.028 (0.963-1.112) | 11% | 4% | 0.3% | 38% |
| h1-plain-small-c256 | 1.010 (0.885-1.092) | 13% | 6% | 0.7% | 12% |
| h1-plain-pipe16-c16 | 1.030 (1.016-1.135) | 14% | 7% | 0.2% | 64% |
| h1-plain-request-c64 | 0.987 (0.682-1.250) | 47% | 16% | 0.2% | 181% |
| h1-plain-stream1m-c16 | 1.006 (0.897-1.192) | 19% | 4% | 23% | 25% |
| h1-plain-upload1m-c16 | 1.006 (0.990-1.043) | 4% | 26% | 0.6% | 148% |
| h1-tls-small-c64 | 1.016 (0.976-1.223) | 22% | 9% | 11% | 73% |
| h1-tls-upload1m-c16 (8 rounds) | 1.006 (0.980-1.088) | 9% | 7% | 1.4% | 3% |
| h2-plain-small-c8x32 | 1.014 (1.000-1.057) | 6% | 5% | 0.6% | 7% |
| h2-tls-small-c8x32 | 1.005 (0.751-1.091) | 33% | 11% | 0.3% | 74% |
| h2-tls-request-c8x32 | 1.020 (0.994-1.441) | 44% | 15% | 0.4% | 117% |
| h3-small-c8x32 | 0.964 (0.799-1.078) | 25% | 31% | 1.1% | 20% |
| h3-large1m-c4x4 | 1.014 (0.982-1.584) | 58% | 17% | 0.5% | 39% |

All 29 scenarios, every metric and every sample are in the raw data. Bands
larger than about 20% mostly come from one outlying round; five rounds were enough
for large and consistent effects, not for differences under about 10%.

## Engine versus `main` (HTTP/1.1)

`main` serves HTTP/1.1 only, so HTTP/2 and HTTP/3 have no `main` rows. Ratios are
`main`/engine medians of paired rounds; `**` marks an established difference.

| Scenario | Engine | Valid | Req/s median (min-max) | p50 / p99 / p99.9 ms | CPU us/req | B/req | main/engine req/s | CPU | B/req | p99 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| h1-plain-churn-c64 | engine | 5/5 | 12,509 (12,271-18,392) | 4.403 / 8.40 / 21.30 | 268.9 | 16,793 |  |  |  |  |
|  | main | 5/5 | 11,922 (11,829-12,502) | 4.608 / 9.01 / 17.82 | 250.1 | 20,803 | 0.95 | 0.97 | 1.24 | 1.06 |
| h1-plain-large1m-c16 | engine | 5/5 | 11,233 (10,507-11,584) | 1.267 / 4.51 / 12.39 | 629.6 | 23,848 |  |  |  |  |
|  | main | 5/5 | 11,042 (10,709-11,360) | 1.267 / 4.71 / 11.67 | 626.2 | 26,941 | 0.99 | 0.99 | 1.13 | 0.97 |
| h1-plain-pipe16-c16 | engine | 5/5 | 234,030 (231,773-240,314) | 0.435 / 4.04 / 10.75 | 25.7 | 6,910 |  |  |  |  |
|  | main | 5/5 | 191,670 (116,266-218,167) | 0.461 / 5.02 / 11.67 | 30.6 | 10,447 | 0.81 ** | 1.20 ** | 1.51 ** | 1.27 |
| h1-plain-request-c64 | engine | 5/5 | 129,216 (123,739-139,976) | 0.297 / 4.20 / 10.04 | 46.2 | 12,523 |  |  |  |  |
|  | main | 5/5 | 113,559 (112,037-163,727) | 0.285 / 4.86 / 10.55 | 50.9 | 15,240 | 0.88 | 1.09 | 1.22 ** | 1.15 |
| h1-plain-small-c256 | engine | 5/5 | 154,618 (144,259-162,387) | 0.570 / 14.54 / 26.01 | 42.6 | 7,084 |  |  |  |  |
|  | main | 5/5 | 116,549 (112,330-123,208) | 0.781 / 16.38 / 26.62 | 51.4 | 10,279 | 0.75 ** | 1.20 ** | 1.45 ** | 1.13 |
| h1-plain-small-c64 | engine | 5/5 | 147,595 (141,642-176,260) | 0.237 / 3.43 / 9.32 | 41.0 | 7,131 |  |  |  |  |
|  | main | 5/5 | 129,661 (125,813-144,142) | 0.243 / 3.99 / 10.04 | 45.4 | 10,379 | 0.87 ** | 1.11 ** | 1.45 ** | 1.22 |
| h1-plain-small-c64-close100 | engine | 5/5 | 135,106 (128,941-144,667) | 0.240 / 3.89 / 10.14 | 44.3 | 7,240 |  |  |  |  |
|  | main | 5/5 | 127,177 (120,932-160,703) | 0.237 / 4.15 / 10.85 | 45.4 | 10,386 | 0.91 | 1.01 | 1.43 ** | 1.14 |
| h1-plain-stream1m-c16 | engine | 5/5 | 4,962 (4,464-5,577) | 2.278 / 13.31 / 308.02 | 1,321.1 | 30,480 |  |  |  |  |
|  | main | 5/5 | 2,262 (2,077-2,580) | 4.352 / 20.28 / 321.13 | 2,896.9 | 34,201 | 0.45 ** | 2.22 ** | 1.12 | 1.58 |
| h1-plain-upload1m-c16 | engine | 5/5 | 9,032 (9,012-9,252) | 1.536 / 9.22 / 17.20 | 631.9 | 10,106 |  |  |  |  |
|  | main | 5/5 | 9,465 (9,328-9,989) | 1.523 / 6.86 / 16.59 | 615.8 | 28,156 | 1.04 | 0.97 | 2.79 ** | 0.69 |
| h1-plain-upload1m-c16-close100 | engine | 5/5 | 8,971 (8,918-9,038) | 1.446 / 11.98 / 18.02 | 620.6 | 10,225 |  |  |  |  |
|  | main | 5/5 | 9,451 (9,414-10,511) | 1.510 / 6.66 / 16.18 | 595.3 | 28,159 | 1.05 | 0.96 | 2.75 | 0.54 |
| h1-tls-churn-c32 | engine | 5/5 | 2,782 (2,666-2,961) | 0.544 / 5.12 / 12.29 | 861.3 | 20,317 |  |  |  |  |
|  | main | 5/5 | 2,907 (2,638-3,149) | 0.576 / 4.45 / 9.32 | 842.6 | 23,084 | 1.05 | 0.96 | 1.14 | 0.87 |
| h1-tls-large1m-c16 | engine | 5/5 | 4,305 (4,011-4,346) | 2.841 / 16.38 / 27.03 | 1,519.8 | 23,942 |  |  |  |  |
|  | main | 5/5 | 4,339 (4,145-4,458) | 2.841 / 14.95 / 26.21 | 1,507.4 | 27,051 | 1.01 | 1.00 | 1.13 | 0.90 |
| h1-tls-request-c64 | engine | 5/5 | 122,509 (119,413-126,369) | 0.326 / 4.30 / 10.75 | 47.8 | 12,654 |  |  |  |  |
|  | main | 5/5 | 100,535 (86,808-114,841) | 0.435 / 3.07 / 8.81 | 58.3 | 15,494 | 0.82 | 1.20 ** | 1.22 ** | 0.73 |
| h1-tls-small-c64 | engine | 5/5 | 146,882 (141,146-153,491) | 0.291 / 3.23 / 8.70 | 41.5 | 7,284 |  |  |  |  |
|  | main | 5/5 | 101,628 (99,942-103,512) | 0.378 / 3.79 / 11.37 | 53.5 | 10,605 | 0.68 ** | 1.29 ** | 1.46 ** | 1.20 |
| h1-tls-upload1m-c16 (8 rounds) | engine | 8/8 | 4,240 (4.2k-4.3k) | 2.893 / 18.43 / 20.69 | 1,469 | 16,956 |  |  |  |  |
|  | main | 8/8 | 4,955 (4.9k-5.3k) | 2.880 / 9.68 / 19.25 | 1,081 | 28,267 | 1.18 ** | 0.73 ** | 1.67 ** | 0.52 ** |

The first 5-round HTTPS upload comparison agreed (`main` 1.13x requests/s, 0.76x
CPU per request) but had only three valid `main` samples, so it was repeated with
8 rounds and its own 8-round control.

## Engine versus Kestrel

Same rounds as the noise control. Ratios are Kestrel/engine.

| Scenario | Engine | Valid | Req/s median (min-max) | p50 / p99 / p99.9 ms | CPU us/req | B/req | Kestrel/engine req/s | CPU | B/req | p99 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| h1-plain-churn-c64 | engine | 5/5 | 14,615 (12,540-18,544) | 3.789 / 7.88 / 15.97 | 217.3 | 17,952 |  |  |  |  |
|  | Kestrel | 5/5 | 11,621 (10,721-13,323) | 4.761 / 10.24 / 15.36 | 243.6 | 8,940 | 0.82 ** | 1.14 | 0.50 ** | 1.25 |
| h1-plain-large1m-c16 | engine | 5/5 | 10,551 (10,414-12,547) | 1.293 / 5.17 / 12.60 | 636.0 | 23,853 |  |  |  |  |
|  | Kestrel | 5/5 | 6,574 (6,170-6,699) | 1.946 / 9.11 / 19.25 | 949.5 | 513 | 0.61 ** | 1.49 ** | 0.02 ** | 1.85 ** |
| h1-plain-pipe16-c16 | engine | 5/5 | 236,790 (229,817-252,500) | 0.429 / 3.99 / 10.24 | 24.9 | 6,905 |  |  |  |  |
|  | Kestrel | 5/5 | 1,864,613 (1,814,383-1,933,971) | 0.109 / 0.65 / 2.07 | 3.3 | 32 | 7.89 ** | 0.13 ** | 0.00 ** | 0.16 ** |
| h1-plain-request-c64 | engine | 5/5 | 147,264 (129,394-185,607) | 0.294 / 3.38 / 10.65 | 44.6 | 12,542 |  |  |  |  |
|  | Kestrel | 5/5 | 190,835 (175,471-227,060) | 0.275 / 1.47 / 5.79 | 33.7 | 1,552 | 1.24 | 0.76 ** | 0.12 ** | 0.51 |
| h1-plain-small-c256 | engine | 5/5 | 149,080 (141,270-160,933) | 0.595 / 14.34 / 25.80 | 42.0 | 7,088 |  |  |  |  |
|  | Kestrel | 5/5 | 216,166 (205,571-218,955) | 1.101 / 3.43 / 8.19 | 34.4 | 33 | 1.45 ** | 0.82 ** | 0.00 ** | 0.23 ** |
| h1-plain-small-c64 | engine | 5/5 | 156,431 (144,111-179,816) | 0.234 / 3.00 / 8.91 | 40.5 | 7,121 |  |  |  |  |
|  | Kestrel | 5/5 | 220,092 (215,608-244,285) | 0.259 / 1.11 / 3.58 | 32.0 | 32 | 1.41 ** | 0.79 ** | 0.00 ** | 0.33 ** |
| h1-plain-small-c64-close100 | engine | 5/5 | 145,665 (135,460-160,769) | 0.240 / 3.17 / 10.85 | 42.3 | 7,264 |  |  |  |  |
|  | Kestrel | 5/5 | 204,957 (198,439-212,622) | 0.259 / 1.24 / 4.51 | 34.2 | 126 | 1.41 ** | 0.81 ** | 0.02 ** | 0.37 ** |
| h1-plain-stream1m-c16 | engine | 5/5 | 4,766 (4,490-5,462) | 2.304 / 13.11 / 308.02 | 1,345.5 | 30,477 |  |  |  |  |
|  | Kestrel | 5/5 | 6,715 (6,584-8,657) | 1.946 / 7.88 / 17.82 | 918.8 | 6,650 | 1.50 ** | 0.68 ** | 0.22 ** | 0.55 ** |
| h1-plain-upload1m-c16 | engine | 5/5 | 9,003 (8,929-9,214) | 1.485 / 10.65 / 17.61 | 638.0 | 10,116 |  |  |  |  |
|  | Kestrel | 5/5 | 7,579 (6,328-7,975) | 1.920 / 6.40 / 11.57 | 926.1 | 686 | 0.82 ** | 1.47 ** | 0.07 ** | 0.66 |
| h1-plain-upload1m-c16-close100 | engine | 4/5 | 9,386 (8,909-10,136) | 1.414 / 8.50 / 14.95 | 571.8 | 10,229 |  |  |  |  |
|  | Kestrel | 5/5 | 6,552 (6,339-7,100) | 2.253 / 6.55 / 12.08 | 1,027.8 | 1,032 | 0.70 ** | 1.82 ** | 0.10 ** | 0.91 |
| h1-tls-churn-c32 | engine | 5/5 | 2,836 (2,706-3,226) | 0.563 / 5.53 / 11.06 | 839.7 | 20,329 |  |  |  |  |
|  | Kestrel | 5/5 | 2,814 (2,805-2,975) | 0.582 / 5.02 / 9.63 | 697.3 | 12,949 | 1.01 | 0.82 ** | 0.64 | 0.98 |
| h1-tls-large1m-c16 | engine | 5/5 | 4,290 (4,152-4,939) | 2.841 / 15.56 / 26.62 | 1,494.4 | 23,939 |  |  |  |  |
|  | Kestrel | 5/5 | 4,063 (3,718-4,400) | 3.277 / 17.00 / 28.26 | 1,624.8 | 7,448 | 0.93 | 1.10 ** | 0.31 ** | 1.01 |
| h1-tls-request-c64 | engine | 5/5 | 125,116 (121,146-132,953) | 0.320 / 4.35 / 10.44 | 47.3 | 12,642 |  |  |  |  |
|  | Kestrel | 5/5 | 191,272 (166,063-217,683) | 0.294 / 1.28 / 5.68 | 35.9 | 1,553 | 1.44 ** | 0.76 ** | 0.12 ** | 0.30 ** |
| h1-tls-small-c64 | engine | 5/5 | 146,547 (132,950-148,217) | 0.291 / 3.23 / 9.32 | 42.3 | 7,284 |  |  |  |  |
|  | Kestrel | 5/5 | 204,398 (198,201-214,356) | 0.275 / 1.43 / 4.81 | 34.1 | 33 | 1.43 ** | 0.81 ** | 0.00 ** | 0.43 ** |
| h1-tls-upload1m-c16 | engine | 5/5 | 4,247 (4,198-4,365) | 2.918 / 18.43 / 20.68 | 1,463.1 | 16,919 |  |  |  |  |
|  | Kestrel | 5/5 | 4,215 (4,171-4,452) | 2.867 / 18.43 / 21.09 | 1,686.2 | 734 | 0.98 | 1.15 ** | 0.04 ** | 1.01 |
| h2-plain-small-c8x32 | engine | 5/5 | 254,016 (251,698-261,848) | 0.563 / 6.35 / 9.52 | 22.1 | 10,190 |  |  |  |  |
|  | Kestrel | 5/5 | 930,362 (861,282-1,181,976) | 0.205 / 1.29 / 2.92 | 5.9 | 32 | 3.62 ** | 0.27 ** | 0.00 ** | 0.20 ** |
| h2-tls-churn-c32 | engine | 5/5 | 1,571 (1,257-1,943) | 19.251 / 34.41 / 40.55 | 1,618.4 | 39,290 |  |  |  |  |
|  | Kestrel | 5/5 | 1,676 (1,434-1,713) | 17.613 / 35.23 / 43.01 | 1,347.2 | 37,295 | 1.07 | 0.84 | 0.96 | 0.94 |
| h2-tls-large1m-c4x4 | engine | 5/5 | 2,509 (2,380-2,584) | 5.888 / 14.95 / 23.14 | 2,315.3 | 61,304 |  |  |  |  |
|  | Kestrel | 5/5 | 2,547 (2,498-2,606) | 5.785 / 13.31 / 17.82 | 2,598.8 | 9,578 | 1.04 | 1.12 ** | 0.15 ** | 0.90 |
| h2-tls-request-c8x32 | engine | 5/5 | 207,243 (194,151-213,627) | 0.665 / 7.68 / 11.47 | 26.0 | 14,618 |  |  |  |  |
|  | Kestrel | 5/5 | 522,266 (509,236-577,061) | 0.339 / 2.71 / 5.94 | 10.7 | 1,699 | 2.54 ** | 0.41 ** | 0.12 ** | 0.37 ** |
| h2-tls-small-c64x1 | engine | 5/5 | 132,450 (126,097-162,177) | 0.294 / 3.28 / 8.60 | 44.3 | 8,816 |  |  |  |  |
|  | Kestrel | 5/5 | 169,043 (156,428-215,809) | 0.294 / 1.92 / 8.09 | 37.0 | 363 | 1.25 | 0.84 ** | 0.04 ** | 0.58 |
| h2-tls-small-c8x32 | engine | 5/5 | 279,062 (235,033-353,869) | 0.531 / 5.79 / 8.40 | 20.3 | 10,235 |  |  |  |  |
|  | Kestrel | 5/5 | 744,488 (716,385-781,053) | 0.259 / 1.60 / 3.58 | 8.1 | 46 | 2.67 ** | 0.40 ** | 0.00 ** | 0.25 ** |
| h2-tls-stream1m-c4x4 | engine | 5/5 | 2,409 (2,342-2,633) | 6.093 / 14.34 / 22.12 | 2,403.1 | 88,945 |  |  |  |  |
|  | Kestrel | 5/5 | 2,473 (2,257-2,690) | 5.785 / 14.75 / 21.09 | 2,427.0 | 38,187 | 1.04 | 1.01 | 0.42 ** | 1.03 |
| h2-tls-upload1m-c4x4 | engine | 5/5 | 548 (513-588) | 27.443 / 65.54 / 77.00 | 4,867.9 | 138,388 |  |  |  |  |
|  | Kestrel | 5/5 | 1,076 (1,058-1,121) | 15.360 / 25.19 / 32.77 | 5,850.9 | 10,619 | 1.96 ** | 1.20 ** | 0.08 ** | 0.38 ** |
| h3-churn-c16 | engine | 5/5 | 727 (692-875) | 18.432 / 66.36 / 87.65 | 2,282.7 | 123,362 |  |  |  |  |
|  | Kestrel | 5/5 | 710 (677-883) | 18.841 / 67.99 / 88.47 | 2,159.8 | 141,201 | 1.03 | 0.93 | 1.14 | 1.00 |
| h3-large1m-c4x4 | engine | 5/5 | 818 (524-839) | 19.251 / 32.77 / 47.51 | 5,500.1 | 15,113 |  |  |  |  |
|  | Kestrel | 5/5 | 534 (489-580) | 27.853 / 62.26 / 82.74 | 11,227.3 | 1,363,627 | 0.65 | 2.09 ** | 90.23 ** | 1.83 ** |
| h3-request-c8x32 | engine | 5/5 | 107,322 (91,515-175,055) | 1.549 / 14.95 / 24.17 | 72.5 | 16,888 |  |  |  |  |
|  | Kestrel | 5/5 | 152,395 (136,234-239,803) | 1.177 / 11.47 / 19.05 | 53.3 | 4,155 | 1.39 ** | 0.73 ** | 0.25 ** | 0.76 |
| h3-small-c8x32 | engine | 5/5 | 129,428 (116,789-147,135) | 1.242 / 13.00 / 20.68 | 52.3 | 12,479 |  |  |  |  |
|  | Kestrel | 4/5 | 170,185 (151,428-193,746) | 1.011 / 9.68 / 17.72 | 43.8 | 2,507 | 1.39 ** | 0.82 | 0.20 ** | 0.73 |
| h3-stream1m-c4x4 | engine | 5/5 | 783 (773-799) | 20.070 / 33.59 / 47.51 | 6,722.6 | 44,689 |  |  |  |  |
|  | Kestrel | 5/5 | 575 (560-724) | 27.443 / 43.01 / 63.08 | 12,357.9 | 8,627 | 0.73 ** | 1.79 ** | 0.19 ** | 1.42 ** |
| h3-upload1m-c4x4 | engine | 5/5 | 779 (634-804) | 20.480 / 30.31 / 34.41 | 4,926.6 | 81,807 |  |  |  |  |
|  | Kestrel | 5/5 | 767 (628-1,016) | 20.890 / 29.49 / 35.23 | 7,123.1 | 16,602 | 0.99 | 1.43 ** | 0.20 ** | 0.97 |

Connection setup (churn rows) is on par for HTTPS, HTTP/2 and HTTP/3: median
connections per second are within 7% across the engine, `main` and Kestrel. For
plain HTTP/1.1 churn the engine opens 18% more connections per second than Kestrel.

## Resources, retention and sockets

- **Contention.** The engine records 2.5-4.7 lock contentions per 1,000 small
  HTTP/1.1 and HTTP/2 requests and 17-24 per 1,000 small HTTP/3 requests; Kestrel
  records about 0.01 and 2. HTTP/2 1 MiB transfers record 117-165 per 1,000 on the
  engine and 46-441 on Kestrel.
- **Exceptions.** Steady-state scenarios throw none per request on either engine.
  Churn throws per connection: HTTPS churn 0.010 per request (Kestrel 0.014), HTTP/2
  churn 1.5 (Kestrel 0.68), HTTP/3 churn 18 (Kestrel 12), mostly
  `QuicException`, cancellation and `ChannelClosedException` during teardown.
  HTTP/3 bulk transfers throw about 6 per 1,000 requests on both engines.
- **GC.** Gen0 collections per 100,000 small requests: engine 22-58, Kestrel 0.5-20.
  One engine `-close100` sample reached 36 gen2 collections; the engine's other
  samples stayed at 0-7.
- **Retention.** After each sample, idle and a forced GC, no server TCP socket
  remained open other than TIME_WAIT (0 in all 617 samples), and no process had to be
  killed. EmbedIO cores' managed heaps grew by at most 0.9 MB over the post-warmup
  snapshot and handle counts by at most 184. Kestrel's heap grew by up to 17.8 MB
  (HTTP/2 1 MiB responses); a single post-GC delta is not evidence of a leak for
  either engine.
- **TIME_WAIT and ports.** OS limits were not changed. Client-side TIME_WAIT stayed
  under the 4,000 gate; machine-wide (mostly server-side) TIME_WAIT reached 19,992.
  79 samples waited for the backlog to drain, at most 116 s, 85 minutes in total.

## Failures

Every failure keeps its error, client counters and a TCP state census.

| Run | Scenario | Engine | Error |
| --- | --- | --- | --- |
| Control | h1-plain-upload1m-c16-close100 r2 | engine | Client connect timed out |
| Control | h3-small-c8x32 r4 | Kestrel | `H3_REQUEST_REJECTED` (0x10b) after 2.66 million requests |
| Engine vs `main` | h1-tls-upload1m-c16 r3, r5 | `main` | Client connect timed out |

The three connect timeouts share the earlier report's signature: EmbedIO only,
1 MiB uploads with frequent reconnects (`main` closes after 100 requests; the
`-close100` client does the same), a measurement window stretched from 15 s to
27-36 s by one connect attempt left unanswered for about 20 s, and 16,500-18,500
server-side TIME_WAIT entries at the start of the sample. Kestrel failed 0 of its 30
upload samples. The 16 samples of the 8-round HTTPS upload follow-up did not fail.
The cause is not identified. The Kestrel HTTP/3 rejection was not investigated.

## Bottlenecks, with evidence

Evidence comes from a separate profiling run (`--profile`: sampled allocation by
type, exceptions and contention, one round), never from the comparison samples.
No stack sampler was installed on this host, so CPU is not attributed to methods.

1. **HTTP/2 request bodies.** HTTP/2 1 MiB uploads run at half Kestrel's rate
   while the engine's server uses 33-35% of its CPUs and the client 20%: the
   transfer waits rather than computes. The profile shows about 65 KB per request
   of per-read asynchronous machinery in the body path: `Http2RequestBody.ReadAsync`
   state machines, `Task<bool>` and `Task<int>` instances, frame-read state machines
   and a `WhenAny` promise per read, plus 148 contentions per 1,000 requests.
   Per-chunk delivery or flow-control latency is the likely limit; window sizes were
   not traced.
2. **HTTPS upload regression.** On the same scenario the engine allocates a
   `RequestStream.ReadTransportAsync` state machine (about 7 KB per request) where
   `main` uses the BCL's `ReadWriteTask`, and the engine uses 1,408 us of CPU per
   request against 1,080 us for `main`. The plain-text upload is on par, so the extra
   cost is specific to the TLS read path. Which frames account for it needs a
   stack-sampling profile.
3. **Small-request object model.** Per small HTTP/1.1 request the engine allocates
   about 7 KB, of which the largest sampled types are `String`, Hashtable
   `Bucket[]`, `Object[]`, `ArrayList`, `Hashtable`, `NameValueCollection`,
   `WebHeaderCollection` and `Uri`: the header and query collection model. The
   request-inspection route adds about 5.4 KB on HTTP/1.1 and 4.4 KB on HTTP/2; Kestrel
   allocates about 1.5 KB for the same handler work.
4. **Remaining contention.** Aggregate lock contention on every protocol's
   small-request path remains two orders of magnitude above Kestrel. The measured
   revision already skips the listener lifecycle lock during ordinary request
   registration; accepting a context still takes that lock on entry and exit.
   Without stack sampling, these counts do not identify which locks account for
   the remaining cost. Attribute them before choosing another admission change.
5. **Multiplexed and pipelined small requests.** These are the largest gaps to
   Kestrel (HTTP/2 32 streams 2.5-3.6x, pipelining 7.9x) and are where CPU per request
   differs most (2.5-7.5x).

## Limitations

- Loopback on one host, closed-loop clients; no network latency, loss or real
  client mix. Overload and coordinated omission are not characterized.
- The host was shared with a running virtual machine and desktop applications, on
  the Balanced power scheme. Background load is recorded, not removed.
- Heterogeneous cores: the server ran on the four Zen 5 cores and the client on the
  eight Zen 5c cores. Absolute numbers are not comparable with the earlier
  9800X3D host.
- Five rounds resolve large, consistent effects. Differences under about 10% that
  are reported as within noise may be real; more rounds would be needed.
- The HTTP/2 and HTTP/3 client is `HttpClient`, heavier than the raw HTTP/1.1 client.
  Client utilization stayed below 80% in every scenario.
- Application HTTP/3 here uses System.Net.Quic on the runtime's MsQuic 2.5.10.
  Native QUIC provider work in this program is not measured as application
  performance; that requires an integrated revision.

## Reproduction

Raw samples (per-sample JSON with full latency histograms), manifests, runner file
hashes, logs and the analysis scripts are retained on the measurement host under
the ignored `TestResults/perf` directory of the benchmark worktree; they are not
committed.

The commands below outline the campaign; this documentation PR alone cannot
reproduce all 29 scenarios. The four request-inspection scenarios require the
exact `harness-request-route.patch` retained on that host. Obtain that patch and
the full hash manifests before reproducing those scenarios or independently
checking the paired comparisons. The abbreviated hashes above identify the
reported artifacts but are not substitutes for their complete manifests.

```sh
# Check out the engine revision with the request-inspection harness patch applied.
git worktree add --detach <src> a4f710574748090ef5bad0871d68f28bcf9f03be
git -C <src> am <harness-request-route.patch>

# Build runners: candidate = checkout, baseline core = git archive of the revision.
python scripts/prepare_load_benchmark.py --baseline a4f710574748090ef5bad0871d68f28bcf9f03be --output TestResults/prep-engine
python scripts/prepare_load_benchmark.py --baseline 1445c238afea7d5a237088e8f19c7a4e84eb43f1 --output TestResults/prep-main
# Copy prep-engine/runners/baseline twice (A, B) and prep-main/runners/baseline once;
# verify that the copies differ only in EmbedIO.dll.

# Noise control plus Kestrel, all scenarios.
dotnet <A>/EmbedIO.LoadBenchmark.dll run --output <results>/control --baseline-dir <B> --modern-baseline \
  --candidate-revision a4f7105... --baseline-revision a4f7105... \
  --rounds 5 --warmup 5 --duration 15 --idle 2 --server-cpus 0-7 --client-cpus 8-23

# Engine versus main, HTTP/1.1 scenarios.
dotnet <A>/EmbedIO.LoadBenchmark.dll run --output <results>/main --baseline-dir <main> \
  --engines candidate,baseline --scenarios h1- \
  --candidate-revision a4f7105... --baseline-revision 1445c23... \
  --rounds 5 --warmup 5 --duration 15 --idle 2 --server-cpus 0-7 --client-cpus 8-23

# HTTPS upload follow-up: the same two commands with --scenarios h1-tls-upload1m-c16
# --engines candidate,baseline --rounds 8, once against <main> and once against <B>.

# Profiles (separate from comparisons): add --profile --rounds 1.
```

Choose server and client CPU sets for the host's own topology: disjoint physical
cores, preferably separate cache complexes.

## Next comparison

The engine branch advanced to `ba253fa` during this work, including bounded
HTTP/1.1 response writes (#258) and four new HTTP/1.1 chunked and HTTPS streaming
scenarios. Comparing it with `a4f7105` on this host, with the same controls, is the
natural next pair once it is agreed.
