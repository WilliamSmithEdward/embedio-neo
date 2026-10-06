# AppContainer fixture YARA review

The malware gate in [run 37524741545](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37524741545)
reported `SIGNATURE_BASE_Powershell_Case_Anomaly` in the test-only
`test/EmbedIO.AppContainerSmoke/Program.cs`. ClamAV reported zero matches.
This review supports one rule/path acceptance; it does not disable scanning.

## Evidence inspected

The tool and full-rule archives were downloaded and verified against the SHA-256
pins in `.github/security/yara.json`: YARA-X 1.20.0 and YARA Forge full 20261004.
The underlying [Florian Roth rule](https://github.com/Neo23x0/signature-base/blob/94a1c48d7ab499879287ff611dfe7f9c56376030/yara/gen_case_anomalies.yar#L11-L60)
was read in full. It compares case-insensitive occurrence counts against allowed
spellings and looks for unusual command casing; it does not detect what an
operation actually does.

Using the pinned binary with `--print-strings` against exact Git bytes from
commit `7facf1874941dd69100f2b24f307981ed05a1b13` reproduced the full-bundle match.
The four `$s1` offsets were `0x78b`, `0x79b`, `0xfb4`, `0xfc4`: the normal
mixed-case system directory name and normal lowercase executable name, each
appearing twice in the source's two explicit tool invocations. The `-NoProfile`
arguments matched their own permitted spelling at `0x7ad` and `0xfd6`.
There is no random-case command, encoded payload, downloaded script or execution
policy bypass. The printed full-bundle result did not report the lowercase
allowlist matches; the isolated rule did not alert on the same bytes. We do not
claim a confirmed scanner engine defect from that observation alone.

The source was reviewed as executable logic, not only as strings. Profile,
ACL and network-policy operations are guarded for this repository's disposable
GitHub runners. The firewall allowance names only the unique test app SID,
TCP port 59654 and local/remote loopback addresses. The fixture creates the
profile itself, removes its outbound allowance, terminates its inbound session,
removes its firewall rule and ACL grant, stops its child and deletes the profile.
It changes no production package, global firewall setting or machine-wide
execution policy. Its observed blocked/allowed/restored phases passed in
[Windows run 37524587734](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37524587734).

## Acceptance boundary

Only this rule on this exact source path is accepted. Future matches in any
other file or rule, tool errors and stale acceptances remain failures. Re-review
if the fixture accepts user-supplied commands or identities, loses its CI guard,
changes its permission scope, or the pinned rules/tool change. The raw archives,
matched-string output and reviewed source snapshot remain under ignored
`TestResults/upstream-554/yara-tools`.
