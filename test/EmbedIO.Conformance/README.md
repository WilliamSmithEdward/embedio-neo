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
| `standards/capture_standards.py` | Captures RFC Editor metadata (with update and obsolescence chains), errata, the RFC index sweep and IANA registries, with URLs, times and SHA-256. |
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

## Known findings while campaigns run

Campaigns keep exploring past known defects instead of stopping at the first one.
The HTTP/1.1 fuzz counts body-framing 500s (F2). The HTTP/2 fuzz counts silent
closures after a client reset (F4). The HTTP/3 fuzz skips upload cancellation with
`--avoid-known` (F3) and runs before the HTTP/3 cases, which can stop the listener.
Remove each allowance with its fix.
