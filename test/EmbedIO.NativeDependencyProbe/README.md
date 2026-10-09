# Native dependency deployment fixture

This test-only package and consumer remain outside the ordinary solution and all
production packages. The package is built from an already validated native
candidate directory into a private local feed, with the fixed `0.0.0-local`
version. It is never uploaded to NuGet. The .NET 10 placeholder restricts this
fixture's compatibility; each private feed contains one osx-arm64 or osx-x64 native candidate.

The consumer project is generated from the template under ignored TestResults,
so its per-artifact lock and local-feed paths stay with the deployment evidence.
It reuses the independent BCL QUIC rebind probe. Run it from a normal
framework-dependent build and a publish for the host macOS RID with library-search/injection
overrides cleared. Record the actual loaded library and its hash alongside the
NuGet lock, dependency manifest, candidate receipt and native licenses. This checks
package selection and loader resolution; it does not establish all RID/platform
support, complete server interoperability, code signing, update policy or release
readiness. No application/library target or dependency group is changed.
The optional CI experiment runs separately on Apple Silicon and Intel macOS.
It derives the RID from the actual host, checks the matrix expectation, includes
that RID in the receipt and uses it for package paths, restore and publish.
Package verification checks each actual Mach-O CPU type and dylib header in
addition to hashes, exact RID assets, notices and receipt equality. A relabelled
arm64 binary therefore cannot pass as osx-x64. The Intel runtime/deployment result
is pending until the corresponding CI job succeeds; adding the matrix is not
validation evidence. Ordinary production packages and dependencies are unchanged.

The header constants are checked against Apple's [Mach-O loader definitions](https://github.com/apple-oss-distributions/xnu/blob/main/EXTERNAL_HEADERS/mach-o/loader.h)
and [CPU type definitions](https://github.com/apple-oss-distributions/xnu/blob/main/osfmk/mach/machine.h).
Each deployment run also mutates the real package: relabelled RID, wrong CPU,
invalid magic, executable rather than dylib, truncated header, extra RID, missing
alias and altered notice must all be rejected for the expected reason. Temporary
mutants stay inside the named evidence directory and are removed; rejection
receipts remain. These are package-boundary checks, not Intel execution evidence.
