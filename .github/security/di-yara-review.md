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

## Hosted v2 artifact follow-up

Malware run 38421579803 at PR #182's a7aa1365 head reports the same rule on
both 2.0.0 DI assemblies, with zero YARA tool errors. The separately downloaded
scanned package has SHA-256
`6cf76e8de8a89602a318b66865cc08f232f6de9551f84c85065c03c5bd2241e4`.
The net10.0 assembly hash is
`0aea1b9556daee42b95cfb332087df9df336f71ce5d981fe440b811008ec8994`;
the netstandard2.0 hash is
`9086fc6306ce434e591cc15049ac847758f28a7b49b43a48a66b1b0266e4145e`.
Independent RSDS metadata inspection finds the compiler-generated
EmbedIO.DependencyInjection.pdb path in each, at offsets 27436 and 29716.
These are different artifacts from the earlier local overlay. The original
failed policy report is preserved, including stale 1.0.3 acceptance paths and
the two binding source/review matches. The exact narrow acceptance and matching
alert-dismissal exceptions remain pending owner approval; no scanner success
is claimed. Downloaded assets, raw YARA result and metadata receipt are under
ignored TestResults/audit/pr182-packages-38421579803 and
TestResults/audit/pr182-yara-38421579803.

## Owner acceptance decision

On 2026-10-13 William explicitly approved the exact God Mode rule on both
2.0.0 DI assembly paths and on this review document and malware-accepted.toml,
waiving only their unavailable matching-alert dismissal. Earlier pending
statements are historical; this decision supersedes them for these four paths.
Full scans, other findings, tool errors, stale entries and every green-check
requirement remain enforced. No matching GitHub alert dismissal is claimed.

## 2.0.1 release revalidation

The exact-head malware run
[38831851808](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38831851808)
for PR #460 rebuilt all five 2.0.1 packages. YARA-X 1.21.0 with the pinned full
YARA Forge 20261011 bundle reported the same God Mode rule only on the two DI
assemblies below. The downloaded CI package and both extracted assemblies were
hashed independently. Each assembly contains one case-insensitive
`Injection.pdb` occurrence and no `Dropper.pdb`, `Bypass.pdb` or `Potato.pdb`
occurrence. The complete matching strings are the compiler-generated CodeView
paths already reviewed above:

```text
/home/runner/work/embedio-neo/embedio-neo/src/EmbedIO.DependencyInjection/obj/Release/net10.0/EmbedIO.DependencyInjection.pdb
/home/runner/work/embedio-neo/embedio-neo/src/EmbedIO.DependencyInjection/obj/Release/netstandard2.0/EmbedIO.DependencyInjection.pdb
```

| Artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.0.1.nupkg` | `2e4b0e73aa73738288063c1b4e59ae46578a9ceea1a7dcc018e43e3a86a7d541` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `7ce814259d5aab2c97433816c530ab582868e99ac14604758979c29ff4c00f91` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `0db15e952f3360e86736c1b4053adebe83f194960f95c53f1c6d6ffad761913e` |
| `yara-x.json` | `a09ea776c3a11613b9042c1956f7ac3dd87e7b4d6133f0f3b4fc57110799f0f9` |
| `clamav.json` | `2da83366345adefe0511ba887edbb9166770eed59749300b23c0fffb1f19d079` |

The YARA report contains eleven findings and no scanner errors: these two
versioned assembly paths and the nine existing accepted source/evidence paths.
ClamAV 1.5.4 with database 28154 scanned 1,506 files with no findings or errors.
The run report identifies synthetic PR merge source
`df1d161ce17c83ba1608cdc4b342536f901dbbd7`; PR head
`758a7c84aebf421e2914fd115136c8799661c4d2` contains the same release inputs.

Only the two package-version paths change from 2.0.0 to 2.0.1. The rule,
matched bytes, target frameworks, build behavior and evidence-file scope are
unchanged. GitHub does not create a code-scanning alert for this workflow's
YARA report, so there is no matching alert to dismiss. Other findings, missing
scans, scanner errors and stale acceptances remain fatal, and the replacement
exact-head scan must pass before merge or publication. Downloaded artifacts and
independent hashes are retained under ignored
`TestResults/release-2.0.1/ci-malware-38831851808`.

## 2.1.0 release revalidation

Release PR #463 malware run [38849633508](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38849633508)
reported the same two DI debug-symbol matches at the new versioned paths and
rejected the stale 2.0.1 paths. The report identifies merge source
`a80466f7852ed48bb99a55a54139f74463104103`, PR head
`9b1ec7985acb2b45fa26c3e68bf495122cab6728`.

Both scanner and full-rule archives were checked against `yara.json` SHA-256
pins. The complete God Mode rule was reread. YARA-X 1.21.0 / YARA Forge 20261011
was rerun with printed strings in a network-disabled container and read-only
mount against both exact downloaded CI assemblies. Each has exactly one match:
`Injection.pdb`, at `0x6c84` (net10.0) and `0x7573` (netstandard2.0), within the
compiler-generated CodeView paths listed above. No other rule or string matched.
The adapter source and build behavior are unchanged from 2.0.1.

| Artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.1.0.nupkg` | `0525f5f57e5550d6efbe800764ce62c394d3024938e881d35441339715dfed63` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `e1d0adb8c4aae32dc1e7bf0cf2a3b421717d5a3b06556b7c136667480317b256` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `541e114386583810889d59f1e046c2ff85109c80e6462a6d5e486fed77bcad03` |
| `yara-x.json` | `497ab9244b33f4f4dd24b540959a2aa8d23ad7565e78aa0fc03cd7737fadd06f` |
| `clamav.json` | `35e95653aaeef5d8dc31690b668a42c70c6eb408ebb3d7b743bddb9be74e46a6` |

The full CI report contains eleven expected matches and no scanner errors.
ClamAV 1.5.4/database 28154 scanned 1,541 files with no findings or errors.
Only the two package-version paths move to 2.1.0; the rule, targets and existing
evidence-file scope are unchanged. GitHub's open code-scanning alert list is
empty, and this YARA workflow creates no matching alert to dismiss. No dismissal
is claimed. Other findings, stale acceptances, missing scans and scanner errors
remain fatal; replacement exact-head scans must pass. Evidence is retained under
ignored `TestResults/release-2.1.0`, including both printed-match logs.

## 2.2.0 release revalidation

Independent release scan [38940153246](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38940153246)
built all six packages from release branch commit `b80c867f`. Its policy report
rejected the two stale 2.1.0 paths while reporting the same two DI debug-symbol
matches at their new 2.2.0 paths. The scanner and full-rule archives were checked
against the unchanged `yara.json` SHA-256 pins, and the complete God Mode rule
was reread. The full pinned YARA-X 1.21.0 / YARA Forge 20261011 rules were rerun
with printed strings in a network-disabled container and read-only mounts
against both exact CI assemblies. The only match in each is `Injection.pdb`,
at `0x6c84` (net10.0) and `0x7573` (netstandard2.0), within the compiler-generated
CodeView paths already listed above. No other rule or string matched.

| Artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.2.0.nupkg` | `ddb7c30960488307acc4f9845e217a215cf2d041aa2c1538831421359939b2f7` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `d0baec4b5f6eb0c9bac61ff5bda4d447bc26edf5a872b8d62a3f60902e99f951` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `c8d4247631226dbfa14b8202d8b067f7d664c2be419d39224d732f31bda05777` |
| `yara-x.json` | `675da5319e3b5fc625745d87e7437156877e71e49781e940e1ef6cb2629f2c06` |
| `clamav.json` | `69a1665b7ccf8edcebfecb06407e43dde7e2eff6dc04408561bb617f65f69717` |

The full CI report contains eleven expected findings and no scanner errors.
ClamAV 1.5.4/database 28155 scanned 1,573 files with no findings or errors.
The DI source, targets, build behavior, scanner pins and matched filename
metadata are unchanged; only the two accepted package-version paths move to
2.2.0. GitHub's open code-scanning alert list is empty and the YARA workflow
creates no matching alert. The existing owner-approved unavailable-alert
exception for this reviewed debug-symbol finding is carried forward; no alert
dismissal is claimed. Other findings, missing scans, scanner errors and stale
acceptances remain fatal. Final exact-head scans must pass before merge or
publication. Evidence is retained under ignored `TestResults/release-2.2.0`,
including archive verification and both full-rule printed-match logs.

## 2.3.0 release revalidation

Release PR #471 malware run [38970787582](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38970787582)
reported the same two DI debug-symbol matches at the new package paths and
rejected the stale 2.2.0 paths. The report identifies merge source
`6d07cabc509ca51a6068f715a8f305c2a673d250`, PR head
`849f9535bc2351a4d40d8d735c467a527c6681f4`.

Both scanner and full-rule archives, the extracted scanner and complete rule
source were checked against the unchanged committed pins. The complete God Mode
rule was reread. YARA-X 1.21.0 / YARA Forge 20261011 was rerun with printed
strings in a network-disabled, read-only container against both exact CI
assemblies. Each has exactly one match: `Injection.pdb`, at `0x6c84`
(net10.0) and `0x7573` (netstandard2.0), inside the compiler-generated CodeView
paths already documented above. No other rule or string matched either assembly.
The DI source, targets and build behavior are unchanged from 2.2.0.

| Artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.3.0.nupkg` | `2d5b3de9494455235a31da3f7206f92aa9da5d00ea9a42922e03b0af4672b6ce` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `4c4bf312b53b5e7a6a4becdc13e18b0fa0385079e2e457be01f0911bb6fd5366` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `6103bdfe7cedeb45887fedeffb36c8c85f1f07ec6d37b1950c319c77c65a2faf` |
| `yara-x.json` | `37627be08dd8d5279cbc070580c126cf5625cb7c7d187746a8241409cf412bba` |
| `clamav.json` | `37bb3334c5ca54af70f8f20890cac7005e5d8c63c782399192411fd8041f2329` |

The full CI report contains eleven expected matches and no scanner errors.
ClamAV 1.5.4/database 28155 scanned 1,622 files with no findings or errors.
Only the two exact package-version acceptance paths move to 2.3.0; the rule,
matched metadata, targets and evidence-file scope remain unchanged. GitHub's
open code-scanning alert list was checked and is empty; this YARA workflow
creates no matching alert. The existing owner-approved unavailable-alert
exception for this reviewed debug-symbol finding is carried forward; no
dismissal is claimed. Other findings, stale entries, scanner errors and missing
scans remain fatal. Fresh exact-head scans must pass before merge/publication.
Evidence is retained under ignored `TestResults/merge-ready/release-yara`,
including archive checks, CodeView parsing and both full-rule printed-match logs.

## 2.4.0 release revalidation

Release PR #473 scan [38991457914](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38991457914)
reports the same two DI debug-symbol matches at the new 2.4.0 package paths.
The reviewed PR head is `c21580221dae85c02464b56eca2a62d4353a797c`.
Both pinned scanner/rule archives and their extracted contents were verified.
The complete God Mode rule was reread, and full YARA-X 1.21.0 / YARA Forge
20261011 rules were rerun with printed strings in a network-disabled,
read-only container against both exact CI-built assemblies. Each has only
`Injection.pdb` in its compiler-generated CodeView path, at `0x6c84`
(net10.0) and `0x7573` (netstandard2.0). No other rule or string matches.
DI source, targets and build behavior remain unchanged.

| Artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.4.0.nupkg` | `2ed85882988d977396b6f3709e78cecae7b4f01ae9f4736a3b0fa025d6613014` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `d92f452240dadab985d22208a64c4c7bafaf22b9f8fde7ae2f06ab86f990ccfe` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `f82870fb2dc9dce931a29d2c0e652042b909005a73c12970fd19599e2f2b2240` |
| `yara-x.json` | `087d9054aa167ad81d8b48da7a003bb67da39c53aca58d51c88e4fdb72d6a708` |
| `clamav.json` | `40768578dceda5aa211f6917eedea1833f8a2e4a93b672e185f99099a14cbf74` |

The complete CI report contains eleven expected matches and no scanner errors.
ClamAV 1.5.4/28155/Fri Oct 16 06:24:17 2026 scanned 1,634 files with no matches or errors.
Only the two exact package-version acceptance paths move to 2.4.0; the rule
and evidence-file scope remain unchanged. GitHub's open code-scanning alert
list is empty, and this YARA workflow creates no matching alert. The existing
owner-approved unavailable-alert exception is carried forward; no dismissal
is claimed. Other matches, stale entries, scanner errors and missing scans
remain fatal. Fresh exact-head scans are required before merge/publication.
Evidence is under ignored `TestResults/release-2.4.0/yara-review`.

## 2.5.0 release revalidation

Release PR #475 scan [38998700700](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/38998700700)
reports the same two DI debug-symbol matches at the new 2.5.0 package paths.
The reviewed PR head is `c78d1938bf2debc682828df6b1a16e049873ef8d`.
Both pinned scanner/rule archives and extracted contents were verified. The complete
God Mode rule was reread, and full YARA-X 1.21.0 / YARA Forge 20261011 rules were
rerun with printed strings against both exact CI-built assemblies in a read-only,
network-disabled container. Each assembly has only `Injection.pdb` in its compiler
generated CodeView path, at `0x6c84` (net10.0) and `0x7573` (netstandard2.0).
No other rule or string matches. DI source, targets and build behavior are unchanged.

| Artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.5.0.nupkg` | `1af5770f62573775719fd8e245763ce196f331822fe28af48393a2548497f7f0` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `3142ad201dab175719aa3b1d562becca445fe3164c08f8f8517efa3429940fe3` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `c7b6b48fb9d1c8f2e66ed2003a1c5eef5e4831cec731b907cff81a87489c1ca7` |
| `yara-x.json` | `0663a3e060146669edd7f9f8854055df14bce45a51c159931d69d4e40daadf78` |
| `clamav.json` | `5d86e95c6f0f8957c06cde4d3a752cfdd4a7f38db640ffe412eeae0b9dee3202` |

The complete CI report contains 11 expected matches and no scanner errors.
ClamAV 1.5.4/28155/Fri Oct 16 06:24:17 2026 scanned 1,642 files with no matches or errors.
Only the two exact package-version acceptance paths move to 2.5.0; the rule and
evidence-file scope remain unchanged. GitHub's open code-scanning alert list is
empty, and this YARA workflow creates no matching alert. The existing owner-approved
unavailable-alert exception is carried forward; no dismissal is claimed. Other
matches, stale entries, scanner errors and missing scans remain fatal. Fresh
exact-head scans must pass before merge/publication. Evidence is retained under
ignored `TestResults/release-2.5.0/yara-review`.

## 2.6.0 release revalidation

Release PR #477 scan [39006903104](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/39006903104)
reports the same two DI debug-symbol matches at the new 2.6.0 package paths.
The reviewed PR head is `fc1c427e3fa4c7e6115a3d37fa7025e745490d36`.
Both pinned scanner/rule archives and extracted contents were verified. The complete
God Mode rule was reread, and full YARA-X 1.21.0 / YARA Forge 20261011 rules were
rerun with printed strings against both exact CI-built assemblies in a read-only,
network-disabled container. Each assembly has only `Injection.pdb` in its compiler
generated CodeView path, at `0x6c84` (net10.0) and `0x7573` (netstandard2.0).
No other rule or string matches. DI source, targets and build behavior are unchanged.

| Artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.6.0.nupkg` | `8013195947694442df9635e6015856151f307447a8655bd8290285a8607bd434` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `7c8ba95b3c7f657db2c461346632b076d0929452cd5210cad9b2ee577831baca` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `91a355d479923cef0e681b10442777b524366966846cb89f973432f9702194d1` |
| `yara-x.json` | `2067e858d689a3dd4e5598d5eb842ef4310f9608fc44f243c595389b6d86854b` |
| `clamav.json` | `bc94452c22ddec8fd9b4a23703909b3021564dade2530ba8a52c52dece60d07f` |

The complete CI report contains 11 expected matches and no scanner errors.
ClamAV 1.5.4/28155/Fri Oct 16 06:24:17 2026 scanned 1,652 files with no matches or errors.
Only the two exact package-version acceptance paths move to 2.6.0; the rule and
evidence-file scope remain unchanged. GitHub's open code-scanning alert list is
empty, and this YARA workflow creates no matching alert. The existing owner-approved
unavailable-alert exception is carried forward; no dismissal is claimed. Other
matches, stale entries, scanner errors and missing scans remain fatal. Fresh
exact-head scans must pass before merge/publication. Evidence is retained under
ignored `TestResults/release-2.6.0/yara-review`.


## 2.7.0 release revalidation

The locally built 2.7.0 candidate retains the two previously reviewed DI compiler
CodeView debug-symbol matches. The pinned YARA-X 1.21.0 and YARA Forge 20261011
archives and extracted contents were verified, the complete God Mode rule was
reread, and full rules were scanned with printed strings in a read-only,
network-disabled container. Each assembly matches only `Injection.pdb` in its
compiler-generated debug-symbol path: `0x6c9e` (net10.0) and `0x758d`
(netstandard2.0). No other rule or string matches either assembly. The DI source,
targets and build behavior are unchanged; the release changes assembly version
metadata. These are Windows-built candidate bytes, not final CI artifact hashes.

| Local candidate artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.7.0.nupkg` | `0a93174fd371532b2000cb4a52daa6167c3f9299026607d9b40553607cbb3fed` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `636d03f08e74b93051c25cddff3c7fb861981a6275fa9dfc3bb60ea79072d3db` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `f2cfece9e0b1de73d786cc15a3c6ed74148138520369b12e02119dc4ca05edaf` |

Only the two exact package-version paths move to 2.7.0. The rule and evidence-file
acceptances remain unchanged. The current GitHub code-scanning alert list contains
no corresponding YARA alert; this workflow exposes no matching alert to dismiss.
The existing owner-approved unavailable-alert exception is retained; no dismissal
is claimed. Fresh exact-head full CI scans, review of final CI artifact matches and
every other check remain required before merge/publication. Other findings, errors
and stale entries remain fatal. Local source, pins, assembly hashes, CodeView
metadata and printed-match evidence are under ignored
`TestResults/release-2.7.0/yara-review`.


## 2.8.0 release revalidation

The locally built 2.8.0 candidate retains the two previously reviewed DI compiler
CodeView debug-symbol matches. Verified pinned YARA-X 1.21.0 / YARA Forge
20261011 archives and extracted contents, the complete God Mode rule and full
read-only, network-disabled scans with printed strings identify only
`Injection.pdb` in each compiler-generated symbol path: `0x6c9e` (net10.0) and
`0x758d` (netstandard2.0). Neither assembly matches another rule or string.
The DI source, targets and executable behavior are unchanged. These are local
Windows candidate bytes, before the release commit, not final CI artifact hashes.

| Local candidate artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.8.0.nupkg` | `bba59b5b608252c5c5e6d8445f1e6c7cc1643b9bffb05dd1e5724899a3b9d01e` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `9229bc7bbaceae0ab0e8e2dc201b2b89094df2d62834b806b5247e5aff1c24d4` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `a9ed4b73cdc6e0b1b5c0099c0be6ff40fd6bf2faefde8261d4e0ace6d4fa0876` |

Only the two exact package-version paths move from 2.7.0 to 2.8.0. Rule and
evidence-file acceptances remain unchanged. GitHub exposes no matching YARA alert
in any state to dismiss; the existing owner-approved unavailable-alert exception
is retained, and no dismissal is claimed. Final exact-head CI scans and review of
their actual package bytes remain required before merge and publication. Other
findings, scanner errors and stale entries remain fatal. Local packages, pins,
CodeView paths, full printed-match scans and the paginated alert receipt are
under ignored `TestResults/release-2.8.0/yara-review`.


## 2.9.0 request-abort release revalidation

The locally built 2.9.0 candidate retains the two reviewed DI compiler CodeView
matches. Verified pinned YARA-X 1.21.0 / YARA Forge 20261011 archives, extracted
contents and the complete God Mode rule. Full read-only, network-disabled scans
with printed strings match only `Injection.pdb` in each compiler-generated symbol
path: `0x6c9e` (net10.0) and `0x758d` (netstandard2.0). No other rule or string
matches either assembly. DI source, targets and executable behavior are unchanged;
its rebuilt metadata reflects the updated core reference/version. These are local
Windows candidate bytes before the feature commit, not final CI artifact hashes.

| Local candidate artifact | SHA-256 |
| --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.9.0.nupkg` | `0a5dd40f026e543ff13e9cd4e671dfb484fb2b3dc03d1c89ff1847a02a822a5c` |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `59ffced1413b2573ac66fe2cbecca2f571f722f940ec15661c9412f0415a892b` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `20d245d7f539c36a94e13d32c571c326d2e39c40e2241d3ef5cf79d5b25126aa` |

Only the two exact package assembly paths move from 2.8.0 to 2.9.0. Rule and
source/evidence-file acceptances are unchanged. Explicit queries of open, fixed
and dismissed GitHub alerts expose no matching YARA alert; the existing
owner-approved unavailable-alert exception for this finding is retained, and no
dismissal is claimed. Exact-head full CI scans and review of the final CI package
bytes remain required before merge/publication. Other findings, scanner errors
and stale entries remain fatal. Source, pins, CodeView paths, printed-match scans
and alert receipts are under ignored `TestResults/request-abort/yara-review`.


## 2.10.0 output-cache release revalidation

The release PR scan [39100390041](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/39100390041)
rejected the two new package-version paths and the stale 2.9.0 entries. The exact
`scanned-nupkg` artifact identifies PR merge source
`b5adb0d668f349b8cdc7493d6d4bbe4681bf09c4` (head `eba91ae7b0acc985b0efabc30f4021abd9834808`).
Both pinned YARA-X 1.21.0 / YARA Forge 20261011 archives and extracted contents
were verified; the complete God Mode rule was reread. Full-rule scans in a
read-only, network-disabled container with printed strings matched only
`Injection.pdb` in each compiler CodeView path. No other rule or string matched
either assembly. DI source and scanner pins are unchanged from 2.9.0.

| Scanned CI artifact | SHA-256 | Match offset |
| --- | --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.10.0.nupkg` | `a5a40b2af0aa1526efbfb0cef9f087c0e0974e07a36141c2a7e10e4934716458` | n/a |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `2697707f4584cda4892d7565e37ecfb410538f6c12ff37e65751635b77d08c0b` | `0x6c88` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `1180ede215a36ddb394f79c249890e2ffaba4d7295c313aab448c5b376b9d449` | `0x7573` |

Only the two exact package-version assembly paths move to 2.10.0. The rule and
source/evidence-file boundaries remain unchanged. Paginated queries of open,
fixed and dismissed GitHub code-scanning alerts found no matching YARA alert;
the existing owner-approved unavailable-alert exception is retained and no
dismissal is claimed. These hashes describe the reviewed CI artifact, not future
builds. Fresh exact-head full scans and every other gate remain required before
merge/publication. Other findings, errors and stale acceptances remain fatal.
Raw packages, pin checks, complete rule, CodeView paths, printed-match scans and
alert receipts are retained under ignored `TestResults/release-2.10.0/yara-review`.


## 2.11.0 idempotency release revalidation

The [release PR scan](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/39109784808)
rejected the two 2.11.0 assembly paths and stale 2.10.0 acceptances. Its exact
`scanned-nupkg` identifies integration source
`7b1eab5af6431ae86b0515d9eacfbdb65f7d0dc9` (head
`0f16deb56835830cf9a4be98462a83bd32285b42`). Both pinned archives and extracted
contents for YARA-X 1.21.0 / YARA Forge 20261011 were verified; the complete God
Mode rule was reread. Full-rule scans with printed strings in a read-only,
network-disabled container match only `Injection.pdb` in each compiler-generated
CodeView symbol path. Independent RSDS/CodeView inspection places each match
inside that path. No other rule/string matches either assembly. DI source and
scanner pins are unchanged from released 2.10.0; ClamAV reports zero matches.

| Scanned CI artifact | SHA-256 | Match offset |
| --- | --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.11.0.nupkg` | `38b08a505f64ce74b9d276cae09e8f3dc432771cdee164acec5908538f8d8ead` | n/a |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `c2b9b8808dc672f9c7777c4652dbd98afe6f40ad4c3343844afca07d4cc6ef2c` | `0x6c88` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `0faebf8b795690489fcf90e341ea21c13b75b56632b4de2409907ba83b2fb907` | `0x7573` |

Only the two exact package-version paths move to 2.11.0. The rule and
source/evidence-file boundaries remain unchanged. Paginated queries of open,
fixed and dismissed GitHub alerts expose no matching YARA alert. The existing
owner-approved unavailable-alert exception for this same reviewed symbol-filename
finding is retained; no dismissal is claimed. These hashes identify this artifact,
not future builds. Fresh exact-head full scans, review of final package bytes and
every other merge/publication gate remain required. Other findings, scanner errors
and stale entries remain fatal. Raw packages, pins, complete rule, CodeView paths,
printed-match scans and alert receipts are under ignored
`TestResults/release-2.11.0/yara-review`.


## 2.12.0 resumable-upload release revalidation

Both pinned YARA-X 1.21.0 / YARA Forge 20261011 archives and extracted contents
were verified, and the complete God Mode rule was reread. Full-rule scans with
printed strings in a read-only, network-disabled container matched only
`Injection.pdb` inside each compiler-generated RSDS/CodeView symbol path.
No other rule matched either assembly. DI source and scanner pins are unchanged
from released 2.11.0.

| Reviewed local precommit candidate | SHA-256 | Match offset |
| --- | --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.12.0.nupkg` | `df7a90abb7dafecbd7975951d64a7099cc1647799482aba2431282555ae318c0` | n/a |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `2482c4b9a46e90087492f50ac00c47c81871fc36e54db48834d3676e01e808a6` | `0x6ca2` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `e2dbe0c5b8bc989fc212bd1f88f7fc821df06de6de16745d6730a2db09cbf836` | `0x758d` |

Only the two exact version-qualified package assembly paths move to 2.12.0.
Paginated queries of open, fixed and dismissed GitHub code-scanning alerts
expose no matching YARA alert. On 2026-10-18 William explicitly approved both
proposed 2.12.0 paths and the unavailable-alert waiver: “Approve both paths and
the unavailable-alert waiver.” This waiver applies only to this reviewed rule
on these two paths; no alert dismissal is claimed. Existing source/evidence-file
acceptances are unchanged. Every other finding, scanner error and stale
acceptance remains fatal.

These hashes identify the reviewed local candidate, not future CI or release
builds. Exact-head full scans, final CI package byte review and every other
merge/publication gate remain required. Raw packages, verified pins, complete
rule, CodeView paths, printed-match scans, alert receipts and the approved
proposal are retained under ignored `TestResults/release-2.12.0/yara-review`.

## 2.13.0 response security-header release revalidation

Pinned YARA-X 1.21.0 / YARA Forge 20261011 archives and extracted bytes were
verified again. Full-rule scans with printed strings in a read-only,
network-disabled container matched only `Injection.pdb` in each compiler
RSDS/CodeView symbol path under `GODMODERULES_IDDQD_God_Mode_Rule`. No other
rule matched either assembly. DI source and scanner pins are unchanged.

| Reviewed local precommit candidate | SHA-256 | Match offset |
| --- | --- | --- |
| `EmbedIO-Neo.DependencyInjection.2.13.0.nupkg` | `23ee1f75acf30c920fd9ccaa6400d8dc0597a5e348013df49c9f8c884eea7636` | n/a |
| `lib/net10.0/EmbedIO.DependencyInjection.dll` | `b2764a89046b050a5d4a78226ddd58dcf9230c031182813357de861840dbc6bc` | `0x6ca2` |
| `lib/netstandard2.0/EmbedIO.DependencyInjection.dll` | `4cce8bfeec925107408ed65cd8ad753b8106f6dd579e99c133818f9bf6f8ed05` | `0x758d` |

Only the two exact version-qualified package assembly paths move to 2.13.0.
Paginated open, fixed and dismissed GitHub code-scanning queries expose no
matching YARA alert. On 2026-10-18 William explicitly approved: "Approve both
2.13.0 paths and the unavailable-alert waiver." No dismissal is claimed. This
waiver covers only this reviewed rule on these two paths. Other matches,
scanner errors, stale acceptances and every final CI gate remain blocking.

Candidate hashes identify local precommit bytes, not future CI/release builds.
Verified pins, full rule, printed scans, exact bytes, alert-query receipts and
the approved proposal remain under ignored `TestResults/release-2.13.0/yara-review`
and `local-yara`. Final CI and published packages require independent exact-byte
and full-scan verification.

## 2.14.0 typed results release revalidation

Written acceptance covers only `GODMODERULES_IDDQD_God_Mode_Rule` on:

- `dist/EmbedIO-Neo.DependencyInjection.2.14.0-unpacked/lib/net10.0/EmbedIO.DependencyInjection.dll`
- `dist/EmbedIO-Neo.DependencyInjection.2.14.0-unpacked/lib/netstandard2.0/EmbedIO.DependencyInjection.dll`

Pinned YARA-X 1.21.0 / YARA Forge 20261011 full-rule scans independently match only `Injection.pdb` in compiler RSDS/CodeView metadata. No other rule matched either assembly. Pinned archive/extracted-tool hashes and exact CodeView byte bounds verified. No DI code or scanner-pin changes.

Local package SHA-256: `0f0524c70f0e3e22fbb47deaa1fa57dffe32409ab07f9f1347b6ef7b4b82d009`.
net10.0 assembly SHA-256: `0dae5cc4f29fade8f4aad25853d3a838bba70da70a11cf5287b4431b3266fcc4`, offset `0x6ca2`.
netstandard2.0 assembly SHA-256: `a9cb2999814214114c92fdd01c06d0f88f72f56758a871af8e57e3e3606bb027`, offset `0x758d`.

Paginated open/fixed/dismissed code-scanning alert queries expose no matching alert. William explicitly approved: "Approve both 2.14.0 paths and the unavailable-alert waiver." No dismissal is claimed. Local candidate hashes identify reviewed precommit bytes; final CI and published packages require independent exact-byte and full-rule verification. Every other match, scanner error, stale acceptance and CI check remains blocking. This new approval is restricted to the two 2.14.0 paths above. Evidence is retained under ignored `TestResults/release-2.14.0/yara-review` and `local-yara`.

## 2.15.0 typed request binding release revalidation

Written acceptance covers only `GODMODERULES_IDDQD_God_Mode_Rule` on:

- `dist/EmbedIO-Neo.DependencyInjection.2.15.0-unpacked/lib/net10.0/EmbedIO.DependencyInjection.dll`
- `dist/EmbedIO-Neo.DependencyInjection.2.15.0-unpacked/lib/netstandard2.0/EmbedIO.DependencyInjection.dll`

Pinned YARA-X 1.21.0 / YARA Forge 20261011 full-rule scans with printed strings independently match only `Injection.pdb` in compiler RSDS/CodeView metadata. No other rule matched either assembly. Archive/extracted-tool hashes and exact CodeView byte bounds verified. No DI source or scanner-pin changes.

Local precommit package SHA-256: `d79d1646a93f6b17d62ffbe5273b2baea67597ace628cabd3b306f49db624d52`.
net10.0 assembly SHA-256: `973ede990e7f35378cfd04c4f75b99a331b29aa07734e14ece02bc7fc6455698`, match offset `0x6ca2`.
netstandard2.0 assembly SHA-256: `213daae575c078f00c8d29a8a41a48654157dbb189f46da8f22a0afa4be4aca4`, match offset `0x758d`.

Paginated open/fixed/dismissed GitHub code-scanning alert queries expose no matching YARA alert. William explicitly approved: "Approve both 2.15.0 paths and the unavailable-alert waiver." No dismissal is claimed. Local hashes identify reviewed precommit bytes, not future CI/release builds. Final exact-byte reviews, independent full scans and all CI gates remain required. Every other finding, scanner error and stale acceptance remains fatal. This approval is restricted to the two version-qualified 2.15.0 paths above. Evidence remains under ignored TestResults/release-2.15.0/yara-review and local-yara.

## 2.16.0 minimal endpoints release revalidation

Written acceptance covers only `GODMODERULES_IDDQD_God_Mode_Rule` on:

- `dist/EmbedIO-Neo.DependencyInjection.2.16.0-unpacked/lib/net10.0/EmbedIO.DependencyInjection.dll`
- `dist/EmbedIO-Neo.DependencyInjection.2.16.0-unpacked/lib/netstandard2.0/EmbedIO.DependencyInjection.dll`

Pinned YARA-X 1.21.0 / YARA Forge 20261011 full-rule scans with printed strings independently match only `Injection.pdb` in compiler RSDS/CodeView metadata. No other rule matched either assembly. Archive/extracted-tool hashes and exact CodeView byte bounds verified. No DI source or scanner-pin changes.

Local precommit package SHA-256: `5041275679121518b4e9c4f7c2921f3b571786c72d2ee90fba8477ef808c296a`.
net10.0 assembly SHA-256: `54760b5fb8a02fec09810b2d02bf512f5378644cc16ecfa244a2bf858dd36e44`, match offset `0x6ca2`.
netstandard2.0 assembly SHA-256: `97908cd90a39200dca5223110dac1b778428271f634ff8e68131ceec33f8bee4`, match offset `0x758d`.

Paginated open/fixed/dismissed GitHub code-scanning alert queries expose no matching YARA alert. William explicitly approved: "Approve both 2.16.0 paths and the unavailable-alert waiver." No dismissal is claimed. Local hashes identify reviewed precommit bytes, not future CI/release builds. Final exact-byte reviews, independent full scans and all CI gates remain required. Every other finding, scanner error and stale acceptance remains fatal. This approval is restricted to the two version-qualified 2.16.0 paths above. Evidence remains under ignored TestResults/release-2.16.0/yara-review and local-yara.

## 2.16.1 HTTP/1 patch release revalidation

Written acceptance covers only `GODMODERULES_IDDQD_God_Mode_Rule` on:

- `dist/EmbedIO-Neo.DependencyInjection.2.16.1-unpacked/lib/net10.0/EmbedIO.DependencyInjection.dll`
- `dist/EmbedIO-Neo.DependencyInjection.2.16.1-unpacked/lib/netstandard2.0/EmbedIO.DependencyInjection.dll`

2.16.1 ships from the `release/2.16` branch: v2.16.0 with one HTTP/1 listener correction. Main carries the same version number and correction afterwards, so its CI-built DI assemblies use the same two paths. DI source on main differs from v2.16.0 only by later formatting and cleanup, and scanner pins are unchanged. The pinned YARA-X 1.21.0 (`yara-x-cli 1.21.0`) and YARA Forge 20261018 full rules (archive SHA-256 `c6a860533194d8c1209bc066c6dc6df9ecb0a2f9b7fc4104a5d87d0d3a651b73`, as pinned in `.github/security/yara.json`) were run with printed strings in the pinned `mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2` container without network access, on local precommit packages from both sources. Each assembly matches only `Injection.pdb` in compiler RSDS/CodeView metadata, once. No other rule matched either assembly.

| Source | Package SHA-256 | net10.0 assembly SHA-256, match offset | netstandard2.0 assembly SHA-256, match offset |
| --- | --- | --- | --- |
| `release/2.16` | `004da86d3695cc09078aac8940da0e72070c255290807217bc00233fefaab057` | `9129f87f5090a7438991b8da6cb475b559b613fb433b72ab1c3cba35c0bbba2c`, `0x6c96` | `980c1108c984e47434f8f18236823d13f0991164a4a6ddb3aa4f48ae6001fee7`, `0x7581` |
| main | `2ea99f6756f8bac3c49fc916ed1b879d8ad70264d624336a701c753774958f79` | `d25a8a66e3658f298367ebe2ad1fd6e75454131ce2ce5720528e3213738bb27d`, `0x6ccc` | `5bd17f839c854f85ce295bad1f4a6cbf8593f44d87b8dd7fb81ae9c3ebf407e2`, `0x75d7` |

Local offsets differ from CI-built offsets because the local build paths differ. Paginated open/fixed/dismissed GitHub code-scanning alert queries on 2026-10-22 expose no YARA alert at all. William explicitly approved, choosing "Approve 2.16.1 paths" to move this acceptance to the two 2.16.1 paths with the same waiver for the alert GitHub does not expose. No dismissal is claimed. Local hashes identify reviewed precommit bytes, not CI or release builds; final exact-byte reviews, independent full scans and all CI gates remain required. Every other finding, scanner error and stale acceptance remains fatal. This approval is restricted to the two version-qualified 2.16.1 paths above. Evidence remains under ignored TestResults/patch-2.16.1/logs/pack-01 and TestResults/http1-fix-main/logs/validate-01.

## 2.17.0 release revalidation

Written acceptance covers only `GODMODERULES_IDDQD_God_Mode_Rule` on:

- `dist/EmbedIO-Neo.DependencyInjection.2.17.0-unpacked/lib/net10.0/EmbedIO.DependencyInjection.dll`
- `dist/EmbedIO-Neo.DependencyInjection.2.17.0-unpacked/lib/netstandard2.0/EmbedIO.DependencyInjection.dll`

2.17.0 ships from main after #506, which adds the EmbedIO.Quic transport. DI source differs from main's 2.16.1 record (`9732a7f7`) only by its lock file, which records the core's new transitive `EmbedIO-Neo.Quic.Native` dependency; against the `v2.16.1` tag it also carries main's later formatting and cleanup. Scanner pins are unchanged. The pinned YARA-X 1.21.0 (`yara-x-cli 1.21.0`) and YARA Forge 20261018 full rules (archive SHA-256 `c6a860533194d8c1209bc066c6dc6df9ecb0a2f9b7fc4104a5d87d0d3a651b73`, as pinned in `.github/security/yara.json`) were run with printed strings in the pinned `mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2` container without network access, on a local precommit package. Each assembly matches only `Injection.pdb` in compiler RSDS/CodeView metadata, once. No other rule matched either assembly.

| Source | Package SHA-256 | net10.0 assembly SHA-256, match offset | netstandard2.0 assembly SHA-256, match offset |
| --- | --- | --- | --- |
| `6aac6230` | `d5105bcddc965ee12a4f9760218d6f34384e004d84c560e186546ba86fdbd72b` | `e698b33d58a2824ca8583eda79576cda8f10f0770c81b917966b158fe95d123b`, `0x6ccc` | `c58e502fb954d9ac28780f7bdc1345e308a7613d78b92144513b120079a42ca8`, `0x75d7` |

Local offsets differ from CI-built offsets because the local build paths differ. Paginated open/fixed/dismissed GitHub code-scanning alert queries on 2026-10-22 expose no YARA alert at all. William explicitly approved, choosing "Approve 2.17.0 paths" to move this acceptance to the two 2.17.0 paths with the same waiver for the alert GitHub does not expose. No dismissal is claimed. Local hashes identify reviewed precommit bytes, not CI or release builds; final exact-byte reviews, independent full scans and all CI gates remain required. Every other finding, scanner error and stale acceptance remains fatal. This approval is restricted to the two version-qualified 2.17.0 paths above. Evidence remains under ignored TestResults/release-2.17.0/logs/precommit-02.

## 2.18.0 release revalidation

Written acceptance covers only `GODMODERULES_IDDQD_God_Mode_Rule` on:

- `dist/EmbedIO-Neo.DependencyInjection.2.18.0-unpacked/lib/net10.0/EmbedIO.DependencyInjection.dll`
- `dist/EmbedIO-Neo.DependencyInjection.2.18.0-unpacked/lib/netstandard2.0/EmbedIO.DependencyInjection.dll`

2.18.0 ships from main after #545. DI source is unchanged since `v2.17.0`, and scanner pins are unchanged. The pinned YARA-X 1.21.0 (`yara-x-cli 1.21.0`) and YARA Forge 20261018 full rules (archive SHA-256 `c6a860533194d8c1209bc066c6dc6df9ecb0a2f9b7fc4104a5d87d0d3a651b73`, as pinned in `.github/security/yara.json`) were run with printed strings in the pinned `mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2` container without network access, on a local precommit package. Each assembly matches only `Injection.pdb` in compiler RSDS/CodeView metadata, once. No other rule matched either assembly.

| Source | Package SHA-256 | net10.0 assembly SHA-256, match offset | netstandard2.0 assembly SHA-256, match offset |
| --- | --- | --- | --- |
| `8ae40f9d` | `35c99481134f15fbd06939058bebe85277fd4c9c36d6f94994df524fb6c0938e` | `436a312c135d71306b92e7e85602661569c74c937e40b717d480788b2a1f70b8`, `0x6cc7` | `399079f8d5bdcfd2901ca6cdf921769bd1f3d46c7b10b4a21b091a3b3072d26e`, `0x75d2` |

Local offsets differ from CI-built offsets because the local build paths differ. Paginated open/fixed/dismissed GitHub code-scanning alert queries on 2026-10-24 expose no YARA alert at all. William explicitly approved, choosing "Approve 2.18.0 paths" on 2026-10-24, to move this acceptance to the two 2.18.0 paths with the same waiver for the alert GitHub does not expose. No dismissal is claimed. Local hashes identify reviewed precommit bytes, not CI or release builds; final exact-byte reviews, independent full scans and all CI gates remain required. Every other finding, scanner error and stale acceptance remains fatal. This approval is restricted to the two version-qualified 2.18.0 paths above. Evidence remains under ignored TestResults/release-2.18.0/logs/precommit-01.

## 2.19.0 release revalidation

Written acceptance covers only `GODMODERULES_IDDQD_God_Mode_Rule` on:

- `dist/EmbedIO-Neo.DependencyInjection.2.19.0-unpacked/lib/net10.0/EmbedIO.DependencyInjection.dll`
- `dist/EmbedIO-Neo.DependencyInjection.2.19.0-unpacked/lib/netstandard2.0/EmbedIO.DependencyInjection.dll`

2.19.0 ships from main after #570. DI source is unchanged since `v2.18.0`. The YARA Forge pin moved from 20261018 to 20261026 in #558; the YARA-X pin is unchanged. The pinned YARA-X 1.21.0 (`yara-x-cli 1.21.0`) and YARA Forge 20261026 full rules (archive SHA-256 `9d47f293c03171f0cfaba783961d46b12ed26dad22a9519b58fa11cc64582595`, as pinned in `.github/security/yara.json`) were run with printed strings in the pinned `mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2` container without network access, on a local precommit package. Each assembly matches only `Injection.pdb` in compiler RSDS/CodeView metadata, once. No other rule matched either assembly.

| Source | Package SHA-256 | net10.0 assembly SHA-256, match offset | netstandard2.0 assembly SHA-256, match offset |
| --- | --- | --- | --- |
| `e7af99e5` | `8a4a672d8aff9c4c662e571e92c71f8ef186a54f5721a3b74051a716d3c29eb6` | `277c90d1d3df48c20c2a43e069037199c7249792ddf9ff0b4ed9313d8505ae00`, `0x6ccc` | `309b4126c36d6debe10eb92713e09c3c34c8251cdc3cbe20b8a967d38820508d`, `0x75d7` |

Local offsets differ from CI-built offsets because the local build paths differ. Paginated open/fixed/dismissed GitHub code-scanning alert queries on 2026-10-27 expose no YARA alert at all. William explicitly approved, choosing "Approve 2.19.0 paths" on 2026-10-27, to move this acceptance to the two 2.19.0 paths with the same waiver for the alert GitHub does not expose. No dismissal is claimed. Local hashes identify reviewed precommit bytes, not CI or release builds; final exact-byte reviews, independent full scans and all CI gates remain required. Every other finding, scanner error and stale acceptance remains fatal. This approval is restricted to the two version-qualified 2.19.0 paths above. Evidence remains under ignored TestResults/release-2.19.0/logs/precommit-01.

## 2.19.1 HTTP/1 patch release revalidation

Written acceptance covers only `GODMODERULES_IDDQD_God_Mode_Rule` on:

- `dist/EmbedIO-Neo.DependencyInjection.2.19.1-unpacked/lib/net10.0/EmbedIO.DependencyInjection.dll`
- `dist/EmbedIO-Neo.DependencyInjection.2.19.1-unpacked/lib/netstandard2.0/EmbedIO.DependencyInjection.dll`

DI source and scanner pins are unchanged since v2.19.0. The initial PR #588 CI
package was built from synthetic merge commit `3e098194e3c3a6ccb79bc0bf6fea62863125314c`,
whose tree equals reviewed head `00e5d5463041600ce1be23870e702c3851101376`, in
[run 41212888693](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/41212888693).
Pinned YARA-X 1.21.0 and YARA Forge 20261026 full rules were independently rerun
with printed strings in the existing digest-pinned .NET SDK 10.0.402-noble
container without network access and with a two-CPU limit. The rule in isolation
and the full bundle each match only `Injection.pdb` once per assembly, inside
compiler RSDS/CodeView debug-symbol filename metadata. No other rule or scanner
error occurred. The source rule and both pinned archive hashes were verified;
the rule condition is one matching string, not an assertion of executable behavior.

CI package SHA-256: `21849123d3b65085d6dcba3e658793a15689ee4e486ed7b49b5e74bddc736b4b`.

| Target | Assembly SHA-256 | Match offset |
| --- | --- | --- |
| net10.0 | `8edbce5473d3f8d506b73d8a146f85f66d50811bad0e806b4f94dc1cd301af09` | `0x6cbc` |
| netstandard2.0 | `ae34d98541db5b7cac7bc81446133a9c972d72a661458abde06ff93e6c18304d` | `0x75c7` |

These hashes identify the initial CI candidate, not future final-head or release
builds. Exact-byte review and full scans remain required for those builds.
Paginated open/fixed/dismissed GitHub code-scanning alert queries expose no YARA
alert to dismiss. William approved only the two version-qualified 2.19.1 paths
above and waived their unavailable matching-alert dismissal on 2026-10-31; no
dismissal is claimed and no other exception is granted. Other matches, scanner
errors and stale acceptances remain fatal. Private detailed evidence is retained
under ignored `TestResults/release-2.19.1/yara-review`.
