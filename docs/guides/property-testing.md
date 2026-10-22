# Generated URL, query, multipart and QUIC properties

`test/EmbedIO.Fuzz` includes a test-only FsCheck 3.4.0 mode. It generates strings
of up to 128 characters containing separators, malformed percent escapes,
controls, ordinary text and Unicode. Six existing properties check:

- URL normalization returns a valid path and is idempotent, for both ordinary
  paths and base paths.
- Mutable and immutable query parsing return the same ordered keys and value
  arrays, with flag grouping both enabled and disabled.
- Literal terminal route matching agrees with the legacy regex reference.
- Multipart file bodies round-trip byte for byte, and arbitrary raw multipart
  parsing has the same result under ordinary and fragmented transport reads.
  The shared AFL++/libFuzzer driver also supplies arbitrary binary input (bounded
  to 64 KiB per multipart iteration) and valid/malformed multipart corpus seeds.

Each failing input is shrunk by deleting progressively smaller chunks, down to
single characters. Candidates are always shorter, including the empty string,
so the shrinker terminates. FsCheck reports the original and reduced input,
property name and replay information, and a failure exits the process nonzero.

From the repository root, using the SDK pinned in `global.json`:

```sh
dotnet restore test/EmbedIO.Fuzz/EmbedIO.Fuzz.csproj --locked-mode
dotnet run --project test/EmbedIO.Fuzz -c Release --no-restore -- --fscheck 1729 10000
```

The first number is an unsigned 64-bit seed; the second is the number of cases
per property (1Ã¢â‚¬â€œ100,000). Defaults are seed 1729 and 10,000 cases. Repeat the
same command on the same source and dependency versions to reproduce a campaign.
The Fuzz workflow uses its run ID to choose a seed and retains the output in
the `http-transport-fuzz` artifact alongside the existing campaigns.

Replay the shared multipart corpus without a native fuzzer:

```sh
dotnet run --project test/EmbedIO.SharpFuzz -c Release -- --multipart-corpus test/EmbedIO.SharpFuzz/corpus
```

FsCheck and its locked FSharp.Core dependency belong only to the existing
nonpackable fuzz executable in the solution. They do not enter production
packages or change NUnit discovery. The existing mutation and protocol-oracle
campaigns remain enabled. These properties do not provide coverage-guided
protocol exploration, listener stress, or a complete fuzzing solution.

[FsCheck](https://fscheck.github.io/FsCheck/) supplies generation, replay and
shrinking. [Scorecard's C# detector](https://github.com/ossf/scorecard/blob/c395761df6afe1a69e476bc60a013a94bcbc153f/checks/raw/fuzzing.go#L167-L181)
recognizes FsCheck integration but did not recognize the custom mutation
harness. Recognition is separate from the properties' scope and effectiveness;
a score does not establish comprehensive parser coverage.

## EmbedIO.Quic

The QUIC engine's test facade participates in the existing fuzz campaigns. See the
[development and testing record](../project/quic-development-and-testing.md) for
all eight targets, multi-connection traces, managed coverage evidence, native TLS
mutation scope and extensive local commands. The ordinary HTTP/3 binding selects
EmbedIO.Quic; the earlier server providers are removed, and the BCL QUIC client
remains only as a test client. Fuzz configuration alone does not establish acceptance.
