# Independent QUIC engine feasibility

This is an isolated development experiment in `test/EmbedIO.QuicPrototype`, outside
`EmbedIO.sln` and every shipped package. It establishes that original C# QUIC packet
and stream handling can use an existing TLS provider and communicate with an
independent implementation. It adds no public listener mode or application API.
The production HTTP/3 listener continues to use its existing provider. Public
WebTransport APIs remain unavailable. The prototype now includes loss recovery,
NewReno congestion control and an experimental HTTP/3/WebTransport adapter.

## Scope and provenance

The implementation was written from RFC 9000, RFC 9001 and the documented OpenSSL
third-party QUIC TLS API. It does not call MsQuic or `System.Net.Quic`, nor copy their
transport implementation. Earlier project investigation included reading MsQuic
source, so this work is described as an independent standards-based implementation,
not as a formally separated clean-room development process.

References:

- [RFC 9000](https://www.rfc-editor.org/rfc/rfc9000.html), packet numbers, framing,
  stream final size, connection identifiers and address-validation accounting.
- [RFC 9001](https://www.rfc-editor.org/rfc/rfc9001.html), initial secrets,
  packet/header protection, TLS CRYPTO transport and alert mapping.
- [OpenSSL 3.5 QUIC TLS callbacks](https://docs.openssl.org/3.5/man3/SSL_set_quic_tls_cbs/).
  The runtime is deliberately pinned to OpenSSL 3.5.7 for this experiment.
- [RFC 9002](https://www.rfc-editor.org/rfc/rfc9002.html), ACK ranges, RTT sampling,
  packet/time loss thresholds, PTO, NewReno, persistent congestion and pacing.
- [WebTransport HTTP/3 draft 16](https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-16)
  and [reliable stream reset draft 11](https://datatracker.ietf.org/doc/html/draft-ietf-quic-reliable-stream-reset-11).
  The experimental adapter uses current parameter `0x1d` and frame `0x24`.

OpenSSL performs the TLS 1.3 handshake, certificate/key handling and secret
calculation. The original C# code owns UDP, QUIC packetization, packet protection,
CRYPTO reassembly and the stream exchange. AES-GCM, AES and HKDF use the .NET
cryptography implementations. Only TLS_AES_128_GCM_SHA256 is enabled. The TLS adapter
uses OpenSSL's public C API and owns its callback buffers and native handles.
No OpenSSL source or binary is redistributed. OpenSSL is Apache-2.0 licensed;
consumers of a future distributed provider would need a separate dependency and
packaging decision. The independent test client reuses the existing, hash-pinned
`test/EmbedIO.Conformance/drivers/requirements.txt` dependencies.

## Feasibility profile

The executable always binds IPv4 loopback on an ephemeral port. Each process
accepts one connection with a ten-second deadline. It has two application modes:

- `serve` keeps the original `embedio-quic-probe` ALPN, one bidirectional stream
  and 8192-byte exact echo.
- `serve-h3` negotiates `h3`, sends HTTP/3 SETTINGS, decodes bounded QPACK headers
  with dynamic capacity zero, and accepts `CONNECT :protocol=webtransport-h3` at
  `https://localhost/echo`. It links the existing repository codecs and session
  core into this test executable; production files are not copied or modified.

The H3 profile requires the current reset parameter, QUIC/HTTP datagrams,
WebTransport settings, the exact loopback Origin and a fresh per-process bearer
value. The harness receives that value through the startup pipe, passes it to its
child through the environment and removes it from retained logs. This is a test
admission callback, not integration with the shipped bearer/JWT middleware.
At most two sessions are admitted when session flow control is negotiated; the
existing session core allows one when it is not. Bidirectional and unidirectional
stream echo, datagram association, close capsules, session faults and partial
reset delivery use the same session ownership logic as the internal core.

Recovery runs on an injected monotonic clock. ACK ranges retire physical packets
once; retransmissions get new packet numbers while retaining logical delivery
identity. Late ACKs cancel redundant retransmissions without subtracting in-flight
bytes twice. Packet and time thresholds schedule retransmission; PTO probes retain
anti-amplification checks. NewReno reduces its window once per recovery epoch,
observes the two-packet minimum, detects persistent congestion and paces sends.
Application-limited ACKs do not increase the congestion window. QUIC datagrams
consume congestion credit but are not retransmitted.

Bounds remain deliberate: 64 KiB CRYPTO reassembly per encryption level, 4096 sent
packet numbers per space, 4096 active recovery records, bounded ACK/loss history
and 512 queued packets. H3 permits 16 incoming streams of each direction, 64 KiB
per stream and 256 KiB total receive credit. Final reset sizes consume credit once,
including gaps. The prototype waits for the reliable prefix before reporting a
partial reset and preserves the header when a local reset waits for send credit.
It does not replenish the connection receive window for indefinite transfers.
Pre-validation sends obey the three-times-received byte allowance.

Peer-initiated key updates derive fresh packet secrets while preserving header
protection, mirror the peer's send phase, and retain the previous receive keys for
reordering during three PTO durations. The larger peer cases require a recorded
key update and complete exact-byte transfer.

This remains a restricted experiment. It has no Retry, migration, locally initiated
key updates, resumption, 0-RTT, complete post-handshake TLS processing,
multi-connection listener, complete draining/closing state
machine or production provider interface. It supports a bounded H3 echo profile,
not a general request dispatcher, public application facade or browser release.
Do not change the bind address or embed this executable into an application.

## Reproduce on Windows x64

Use the SDK pinned by `global.json`, Python 3.11+ and an existing OpenSSL 3.5.7 x64
installation with both `libssl-3-x64.dll` and `libcrypto-3-x64.dll`. Set the absolute
library directory explicitly; the executable checks the version and the receipt
records the actual library SHA-256 values. No system library is replaced.

From the repository root, in PowerShell:

```powershell
$opensslDirectory = 'C:\path\to\OpenSSL-3.5.7\bin'
python -m venv TestResults/quic-prototype-python
$python = 'TestResults/quic-prototype-python/Scripts/python.exe'
& $python -m pip install --require-hashes --only-binary=:all: -r test/EmbedIO.Conformance/drivers/requirements.txt
dotnet restore test/EmbedIO.QuicPrototype/EmbedIO.QuicPrototype.csproj --locked-mode
dotnet build test/EmbedIO.QuicPrototype/EmbedIO.QuicPrototype.csproj -c Release --no-restore
$assembly = 'test/EmbedIO.QuicPrototype/bin/Release/net10.0/EmbedIO.QuicPrototype.dll'
dotnet $assembly self-test
& $python test/EmbedIO.QuicPrototype/verify.py --dotnet (Get-Command dotnet).Source --assembly $assembly --openssl $opensslDirectory --output TestResults/quic-prototype-wire
```

Use a fresh output directory for each run to retain earlier evidence. The harness
records process output, exact source/assembly/library hashes and a machine-readable
`receipt.json`. A rejection requires its expected QUIC error and a normal process
exit; a timeout or crash is not accepted as proof of rejection. The peer must match
the repository's aioquic pin. The temporary certificate is valid for one day.

## Observed local result

Windows x64 with SDK 10.0.402 and OpenSSL 3.5.7 completed the following local
checks. Run receipts retain exact assembly/source/native-library identities.

- Fifteen aioquic 1.6.0 cases cover the original ten interoperability cases plus
  Initial/handshake/response loss, ACK loss and reordering. The designated reliable
  loss cases require actual retransmitted packets as well as an exact response.
- Thirteen quic-go 0.63.0 cases cover H3 settings and CONNECT, bidi/uni stream echo,
  datagrams, two sessions and quota rejection, authorization/Origin rejection,
  missing current-reset negotiation, incoming and outgoing partial resets,
  a malformed capsule followed by a healthy sibling, larger transfers, withheld
  transport credit, peer key updates and handshake/response loss.
- The original 10,076 primitive assertions remain. Additional deterministic
  recovery and bridge regressions cover packet spaces, RTT/ACK delay, PTO backoff,
  late/duplicate ACKs, persistent congestion, blocked credit, sparse reliable
  prefixes, final-size conflicts and session admission. A seeded recovery campaign
  checks 10,000 state transitions plus complete retirement of each run.

CI builds the standalone project and runs the managed checks without loading
OpenSSL or adding another job. The two independent wire harnesses remain explicit
local checks. No Linux/macOS wire validation, browser interoperability, latency
comparison or production acceptance is claimed.

### Current-wire peer

Use Go 1.27.3 for the independent peer. Its source dependencies are pinned in
`test/EmbedIO.QuicPrototype/peer/go.mod` and authenticated by `go.sum`. The Windows
amd64 toolchain archive is
[`go1.27.3.windows-amd64.zip`](https://go.dev/dl/go1.27.3.windows-amd64.zip), SHA-256
`f26081f438319757f8a1b46d7d0b2c5ec7583d4038e485e0aa725cfde7885fff`.
The local validation used that exact archive. Go is BSD-3-Clause licensed;
quic-go and qpack are MIT licensed. They are development-only and are not added to
any NuGet dependency group or package payload.

```powershell
$env:GOTOOLCHAIN = 'local'
go version # Expect go1.27.3.
go -C test/EmbedIO.QuicPrototype/peer mod download
go -C test/EmbedIO.QuicPrototype/peer mod verify
go -C test/EmbedIO.QuicPrototype/peer build -mod=readonly -trimpath -o ../../../TestResults/webtransport-peer.exe .
& $python test/EmbedIO.QuicPrototype/verify_webtransport.py --dotnet (Get-Command dotnet).Source --assembly $assembly --openssl $opensslDirectory --peer TestResults/webtransport-peer.exe --output TestResults/quic-prototype-webtransport
```

## Next acceptance boundaries

1. Extend the bounded connection/stream machinery to sustained receive-credit
   replenishment, complete shutdown/draining, repeated and locally initiated key updates, and
   concurrent connections.
   Broaden loss/congestion campaigns before treating the recovery algorithm as
   production transport acceptance.
2. Exercise outbound partial reset, withheld credit, concurrent session termination
   and malformed authenticated traffic more extensively against independent peers.
3. Connect the validated transport to the production HTTP/3 provider abstraction,
   request dispatcher and application authorization APIs. Keep capability checks
   bound to the live connection before exposing a public WebTransport API.
4. Validate Windows, Linux and macOS using exact dependency artifacts, then browsers.
   Measure warm steady-state and tail latency separately from cold start before
   choosing any default provider.

The existing [WebTransport upstream readiness record](webtransport-upstream-readiness.md)
continues to describe the MsQuic route. This experiment explores a separate provider;
it does not establish that either route is ready to ship.
