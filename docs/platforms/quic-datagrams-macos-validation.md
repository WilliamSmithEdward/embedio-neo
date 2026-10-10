# Native QUIC datagram validation on macOS

Local validation of native QUIC datagrams (PR
[#241](https://github.com/WilliamSmithEdward/embedio-neo/pull/241)) on Apple
Silicon, using the independent aioquic peer. The setup mirrors CI's step
"Install pinned macOS QUIC prerequisite" in `.github/workflows/ci.yml`.

Result: **pass, 3 of 3 runs** at head
`5f08f9902291f27263d437bd8087edc7311014b9`. Run on 2026-10-10.

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

## Build

`dotnet restore test/EmbedIO.Tests/EmbedIO.Tests.csproj --locked-mode`
succeeded. The Release build with `--no-restore` produced 0 warnings and
0 errors.

## Runs

Each run used `EMBEDIO_REQUIRE_QUIC=1`, `EMBEDIO_DATAGRAM_PEER_PYTHON` set to
the venv, and `--filter "FullyQualifiedName~MsQuicNativeDatagramTest"`, with its
own TRX results directory.

| Run | Total | Succeeded | Failed | Skipped | Echo outcomes |
| --- | --- | --- | --- | --- | --- |
| 1 | 35 | 35 | 0 | 0 | `echoed=7`, Acknowledged 5, Lost 3 |
| 2 | 35 | 35 | 0 | 0 | `echoed=7`, Acknowledged 4, Lost 4 |
| 3 | 35 | 35 | 0 | 0 | `echoed=7`, Acknowledged 3, Lost 5 |

The peer's lines were identical in every run.

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
