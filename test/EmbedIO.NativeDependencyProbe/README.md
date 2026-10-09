# Native dependency deployment fixture

This test-only package and consumer remain outside the ordinary solution and all
production packages. The package is built from an already validated native
candidate directory into a private local feed, with the fixed `0.0.0-local`
version. It is never uploaded to NuGet. The .NET 10 placeholder restricts this
fixture's compatibility; it contains only the osx-arm64 native candidate.

The consumer project is generated from the template under ignored TestResults,
so its per-artifact lock and local-feed paths stay with the deployment evidence.
It reuses the independent BCL QUIC rebind probe. Run it from a normal
framework-dependent build and an osx-arm64 publish with library-search/injection
overrides cleared. Record the actual loaded library and its hash alongside the
NuGet lock, dependency manifest, candidate receipt and native licenses. This checks
package selection and loader resolution; it does not establish all RID/platform
support, complete server interoperability, code signing, update policy or release
readiness. No application/library target or dependency group is changed.