# DI adapter YARA match review

The [malware run for PR #36](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37274269787)
failed because `GODMODERULES_IDDQD_God_Mode_Rule` matched both optional DI adapter
assemblies. ClamAV found zero matches. This review supports the two exact
rule/path entries in `malware-accepted.toml`; it does not disable the scanner.

## Rule and tool inspected

- YARA-X 1.20.0, using the archive pinned in `.github/security/yara.json`.
- YARA Forge full rules, release 20261004, archive SHA-256
  `809fe0c2e6c58dd9dc74afad0cdae21a1ed3bd5e68e539504d211dbf4d872f8d`.
- Florian Roth's [God Mode rule](https://github.com/Neo23x0/god-mode-rules/blob/436dc682164cf17a123d6b09d1424e7e2acf0c25/godmode.yar#L24-L69),
  identified in the pinned rules by ID `cb16ab74-1452-5898-b819-4346fea28c69`.
  Its condition is `1 of them`; one filename pattern is
  `/(Dropper|Bypass|Injection|Potato)\.pdb/ nocase`.

The tool and rule archives were checked against their committed SHA-256 pins.
The rule was read in full, then the pinned scanner was run with
`--print-strings` against the assemblies downloaded from CI's `scanned-nupkg`
artifact. The isolated scanner container had no network access and a read-only
mount of the evidence directory.

## Exact observed matches

| Target | Offset | Match | Assembly SHA-256 |
| --- | --- | --- | --- |
| net10.0 | `0x5350` | `Injection.pdb` | `798bf1b7248a5774f716d63917d80f2daf37bd3ac60b8f510c9e4fe117de1da2` |
| netstandard2.0 | `0x558f` | `Injection.pdb` | `8b5925ebfde47f4be7c1b9858f0d0032f32360824a24f33f8faa4803408a8d30` |

`System.Reflection.PortableExecutable.PEReader` confirmed these strings occur
in CodeView debug metadata. The paths are:

```text
/home/runner/work/embedio-neo/embedio-neo/src/EmbedIO.DependencyInjection/obj/Release/net10.0/EmbedIO.DependencyInjection.pdb
/home/runner/work/embedio-neo/embedio-neo/src/EmbedIO.DependencyInjection/obj/Release/netstandard2.0/EmbedIO.DependencyInjection.pdb
```

The scanner printed exactly one string match per assembly. No other strings
in this rule matched. These are the compiler's debug-symbol filenames, derived
from the adapter's assembly name, rather than evidence of process injection.
The adapter source implements managed service activation, HTTP request scopes,
resource cleanup and host lifecycle; it does not implement process injection.

These hashes and offsets describe the reviewed CI artifact, whose report
identifies PR merge commit `93e1a274ecc95178b7003ca933a0f1658f4c17a7`
(head `1e53ac02cf7ed56ab7d8e59994cc0da4bd183062`). Later builds can have different
hashes or offsets. Raw downloads, extracted metadata and scanner output are
retained locally under ignored `TestResults/di-implementation/yara-evidence`.

## Acceptance boundary

Accept this rule only for the two `EmbedIO.DependencyInjection.dll` paths in
the 1.0.0 package. Other rules, other files, scan errors, missing scans and
accepted paths that cease matching remain failures. A new package version or
changed rule requires review of the acceptance paths and evidence. Preserve
full scanning of source files, packages and unpacked assemblies.
