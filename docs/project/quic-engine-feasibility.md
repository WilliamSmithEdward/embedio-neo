# EmbedIO.Quic implementation

The EmbedIO.Quic development and validation host is in `test/EmbedIO.Quic`. It is
referenced only by test harnesses and excluded from shipped packages. Its C# packet
and stream handling can use an existing TLS provider and communicate with an
independent implementation. It adds no public listener mode or application API.
The production HTTP/3 listener continues to use its existing provider. Public
WebTransport APIs remain unavailable. The engine includes loss recovery,
NewReno congestion control and an HTTP/3/WebTransport adapter.

The intended production end state replaces MsQuic entirely, including the native
provider and indirect use through `System.Net.Quic`. That transition remains gated
on complete transport/application integration, interoperability, platform and
performance validation. The existing production provider is still in place.

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
  The runtime is deliberately pinned to OpenSSL 3.5.7 for this development host.
- [RFC 9002](https://www.rfc-editor.org/rfc/rfc9002.html), ACK ranges, RTT sampling,
  packet/time loss thresholds, PTO, NewReno, persistent congestion and pacing.
- [WebTransport HTTP/3 draft 16](https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-16)
  and [reliable stream reset draft 11](https://datatracker.ietf.org/doc/html/draft-ietf-quic-reliable-stream-reset-11).
  The adapter uses current parameter `0x1d` and frame `0x24`.

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
uses a ten-second process deadline by default. The single-connection modes are:

- `serve` keeps the original `embedio-quic-probe` ALPN, one bidirectional stream
  and 8192-byte exact echo.
- `serve-h3` negotiates `h3`, sends HTTP/3 SETTINGS, decodes bounded QPACK headers
  with dynamic capacity zero, and accepts `CONNECT :protocol=webtransport-h3` at
  `https://localhost/echo`. It links the existing repository codecs and session
  core into this test executable; production files are not copied or modified.
- `serve-h3-updates` uses the same H3 profile with frequent local key updates and
  a separate bounded endurance deadline, described below.

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

Bounds remain deliberate: 64 KiB CRYPTO reassembly per encryption level,
4096 active recovery records, bounded ACK/loss history
and 512 queued packets. H3 permits 16 incoming streams of each direction, 64 KiB
per stream receive window and an initial 256 KiB connection window. Both windows
advance as the application consumes or discards input; buffered gaps retain their
credit. Final reset sizes consume and release connection credit once, including
gaps, after any required reliable prefix arrives. The engine waits for the reliable prefix before reporting a
partial reset and preserves the header when a local reset waits for send credit.
Stream reassembly reuses a fixed ring, and the H3 parser compacts consumed input.
Retired duplicate bytes are ignored; overlap still buffered in the ring must agree,
and final-size history is retained. The active-record/session/stream-count budgets and
process deadline still bound each validation process; this is not indefinite service.
Receive packet history now uses at most 128 ACK ranges within a sliding
4096-number window. A monotonic acceptance floor prevents processing retired
duplicates again. Large jumps and sparse ranges retire old history without ending
the connection. This removes the cumulative receive-packet limit; outbound packet
numbers can advance to the QUIC 62-bit limit; active recovery records remain
bounded independently of cumulative traffic. Four-byte packet numbers carry the
low bits and reconstruct across 32-bit boundaries.
Pre-validation sends obey the three-times-received byte allowance.

Lost-packet metadata retains the most recent 4096 retirement entries, while
current in-flight records remain independently owned. Very late ACKs for evicted
records can be ignored; a current retransmission still requires its own ACK.
Acknowledged-send timestamps compact from 12,288 to 8192 entries at a known
acknowledged-packet barrier. Loss history before that barrier retires with them,
so compaction does not join persistent-loss intervals across a forgotten ACK.
These are bounded histories, rather than cumulative connection quotas.

Key updates derive fresh packet secrets while preserving header protection. Read
and write generations are tracked separately through phase-bit wraparound; a local
update can advance the writer once before the peer responds. Current-generation
ACK confirmation and a three-PTO wait gate subsequent local updates. The default
local interval is 1,048,576 encryptions; per-key AES-GCM protection stops at the
RFC 9001 limit of 2^23, reserving capacity for AEAD_LIMIT_REACHED closure if an
update cannot proceed. Packet encryption occurs after pacing/amplification checks.
Previous receive keys are retained for reordering during three PTO durations.
Next receive keys are prepared after TLS setup and authenticated transitions;
unauthenticated phase changes do not trigger derivation. Header protection is
removed once before selecting the current, previous or next payload key. The
larger peer cases require a recorded key update and complete exact-byte transfer.
The per-connection failed-authentication counter stops packet processing at the
AES-GCM integrity limit of 2^52 failures, with AEAD_LIMIT_REACHED closure. Native
regressions use smaller test-only budgets to verify both limit paths on the wire.

Connections now distinguish active, closing, draining and closed states. Local
protocol/TLS errors send bounded CONNECTION_CLOSE responses; peer close enters
silent draining for three PTO durations. Negotiated idle expiry is silent, and
repeated local sends cannot keep an unresponsive peer alive. Authenticated traffic
can trigger a rate-limited close retry; closing/draining deadlines are not extended.
The Windows harness tolerates ICMP connection-reset notifications while finishing
those states. The hard process deadline remains independent.

The shared `serve-many` / `serve-many-h3` host routes by destination connection ID,
checks the existing peer endpoint, admits at most 16 resident owners and 16 new
attempts per one-second window, and retires incomplete handshakes after three
seconds. Those host limits are independent of each established connection's idle
state. Cancellation owns the pending receive/timer operations and every connection;
retired IDs and endpoint aliases are removed. Migration remains unsupported.

The current transport profile has no Retry, migration, resumption, 0-RTT,
complete post-handshake TLS processing,
complete transport-error classification, stateless reset
or production provider interface. It supports a bounded H3 echo profile,
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
python -m venv TestResults/embedio-quic-python
$python = 'TestResults/embedio-quic-python/Scripts/python.exe'
& $python -m pip install --require-hashes --only-binary=:all: -r test/EmbedIO.Conformance/drivers/requirements.txt
dotnet restore test/EmbedIO.Quic/EmbedIO.Quic.csproj --locked-mode
dotnet build test/EmbedIO.Quic/EmbedIO.Quic.csproj -c Release --no-restore
$assembly = 'test/EmbedIO.Quic/bin/Release/net10.0/EmbedIO.Quic.dll'
dotnet $assembly self-test
& $python test/EmbedIO.Quic/verify.py --dotnet (Get-Command dotnet).Source --assembly $assembly --openssl $opensslDirectory --output TestResults/embedio-quic-wire
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
- Twenty-one quic-go 0.63.0 cases cover H3 settings and CONNECT, bidi/uni stream echo,
  datagrams, two sessions and quota rejection, authorization/Origin rejection,
  missing current-reset negotiation, incoming and outgoing partial resets,
  a malformed capsule followed by a healthy sibling, larger transfers, withheld
  transport credit, peer key updates and handshake/response loss. Three cases
  transfer 512 KiB per session beyond the initial stream and connection windows,
  including small peer windows and packet loss. Three cases verify transport/application
  closing and silent idle expiry; every wire case requires completed shutdown.
  Two endurance cases transfer 8 MiB over bidi/uni streams, require more than
  4096 sent packets and multiple local and peer key transitions; the loss case
  also requires actual retransmission.
- The original 10,076 primitive assertions remain. Additional deterministic
  recovery and bridge regressions cover packet spaces, RTT/ACK delay, PTO backoff,
  late/duplicate ACKs, persistent congestion, blocked credit, sparse reliable
  prefixes, final-size conflicts, session admission, receive-window reuse and
  closing/draining timers. A seeded recovery campaign
  checks 10,000 state transitions plus complete retirement of each run.

CI builds the standalone project and runs the managed checks without loading
OpenSSL or adding another job. The three independent wire harnesses remain explicit
local checks. No Linux/macOS wire validation, browser interoperability, latency
comparison or production acceptance is claimed.

### Current-wire peer

Use Go 1.27.3 for the independent peer. Its source dependencies are pinned in
`test/EmbedIO.Quic/peer/go.mod` and authenticated by `go.sum`. The Windows
amd64 toolchain archive is
[`go1.27.3.windows-amd64.zip`](https://go.dev/dl/go1.27.3.windows-amd64.zip), SHA-256
`f26081f438319757f8a1b46d7d0b2c5ec7583d4038e485e0aa725cfde7885fff`.
The local validation used that exact archive. Go is BSD-3-Clause licensed;
quic-go and qpack are MIT licensed. They are development-only and are not added to
any NuGet dependency group or package payload.

```powershell
$env:GOTOOLCHAIN = 'local'
go version # Expect go1.27.3.
go -C test/EmbedIO.Quic/peer mod download
go -C test/EmbedIO.Quic/peer mod verify
go -C test/EmbedIO.Quic/peer build -mod=readonly -trimpath -o ../../../TestResults/webtransport-peer.exe .
& $python test/EmbedIO.Quic/verify_webtransport.py --dotnet (Get-Command dotnet).Source --assembly $assembly --openssl $opensslDirectory --peer TestResults/webtransport-peer.exe --output TestResults/embedio-quic-webtransport
```

## Development and fuzzing

The [development and testing record](quic-development-and-testing.md) contains
the complete implementation/test map, replay commands, extended local campaigns,
validation ledger and MsQuic retirement checklist. All existing fuzz engines now
include direct QUIC core targets; native-context mutations complement managed
coverage. The production provider remains unchanged.

## Next acceptance boundaries

1. Extend the bounded shared-listener/stream machinery to production lifetimes,
   replenished stream counts, complete error classification/stateless reset,
   and longer transfer/loss/endurance coverage.
   Broaden loss/congestion and close-retry campaigns before treating the recovery
   and lifecycle algorithms as production transport acceptance.
2. Exercise outbound partial reset, withheld credit, concurrent session termination
   and malformed authenticated traffic more extensively against independent peers.
3. Connect the validated transport to the production HTTP/3 provider abstraction,
   request dispatcher and application authorization APIs. Keep capability checks
   bound to the live connection before exposing a public WebTransport API.
4. Validate Windows, Linux and macOS using exact dependency artifacts, then browsers.
   Measure warm steady-state and tail latency separately from cold start before
   choosing any default provider.

The existing [WebTransport upstream readiness record](webtransport-upstream-readiness.md)
continues to describe the MsQuic route. EmbedIO.Quic implements a separate provider;
it does not establish that either route is ready to ship.

## Shared listener validation

After building the pinned peer above, run the additional ownership cases:

```powershell
& $python test/EmbedIO.Quic/verify_connections.py --dotnet (Get-Command dotnet).Source --assembly $assembly --openssl $opensslDirectory --peer TestResults/webtransport-peer.exe --output TestResults/embedio-quic-connections
```

The four cases cover eight simultaneous aioquic clients with loss/malformed input,
twelve clients in waves after rebinding the same UDP port, four simultaneous
quic-go WebTransport clients, and cancellation while an established connection is
still active. Every case checks that the shared host retains zero owners at exit.
The native mutation entry point also exercises admission, endpoint/CID isolation,
capacity, handshake deadlines and once-only disposal without sockets.

The `serve-h3-updates` development command uses a 128-packet local update interval
and a bounded 30-second process lifetime for the 8 MiB endurance case. Its peer
has a separate 20-second operation deadline. Ordinary hosts and existing peer cases
retain their ten-second and eight-second bounds. This case verifies exact bidi/uni
echo bytes, packet count beyond 4096, repeated updates, post-transfer datagrams,
complete session shutdown and zero remaining physical flight.
