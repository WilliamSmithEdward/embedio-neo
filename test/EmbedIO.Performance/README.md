# Performance checks

Run the allocation checks from the repository root:

```sh
dotnet restore test/EmbedIO.Performance/EmbedIO.Performance.csproj --locked-mode
dotnet run --project test/EmbedIO.Performance/EmbedIO.Performance.csproj -c Release --no-restore -- --verify-allocations
```

This dependency-free, unpackaged project is intentionally outside `EmbedIO.sln`.
Desktop CI runs it alongside the regression suite. Allocation ceilings catch
regressions; timings are informational and should be compared on the same machine.

See [the additional audit](../../docs/user-reports/performance-audit-second-pass.md) for negotiation, header, byte-order and history workloads. See [the audit report](../../docs/user-reports/performance-audit.md) for the baseline,
measurement method, coverage and limits. Omit `--verify-allocations` for baseline
measurements, using the same runner source against each revision.
