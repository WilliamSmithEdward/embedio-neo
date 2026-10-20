# Independent QUIC engine feasibility

This is an isolated development experiment in `test/EmbedIO.QuicPrototype`, outside
`EmbedIO.sln` and every shipped package. It establishes that original C# QUIC packet
and stream handling can use an existing TLS provider and communicate with an
independent implementation. It adds no public listener mode or application API.
HTTP/3 continues to use the existing provider. WebTransport remains unavailable.

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
- [RFC 9002](https://www.rfc-editor.org/rfc/rfc9002.html), required future loss
  recovery and congestion control; these are not implemented here.

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

The executable always binds IPv4 loopback, with an ephemeral port. Each process
accepts one connection, one client-initiated bidirectional stream (ID 0), up to
8192 stream bytes and a ten-second overall deadline. It echoes the complete stream,
including an empty stream, after TLS authentication finishes. The peer authenticates
the server through a temporary trusted localhost certificate and hostname checking.
The application protocol is `embedio-quic-probe`, not HTTP/3.

CRYPTO reassembly has a 64 KiB bound at each encryption level. Packet tracking and
outbound packet numbers have a 4096-packet bound per number space, the outbound
queue has a 128-packet bound, and pre-validation sends obey the three-times-received
byte allowance. Retransmitted data is checked for matching overlapping bytes and
consistent final sizes. Certificate keys are generated into the caller's ignored
results directory, never into source or a package.

This profile is deliberately incomplete. It has no general retransmission/PTO,
congestion control, pacing, Retry, migration, key updates, resumption, 0-RTT,
multiple connections/streams, QUIC datagrams or reliable reset. Its limited ACK and
transport-parameter processing must be extended before general use. It requires
an orderly loopback exchange; successful echo is not QUIC conformance acceptance.
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

Windows x64, .NET SDK 10.0.402 and OpenSSL 3.5.7 completed ten independent aioquic
1.6.0 cases: exact echo at 0, 1, 128, 4096 and 8192 bytes; rejection of an untrusted
certificate, wrong hostname and wrong ALPN; malformed packets before a valid
connection; and a relay injecting truncation, ciphertext corruption and a duplicate
into an established connection. TLS errors retain the corresponding QUIC
CRYPTO_ERROR code. The peer still receives the exact response after relay injection.

The executable's deterministic checks perform 10,076 assertions, including the
RFC 9001 key/IV/header-protection vectors, modified-packet rejection, reassembly
bounds/overlap/final-size checks, varint boundaries and 10,000 seeded random varint
inputs. These are primitive checks, not 10,076 connection or browser tests.

No Linux/macOS wire validation, browser validation, latency comparison or production
acceptance is claimed. This experiment has no automatic CI wire job. Source-quality
checks and project-specific formatter/analyzer checks apply in addition to the
commands above.

## Next acceptance boundaries

1. Specify connection, stream, timer and memory ownership and implement a deterministic
   transport core with injected time and packet delivery. Add complete RFC 9000/9002
   state transitions, ACK/loss recovery, congestion control and address validation.
2. Prove independent interoperability under packet loss, reordering, blocked credit,
   malformed authenticated input, key updates, cancellation and concurrent sessions.
3. Add QUIC DATAGRAM and the current RESET_STREAM_AT extension, verifying final-size
   accounting and reliable-prefix delivery. Then connect the existing HTTP/3 and
   WebTransport session layers through the transport abstraction.
4. Validate Windows, Linux and macOS using exact dependency artifacts, then browser
   behavior, application authorization and session limits. Measure warm steady-state
   and tail latency separately from cold start before choosing any default provider.

The existing [WebTransport upstream readiness record](webtransport-upstream-readiness.md)
continues to describe the MsQuic route. This experiment explores a separate provider;
it does not establish that either route is ready to ship.
