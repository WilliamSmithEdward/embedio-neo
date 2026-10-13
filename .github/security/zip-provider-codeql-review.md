# Zip provider CodeQL review

CodeQL 2.27.2 in Security run 38421579812 reports cs/zipslip at
`fullName = entry.FullName;` in ZipFileProvider. The reviewed SARIF paths cross
from this provider's MapUrlPath through MappedResourceInfo and FileModule into
FileSystemProvider.OpenFile (and its CLI wrapper), where a FileStream is opened.

That cross-provider edge cannot occur for the built-in FileModule: its Provider
is assigned in the constructor and has a getter only. Mapping, precompressed
variant lookup and opening all use that same provider instance. ZIP keys remain
archive keys: ZipFileProvider.OpenFile calls GetEntry(path).Open(), not FileStream
or any extraction API. The archive file constructor's FileStream uses the
application-supplied archive path, not an entry name. Custom providers remain
responsible for their own filesystem boundaries; this acceptance does not cover
them. Changing built-in provider identity or adding extraction requires review.

This restores the narrow acceptance originally reviewed in PR #161, removed
later when scans stopped emitting it. It now matches an actual current result;
the stale-entry gate remains enabled. Matching GitHub alert #9 was independently
queried and is dismissed as false positive by WilliamSmithEdward since
2026-10-08T01:26:47Z, with the same archive-key/immutable-provider explanation.
No new dismissal, rule exclusion or scanner configuration change was made.
The original failed run remains retained. A local report recheck uses its actual
C#/Actions/Semgrep SARIF; that is not a fresh scan or a green hosted check.

## Reviewed identities

- `src/EmbedIO/Files/ZipFileProvider.cs`: `51d711e3cf09152f83e3969a98e0b37b64f13de20c7083ca795746bb5ed2ea48`
- `src/EmbedIO/Files/FileModule.cs`: `46c7285777a41f130b1bf49adebb8ee89b2a5fdfd23d7936bd1f58f926834fec`
- `src/EmbedIO/Files/MappedResourceInfo.cs`: `6872f5243334b756d11ca0ddd33977a92e383fc660b41695ddc442fea6617add`
- `src/EmbedIO/Files/FileSystemProvider.cs`: `61908147aad87afba81335e09d53227daa561bf1408ad6758521085541be1cbc`
- `src/EmbedIO.Cli/CliFileProvider.cs`: `37994932ecbd412f4c8bcdd546bdfac2d07ec38e3eb9eae70353aa18fe9b43ac`
- Downloaded C# SARIF: `d3b21e6a0717213d6cb47180df44f13c82c87985e770dddd074256496c876ca6`
