# HTTP conformance and stateful campaigns

Test-only tooling for program #181. It drives a real `WebServer` through public
APIs over real sockets and compares what clients observe with the RFCs. Nothing here
ships in a package, and the project is not part of the ordinary solution build.

The audit, results and prioritized findings are in
[docs/project/http-conformance.md](../../docs/project/http-conformance.md).

## Components

| Path | Role |
| --- | --- |
| `ConformanceServer.cs` | Application surface: `/plain`, `/get-only`, `/echo` (any method, echoes body with SHA-256), `/query` (QUERY), `/stream` (unknown-length response), `/slow`, `/files/` (static files for ranges, validators and gzip), `/__stats` (GC, handles, threads and active-handler counters after a forced collection). |
| `RawHttp1.cs` | Independent HTTP/1.1 client and response parser written from RFC 9112 Section 6.3. Shares no code with EmbedIO. |
| `Http1Conformance.cs` | HTTP/1.1 and HTTP semantics requirement checks, each citing its RFC section and level. A fresh-connection health check follows every case. |
| `Http1Fuzz.cs` | Stateful HTTP/1.1 campaign: pipelined valid, invalid and aborted requests with random framing and fragmentation, checked against an independent model, plus resource settlement. |
| `drivers/h2_campaign.py` | HTTP/2 cases and stateful campaign. The client is hyper-h2; malformed and abusive input is written as raw frames with hyperframe. |
| `drivers/h3_campaign.py` | HTTP/3 cases and stateful campaign over aioquic, whose QPACK decoder also checks the server's dynamic encoding. |
| `drivers/quic_datagram_peer.py` | Independent RFC 9221 QUIC datagram peer over aioquic for the internal native MsQuic provider. Driven by `MsQuicNativeDatagramTest` when `EMBEDIO_DATAGRAM_PEER_PYTHON` names a Python with `drivers/requirements.txt` installed; skipped otherwise. |
| `standards/capture_standards.py` | Captures RFC Editor metadata (with update and obsolescence chains), errata, the RFC index sweep, IANA registries and datatracker state of tracked drafts, with URLs, times and SHA-256. |
| `ApplicabilityProbes.cs` | `applicability` mode: in-process minimal reproductions for the [standards applicability audit](../../docs/project/http-standards-applicability.md) (informational responses, trailers, Upgrade/CONNECT, QUERY, HTTP/2 SETTINGS). Uses the `/probe/*` routes. Outcome `gap` records a missing application capability the RFC permits; it does not fail a run. |
| `docker/Dockerfile` | Pinned runner: .NET SDK 10.0.401 image by digest, MsQuic 2.6.2 by SHA-256, h2spec v2.6.0 built from its tag commit, Python packages by hash. |
| `run-campaigns.sh` | Every campaign against one separate server process, with a summary tied to the source commit. |
| `repro.sh` | One focused driver run with engine logging, for reducing a failure. |

Outcomes are `conforms`, `violation` (a stated requirement is not met),
`policy` (the RFC permits a choice; the observed behavior is recorded) and `error`
(the driver failed). Only `violation` and `error` fail a run.

## Running

Quick local HTTP/1.1 pass on loopback (Windows or Linux):

```sh
dotnet run --project test/EmbedIO.Conformance -c Release -- self --out TestResults/http-conformance/local --seed 20261009 --iterations 300
```

Full campaign in the pinned container. Heavy runs belong here so host socket
pressure stays isolated. Capture the exact commit as a snapshot first; the archive
keeps LF endings through `.gitattributes`:

```sh
docker build -f test/EmbedIO.Conformance/docker/Dockerfile -t embedio-conformance test/EmbedIO.Conformance
git archive --format=tar -o TestResults/http-conformance/src.tar HEAD
docker run --rm --cpus=4 --memory=6g -e SOURCE_SHA=$(git rev-parse HEAD) -v "$PWD/TestResults/http-conformance:/runs" embedio-conformance \
  bash -c "mkdir /src && tar -xf /runs/src.tar -C /src && bash /src/test/EmbedIO.Conformance/run-campaigns.sh /src /runs/out 20261009 1"
```

The last argument scales fuzz iterations (1 takes about fifteen minutes on four CPUs, with a fresh server per phase).
`repro.sh` takes the same source and output arguments followed by a driver and its
arguments, for example `h3 cases --host localhost --port 18444 --only storm`.

Python is development-only. Rebuild `drivers/requirements.txt` with:

```sh
uv pip compile test/EmbedIO.Conformance/drivers/requirements.in --universal --generate-hashes --python-version 3.12 --exclude-newer <date a week ago> -o test/EmbedIO.Conformance/drivers/requirements.txt
```

To move h2spec, change the tag commit in the Dockerfile. To move the base image or
MsQuic, update the digest or package hash in the Dockerfile, as for CI.

## Repaired findings are campaign failures

The current engine has corrections for body-framing 500s (F2), HTTP/2
connection loss after a reset (F4), and HTTP/3 listener loss on upload
cancellation (F3). These are no longer campaign allowances: malformed-body
500s and HTTP/2 connection loss fail the run, and HTTP/3 upload cancellations
are included. Reproduction scripts propagate the driver exit status.

## Windows sender pacing and current-engine check

Fragmented requests use an elapsed-time schedule. A one-millisecond sleep can
consume a full Windows scheduler tick; accumulating one sleep per fragment made
the test sender exceed its own unchanged five-second response timeout. The first
current-engine run stopped at seed 20261009, iteration 1230, with 392 delayed
fragments. A separate 400-sleep diagnostic took 6.17 seconds on that host.

After correcting sender pacing, the same 2,000-iteration seed passed against
engine 401983a on Windows: 5,029 valid requests, 473 malformed sequences and 211
aborts; no body-framing 500s, no active handlers at completion, handle growth 7
and retained managed growth about 2.24 MiB. All 57 HTTP/1 cases passed (44
conforming and 13 permitted policy choices). These results do not cover the
HTTP/2, HTTP/3 or sustained-load campaigns, or the legacy range-end policy.
The original timeout record remains separate from the passing campaign evidence.
## SETTINGS acknowledgement model

The pinned hyper-h2 4.4.1 settings object acknowledges pending values per key,
which can apply values from a later SETTINGS frame when an earlier frame's ACK
arrives. With initial SETTINGS, MAX_FRAME_SIZE, zero INITIAL_WINDOW_SIZE and
restoration all outstanding, an isolated peer-only probe applied zero on the
first ACK. The original current-engine campaign retained a FlowControlError at
seed 20261009, iteration 58; its trace shows the DATA before the zero-window ACK.

The driver now queues settings by frame and stages only the acknowledged frame's
values through hyper-h2's normal validation and flow-control update. Every ACK
and DATA frame is consumed in wire order, including fragmented ACKs. Overlapping
SETTINGS remain in the campaign. `drivers/settings_ack_test.py` verifies legal
DATA before the shrink ACK, a negative adjusted window after that ACK, restoration,
and rejection of an actual DATA overrun. It requires no EmbedIO server and runs
before the full campaign. This does not waive any server flow-control assertion;
the original failure remains recorded and the corrected seed must be rerun.
A second retained run reached iteration 76: DATA arrived before the shrink ACK,
then a zero-byte END_STREAM arrived with the adjusted stream window negative.
RFC 9113 section 6.9.1 permits that empty EOF, but hyper-h2 rejected it. For that
exact unpadded zero-byte EOF only, the driver returns the consumed stream credit
through the library's public WINDOW_UPDATE API before processing EOF. The stream
then closes, so later payload cannot consume the returned credit. Nonzero DATA,
padded DATA, invalid stream state and empty frames without END_STREAM retain the
ordinary checks. The peer-only regression also verifies this EOF and preserves a
nonzero overrun that must raise FlowControlError.

Reference: https://www.rfc-editor.org/rfc/rfc9113.html#section-6.9.1
The next retained run timed out at iteration 178. Its completion phase restored
INITIAL_WINDOW_SIZE to 65,535 even after advertising 1 MiB, which could shrink
an active stream to zero after 65,535 consumed bytes. No new DATA arrived to drive
the peer library's automatic replenishment. The completion phase now explicitly
opens a 1 MiB stream window (larger than every generated response) while preserving
all randomized window changes before completion. A peer-only wire regression
reproduces the zero window and verifies that explicit reopening permits the final
payload. The 30-second completion deadline and byte validation remain unchanged.