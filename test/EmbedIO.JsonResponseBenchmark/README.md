# HTTP JSON response component comparison

This non-packable .NET 10 tool measures the in-memory `TestWebServer` pipeline.
`legacy` reconstructs the previous HTTP JSON callback through public APIs:
serialize to a string, then send through `ResponseSerializer.None(false)`.
`current` invokes the current `ResponseSerializer.Json`. Both use the same core,
options and input. Every response must equal the expected UTF-8 bytes.

This isolates representation and output work. It does **not** measure socket
throughput, TCP behavior, HTTP/2 or HTTP/3 performance, or establish a correction
for an intermittent network timeout. In-memory stream growth and response copies
are included in allocation; those costs differ from a production transport.

Build with the repository SDK and lock files:

```sh
dotnet restore test/EmbedIO.JsonResponseBenchmark/EmbedIO.JsonResponseBenchmark.csproj --locked-mode
dotnet build test/EmbedIO.JsonResponseBenchmark/EmbedIO.JsonResponseBenchmark.csproj -c Release --no-restore
```

Each invocation starts a fresh process, warms ten requests, collects the heap,
then measures the requested count. Output records total allocation, process CPU
and elapsed time. Run three alternating rounds; keep each result separately and
retain failures without retries. Coordinate with the shared benchmark lock.

```sh
dotnet test/EmbedIO.JsonResponseBenchmark/bin/Release/net10.0/EmbedIO.JsonResponseBenchmark.dll legacy 1024 20000
dotnet test/EmbedIO.JsonResponseBenchmark/bin/Release/net10.0/EmbedIO.JsonResponseBenchmark.dll current 1024 20000
```

Also compare `65536 1000` and `1048576 100`, reversing mode order in the second
round. For CPU-focused measurements increase iterations: the recorded windows
were short and Windows process CPU time advanced in roughly 15.6 ms steps.

## Recorded component evidence, 2026-10-10

Windows x64, SDK 10.0.401 / runtime 10.0.12. Three alternating rounds, eighteen
fresh processes, every response byte validated, zero failed samples. The initial
five-request smoke samples are excluded. Median allocation per request:

| Input string | Legacy | Current | Reduction |
| --- | ---: | ---: | ---: |
| 1 KiB | 19,424.5 B | 11,008.4 B | 43.3% |
| 64 KiB | 535,886.4 B | 269,064.3 B | 49.8% |
| 1 MiB | 8,401,737.5 B | 4,203,032.0 B | 50.0% |

CPU moved in the same direction, but the short windows and clock quantization
limit precise CPU conclusions. No production/network speedup is claimed.

Measured production source: `1409cc2`; later documentation/floor commit
`5f70953` does not change that source. Core SHA-256:
`09153828D437BAE6563CB0827FFA993CD752147BC590450D0D7734F195E2D28E`.
The originally measured standalone harness DLL SHA-256 is
`B364AC2179C61407BBADB105E16B5439E00523E6FCDAAB56E71A1A1E377E4F8C`;
its formatted source SHA-256 is
`FB679119BDAFB5336A27ED967CC7AA2C8CC72FD20F43E782FDF417096D7F34B8`.
The project committed here uses that same program; its renamed assembly and
checkout-specific build paths produce a different harness DLL hash.

Raw samples and hashes are retained under ignored
`TestResults/json-response-component/samples`. The older callback is reconstructed
on the candidate core, not loaded from a baseline package or Git checkout.
