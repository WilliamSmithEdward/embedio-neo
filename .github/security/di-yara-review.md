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
the 1.0.2 package and the two review files named in the acceptance list.
The review Markdown and acceptance TOML repeat the inspected filename as
evidence; the pinned scanner confirmed that these text references also match.
A full local YARA scan found only these four expected paths across 500 files.
Other rules, other files, scan errors, missing scans and
accepted paths that cease matching remain failures. A new package version or
changed rule requires review of the acceptance paths and evidence. Preserve
full scanning of source files, packages and unpacked assemblies.

## 1.0.1 release revalidation

The [release PR scan](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37284977164)
reported the same four matches and no scanner errors; ClamAV found zero matches.
The assembly paths changed with the package version. Both scanner/rule archive
hashes were verified against `yara.json`, the rule was reread, and the full pinned
rules were rerun with printed matching strings in a network-disabled container
against the downloaded `scanned-nupkg` artifact.

| Target | Offset | Match | Assembly SHA-256 |
| --- | --- | --- | --- |
| net10.0 | `0x5350` | `Injection.pdb` | `c97db9492f3f48aadfb848c09dab0adf7c8bba8088f6cf86f659130a64293dbc` |
| netstandard2.0 | `0x558f` | `Injection.pdb` | `bc17229a4bd527b9be4d9d8f399c666f18cbf49a41beab999cc1fbfbf528c80c` |

PEReader again confirmed the matching bytes belong to the same CodeView paths
listed above. No other strings or rules matched the two assemblies. This review
covers PR merge commit `aaacbb5e1379844edc144529ea8a7d27155529ad` and preserves
the exact rule/path acceptance boundary. Only the two package-version paths
change; other rules, paths, errors and stale acceptances continue to fail.
Raw scanner output and extracted metadata are retained under ignored
`TestResults/release-1.0.1/TestResults/release-validation`.
## 1.0.2 release revalidation

The 1.0.2 local Release build was checked with YARA-X 1.20.0 and the full
YARA Forge 20261004 rules. Both archives were verified against `yara.json`.
The rule's filename pattern and `1 of them` condition were reread. The scanner
ran with `--print-strings` in a network-disabled container with read-only mounts.

| Target | Offset | Match | Assembly SHA-256 |
| --- | --- | --- | --- |
| net10.0 | `0x5356` | `Injection.pdb` | `de185d9492ee132ce630c73ccbcdfce05735701f2193f41e2273ac84cdf1ed12` |
| netstandard2.0 | `0x5595` | `Injection.pdb` | `ede2de3f13697f74585cf5180da591f37f204bb082454ca4c3e860d01484832c` |

PEReader confirmed that both matches belong to CodeView debug paths ending
in the adapter's unchanged debug-symbol filename. No other rules or strings
matched these assemblies. The hashes describe the local Windows packages at
source commit `391526b`; CI builds have different paths and hashes and must
pass the full required scans before merge and publication. Raw scanner output
and metadata are retained under ignored
`TestResults/release-1.0.2/TestResults/release-validation`.

Update only the two exact assembly paths from version 1.0.1 to 1.0.2.
The rule, target frameworks and evidence-file boundaries remain unchanged;
other matches, missing scans, scanner errors and stale acceptances still fail.

## 1.0.3 release revalidation

The release PR malware run [37588460548](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37588460548)
reported the same symbol-filename matches under the new 1.0.3 package paths,
and rejected the stale 1.0.2 entries. The exact scanned CI packages were
downloaded; both pinned archives and the scanner/bundle extracted bytes were
verified against the committed pins. The complete rule was reread, and the
full pinned scanner ran with `--print-strings` against both assemblies.

| Target | Offset | Match | Assembly SHA-256 |
| --- | --- | --- | --- |
| net10.0 | `0x5350` | `Injection.pdb` | `1bcd31636104026299bef3b9cb0c087bce1b58efb8d9fa734f72d2e6eb4b277a` |
| netstandard2.0 | `0x558f` | `Injection.pdb` | `5b4f5ab548206f5cf71eed5279eb03b66874a4cf86c6ac86aebde977946db2b2` |

PEReader confirmed that both offsets are inside the unchanged compiler CodeView
paths listed above. No other strings or rules matched these assemblies;
ClamAV reported zero findings. This evidence describes PR merge source
`32be19ead336e5fd13b0b092d9efd98f175f49db`. Later CI commits can change hashes;
full required scans still apply before merging and publishing.

Only the two exact package-version paths change from 1.0.2 to 1.0.3. The
rule, target frameworks and evidence-file scope are unchanged. Other findings,
errors and stale entries still fail. Raw packages, pin checks, scanner output
and PE metadata are retained under ignored `TestResults/release-1.0.3`.

## 2.0.0 local candidate revalidation

The local five-package 2.0.0 candidate was scanned with YARA-X 1.21.0 and
YARA Forge 20261011. The pinned scanner archive SHA-256 is
`01585181e8979e36ac10ab123fcf8921cf8ba879bf942916413e091e18d64852`;
the rules archive SHA-256 is
`ae03df5577c64909fd3bac58d5b45986f11689bee538eee6872049eebf47e312`.
The complete God Mode rule was reread and is byte-identical to the previously
reviewed rule, SHA-256
`d4e3b0cbf8559ab1a1520db60f6c529104e3e8f1a2e8e24f85222420388d05ac`.
Its filename pattern and `1 of them` condition are unchanged.

The full five-package scan completed without scanner errors and reported only
the two DI assembly paths below. A separate printed-string scan reported only
`Injection.pdb` in each assembly. Independent PE debug-directory/RSDS parsing
placed both matches inside compiler-generated CodeView paths ending in
`src/EmbedIO.DependencyInjection/obj/Release/{target}/EmbedIO.DependencyInjection.pdb`.
These are local Windows build paths; the matching suffix remains symbol-file
metadata. Both extracted DLLs were verified byte-for-byte against the package.

| Target | Offset | Assembly SHA-256 |
| --- | --- | --- |
| net10.0 | `0x6bce` | `b7a0aad636079c34bba8a2c13d113349c30384ebba8b0cb57035c5f8b6d7b837` |
| netstandard2.0 | `0x74bd` | `493292858a1aaf51e5bf4d0b14af028b5b7d79e0b6104da6029c447d7e555c81` |

The DI package SHA-256 is
`967704d9ce43c6fc020a3379697a7a2fdf636c5a18b6356a6be0a422fe0499e1`.
These bytes describe checkout `f1d4d75ade6c04d8a7eb65b93dec1f1b67775235`
plus the dirty v2 packaging overlay, recorded in
`TestResults/audit/v2-package-scan-input-manifest.json`. The nuspec commit is
the checkout checkpoint, not proof that the overlay was committed. The different
hashes and offsets do not replace the historical 1.0.3 CI evidence above.

This is a source-qualified review, not an acceptance change. Any approved
version-path update must be limited to
`dist/EmbedIO-Neo.DependencyInjection.2.0.0-unpacked/lib/net10.0/EmbedIO.DependencyInjection.dll`
and
`dist/EmbedIO-Neo.DependencyInjection.2.0.0-unpacked/lib/netstandard2.0/EmbedIO.DependencyInjection.dll`
for this exact rule. Preserve the separate two text-evidence path scopes and
failures for other findings, scanner errors, missing scans and stale entries.
ClamAV 1.5.4 with daily database 28152 (dated 2026-10-13 06:26:14)
scanned all five local package archives and their extracted assets: 70 files,
zero infected files, and successful completion. The fresh database update and
standalone scan used bounded commands with read-only source access. This local
result applies to the same dirty-overlay package inputs; it does not establish
final hosted CI or published-artifact results. No matching GitHub alert dismissal
or owner exception is claimed. Approval remains pending; final exact-source CI
and release artifact scans remain required.

Raw scan output is retained under ignored
`TestResults/audit/scan-v2-packages-current-pins` and
`TestResults/audit/scan-v2-di-printed`; the ClamAV log and terminal receipt are
under `TestResults/audit/scan-v2-packages-clamav`. Package and CodeView receipts are under
`TestResults/audit/v2-local-package-inspection`. Independent comparison hashes
are retained under `TestResults/audit/v2-di-yara-proposal`.
