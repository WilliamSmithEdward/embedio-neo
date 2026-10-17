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
