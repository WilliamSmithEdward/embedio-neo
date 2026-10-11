# Native QUIC datagram validation on macOS

Local Apple Silicon validation of the internal native QUIC datagram owner
(`MsQuicNativeDatagrams`), using the independent aioquic peer. The setup
mirrors CI's step "Install pinned macOS QUIC prerequisite" in
`.github/workflows/ci.yml`.

Each result below applies only to the exact source commit it names. A later
source change, including a correction, needs its own run. Editing this page
does not change the tested bytes.

| Source commit | Scope | Result |
| --- | --- | --- |
| `5f08f9902291f27263d437bd8087edc7311014b9` (PR [#241](https://github.com/WilliamSmithEdward/embedio-neo/pull/241)) | Focused datagram tests, 3 runs | 35 of 35 passed in each run |
| `7b8aee59bb8caf817ce25e16b1d03b4df0c5a800` (PR [#243](https://github.com/WilliamSmithEdward/embedio-neo/pull/243), includes the send-preparation/disposal correction `f13c113`) | Focused datagram tests, 3 runs | 36 of 36 passed in each run |
| `7b8aee59bb8caf817ce25e16b1d03b4df0c5a800` | Full suite, 1 run | 4939 total: 4907 passed, 31 platform skips, 1 failure in an unrelated shutdown fixture (see below) |

The `5f08f99` runs predate the correction and do not validate it.

This is local evidence, not a hosted CI result. It does not cover Intel Macs,
other macOS versions, the .NET Standard 2.0 asset, HTTP/3 datagram association
or WebTransport, none of which this provider integrates yet.

## Environment

| Item | Value |
| --- | --- |
| OS | macOS 27.0.1 (26A434), arm64 |
| CPU | Apple M5 Pro, 18 cores |
| .NET SDK | 10.0.401 (local install via `dotnet-install.sh --channel 10.0`) |
| Runtime | Microsoft.NETCore.App 10.0.12 |
| Python | 3.14.6 (venv, test tooling only) |
| aioquic | 1.3.0 (`--require-hashes --only-binary=:all:`) |
| OpenSSL | openssl@3 3.6.5 (Homebrew) |
| MsQuic bottle | blob `sha256:c7d7fbdaec216ced0ed5b9b7b58d3d6b251e96dc4d666409e3fb211cf8c3bf55`, verified |
| `libmsquic.2.6.2.dylib` after relocation and ad-hoc signing | `662c2a1256872253fef76f4e76582410e794807ac0e3787ccee7976870543bcd` |

`otool -L` shows libcrypto resolved to
`/opt/homebrew/opt/openssl@3/lib/libcrypto.3.dylib`.
`DYLD_FALLBACK_LIBRARY_PATH` pointed at the extracted bottle's `lib` folder.
The same environment and library were used for both commits.

## Method

For each commit: locked restore, then a Release build with `--no-restore`
(0 warnings, 0 errors). Each focused run used `EMBEDIO_REQUIRE_QUIC=1`,
`EMBEDIO_DATAGRAM_PEER_PYTHON` set to the venv, and
`--filter "FullyQualifiedName~MsQuicNativeDatagramTest"`, with its own TRX
results directory. The full-suite run used the same environment variables,
`--timeout 5m` and `--minimum-expected-tests 4939`, the floor recorded at that
commit.

## Results at `5f08f99`

Run on 2026-10-10. The locked restore covered `test/EmbedIO.Tests/EmbedIO.Tests.csproj`.

| Run | Total | Succeeded | Failed | Skipped | Echo outcomes |
| --- | --- | --- | --- | --- | --- |
| 1 | 35 | 35 | 0 | 0 | `echoed=7`, Acknowledged 5, Lost 3 |
| 2 | 35 | 35 | 0 | 0 | `echoed=7`, Acknowledged 4, Lost 4 |
| 3 | 35 | 35 | 0 | 0 | `echoed=7`, Acknowledged 3, Lost 5 |

## Results at `7b8aee5`

Run on 2026-10-10. The locked restore covered `EmbedIO.sln`. The tested
`net10.0` `EmbedIO.dll` SHA-256 was
`b03bd529adacbfc3c27fe1f7d77fb344742eb22cd0ff350605a8b4e795aafc62`.

| Run | Total | Succeeded | Failed | Skipped | Echo outcomes |
| --- | --- | --- | --- | --- | --- |
| 1 | 36 | 36 | 0 | 0 | `echoed=7`, Acknowledged 4, Lost 4 |
| 2 | 36 | 36 | 0 | 0 | `echoed=7`, Acknowledged 5, Lost 3 |
| 3 | 36 | 36 | 0 | 0 | `echoed=7`, Acknowledged 5, Lost 3 |

The 36th case is the connection-lifetime regression added with the correction.

The full-suite run (2m 09s) had one failure:
`Issue595_TwoServers.ClosingDuringInFlightRequestsCompletesAcceptLoop(True)`
with `SocketException: Invalid argument` from `HttpClient` reading a reset
socket's peer address. That test file is unchanged by the datagram work. The
same failure reproduces on the unmodified development branch at `a4f7105`
(6 of 40 focused invocations). PR
[#247](https://github.com/WilliamSmithEdward/embedio-neo/pull/247) proposes the
fixture correction. All 31 skips are existing platform skips; no QUIC or
datagram case was skipped.

## Peer output

The peer's lines were identical in every run at both commits.

Echo case:

```json
{"event": "connected", "remote_max_datagram_frame_size": 65535}
{"event": "greeting", "length": 29}
{"count": 7, "event": "echo-complete", "retransmissions": 0}
{"event": "done"}
```

Unsupported case:

```json
{"event": "connected", "remote_max_datagram_frame_size": 65535}
{"event": "no-datagrams"}
{"event": "done"}
```

The `Lost` outcomes are expected: MsQuic reports `LOST_DISCARDED` for datagrams
still unacknowledged when the peer closes. The test asserts at least one
`Acknowledged`, and every run had three or more.

## Notes

- Python 3.14.6 was used. The hash-locked requirements were compiled for 3.12
  and later, and the wheels installed cleanly.
- No source, test, workflow or dependency files were changed for the
  validation.
