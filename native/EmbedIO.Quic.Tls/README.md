# EmbedIO.Quic TLS boundary

This original native boundary supplies TLS 1.3 through OpenSSL's public recordless
TLS API. Packet processing, recovery, congestion control and stream ownership stay
in EmbedIO.Quic. The private ABI uses owned handles and bounded queues; it does
not expose OpenSSL objects or native callbacks to managed code. Managed application
policy handles certificate trust, usage, expiry and revocation before admission.

The build pins OpenSSL 3.5.9 and verifies its source archive SHA-256 before safe
extraction. OpenSSL is statically linked with its QUIC engine, shared modules,
dynamic module loading and automatic system configuration loading disabled. Only
the private EmbedIO ABI is exported. Linux uses RELRO, immediate binding and a
non-executable stack. The output includes OpenSSL's Apache-2.0 license and a build
receipt with source/output hashes and the native configuration. Original EmbedIO
boundary source is covered by the repository MIT license.

## Development build

Use Python 3.12+, Perl, make, ar, curl and a C11 compiler on Linux x64/ARM64 or macOS
x64/ARM64. Builds use fresh work/output directories and never install system files.
The host toolchain determines the resulting OS/libc baseline; a successful local
build does not establish compatibility with older systems or a distributable RID.

```sh
python3 native/EmbedIO.Quic.Tls/build.py --work TestResults/quic-native-work --output TestResults/quic-native --jobs 2
# --archive /path/to/openssl-3.5.9.tar.gz permits an offline build with the same hash check.
dotnet publish test/EmbedIO.Quic/EmbedIO.Quic.csproj -c Release -o TestResults/quic-host -p:EmbedIOQuicNativeTlsDirectory="$PWD/TestResults/quic-native"
python3 test/EmbedIO.Quic/verify.py --dotnet dotnet --assembly TestResults/quic-host/EmbedIO.Quic.Development.dll --openssl portable-rsa --output TestResults/quic-native-wire
```

Install the hash-locked independent peer from
`test/EmbedIO.Conformance/drivers/requirements.txt` in an isolated Python environment.
`verify_sni.py`, `verify_tls_listener.py`, `verify_client_certificate.py` and
`fuzz_client_certificate.py` exercise the same production TLS factory on Unix.
The portable CI job builds this boundary and runs those checks on Linux and macOS.
Configured jobs are not validation evidence until their exact-source runs pass.

The ordinary HTTP/3 binding selects EmbedIO.Quic; packages through 2.16.1 retain
their previous provider. Runtime assets are built into the asset-only
`EmbedIO-Neo.Quic.Native` package, first published with 2.17.0; the modern core depends on
it, and .NET Standard dependencies remain unchanged. CI and Publish include all six
runtime assets, and CI runs the package consumers on each of the six runtime
identifiers. They passed once on macOS 14, and CI also runs them on the oldest
glibc and musl that .NET 10 supports ([Oldest glibc and musl](#oldest-glibc-and-musl)).
CI also runs them as ad-hoc signed hardened-runtime macOS apps, Native AOT and JIT
([Hardened-runtime macOS apps](../../docs/project/quic-development-and-testing.md#hardened-runtime-macos-apps));
signing with a Developer ID is not validated.
See [the development record](../../docs/project/quic-development-and-testing.md).

## Issuer chains and ownership checks

Private ABI 2 adds ordered public issuer certificates separately from the leaf's
PKCS#12 credential. The managed adapter uses normal runtime issuer discovery,
keeps a partial chain's final issuer, omits the trust anchor and releases its
chain copies. An older ABI fails preflight before listener readiness. This does
not choose the public provider or change client-certificate trust policy.

`verify_server_chain.py` exercises a BCL TCP control and the actual QUIC factory
with complete, partial, two-intermediate, direct-root and self-signed profiles,
including exact wire issuer order and root omission. It requires Linux and
`EMBEDIO_QUIC_DISPOSABLE_CERTIFICATE_STORE=1`; use only a disposable container or
CI user profile. It adds public certificates only to the temporary current-user
CA store and removes its entries afterward. It never adds a trusted root.

The standalone Linux ownership test also checks 32 issuer entries, excess and
malformed input, allocation cleanup and native error retirement. After the native
build above, use the pinned independent-peer Python environment:

```sh
python3 native/EmbedIO.Quic.Tls/tests/create_fixtures.py TestResults/quic-owned-fixtures
work=TestResults/quic-native-work
cc -std=c11 -O1 -g -fsanitize=address,undefined -fno-sanitize-recover=all -fno-omit-frame-pointer -I"$work/objects/include" -I"$work/openssl-3.5.9/include" native/EmbedIO.Quic.Tls/native.c native/EmbedIO.Quic.Tls/tests/ownership.c "$work/objects/libssl.a" "$work/objects/libcrypto.a" -Wl,--exclude-libs,ALL -ldl -pthread -o TestResults/quic-owned-fixtures/ownership
ASAN_OPTIONS=detect_leaks=1:halt_on_error=1 TestResults/quic-owned-fixtures/ownership TestResults/quic-owned-fixtures/credential.pfx TestResults/quic-owned-fixtures/certificate.der
```

This instruments the boundary/test code and checks allocator leaks; the ordinary
vendor archives and assembler are not compiler-instrumented by this command.

## Runtime asset preparation

`build.py --rid <rid>` requests a distribution identity. The build runs `audit.py`
without loading the native code and records the result and tool/source hashes in
`binary-audit.json` and `build-receipt.json`. Linux checks the actual ELF64
architecture, exact ABI-2 symbol versions, system-only dependencies, SONAME,
non-executable stack, RELRO and immediate binding. Requested glibc RIDs reject
symbol requirements newer than 2.27. A development build without `--rid` records
its actual glibc requirement and must not be treated as a distribution artifact.
macOS builds target 14.0 to avoid imposing a newer native loading requirement
during replacement. The build checks the actual Mach-O deployment, architecture,
exports and dependency metadata. Musl metadata cannot prove runtime compatibility
by itself; each supported RID still requires real execution and package testing.

Linux x64 CI uses the pinned compiler-only `Dockerfile.manylinux-x64`. Its
OpenSSL configuration and link commands stay TLS-only. No image Python/Perl or
compiler dependency is shipped in the resulting library. To reproduce its build
from a Linux shell with Docker:

```sh
docker build -f native/EmbedIO.Quic.Tls/Dockerfile.manylinux-x64 -t embedio-quic-native-build native/EmbedIO.Quic.Tls
mkdir -p TestResults
docker run --rm --cpus 3 --memory 6g --mount "type=bind,source=$PWD/native/EmbedIO.Quic.Tls,target=/source,readonly" --mount "type=bind,source=$PWD/TestResults,target=/results" embedio-quic-native-build bash /source/build-linux.sh linux-x64
python3 -m unittest discover -s native/EmbedIO.Quic.Tls/tests -p 'test_*.py'
```

Use fresh output directories. Native binaries, their license, binary audit and
source/output receipt are retained by the portable workflow. These are inputs to
runtime-asset packaging. The separate package described below supplies development
NuGet runtime assets. The ordinary provider transition is described above. See the development record for exact tests,
platform scope and remaining work.

## Package assembly and actual consumers

`EmbedIO.Quic.Native.csproj` builds the unpublished `EmbedIO-Neo.Quic.Native`
package outside the ordinary solution. It contains only native runtime assets,
build/audit records, a manifest and license/readme/icon files. It contains no
managed implementation, build targets or package dependencies. The managed
transport remains compiled into the modern core asset; Windows uses Schannel.
The native package is separate so the .NET Standard dependency groups and legacy
Framework AnyCPU behavior remain unchanged. The modern core's project reference
emits a NuGet runtime-package dependency without using a managed native-wrapper
assembly. Package consumers reference only `EmbedIO-Neo`; their generated locks
must show the exact native version as transitive, and actual output bytes must
match the sealed runtime package. RID-specific publishing selects the applicable
asset; portable output retains the runtime directories needed for runtime selection.
See [NuGet's native asset selection and Framework guidance](https://learn.microsoft.com/en-us/nuget/create-packages/native-files-in-net-packages).

Install `requirements.txt` in the isolated build/validation Python environment
with `python3 -m pip --isolated install --require-hashes --only-binary=:all: -r native/EmbedIO.Quic.Tls/requirements.txt`.
Its XML parser pin matches the existing formatter toolchain and is not shipped in
any runtime package.

`stage.py stage` requires exactly six audited outputs by default: `linux-x64`,
`linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64` and `osx-arm64`.
It checks their archive/source identity, ABI/header/RID, audited baseline, license
and output hashes before writing a fresh staging directory. Source hashes accept
only the current files or their CRLF-to-LF normalization for cross-platform
checkouts. The manifest and receipts establish consistency, not build attestation
or runtime/platform acceptance. The package target verifies that staging inventory
before packing. `stage.py package` compares the actual ZIP payload paths/bytes,
notices, package identity and license with the staged inputs.

The reusable `quic-native.yml` workflow builds the six assets on matching GitHub
hardware. Ordinary CI shares those same-run artifacts between Unix regression
tests, complete package assembly and `quic-portable.yml`; it does not dispatch a
second native matrix. The standalone portable workflow still builds its own inputs.
The shared `quic-runtime` action checks and stages the complete source-matched
inventory before packing. Publish input assembly uses it too, including dry runs,
and checks seven packages. No tag or publication is implied by these configured jobs.
The portable workflow seals the native/core hashes and source commit, then runs
`test/EmbedIO.Quic.PackageConsumer/validate.py` on each RID.
Compiler and musl validation images use immutable digests; musl validation uses
the pinned .NET SDK with Native AOT tools and exact signed Python APK packages.
The native build and consumer matrices each allow at most two concurrent jobs.
Ordinary source-build tests opt in to the same native artifact through
`EmbedIOQuicNativeTlsDirectory`. `stage.py asset --output <artifact-directory>
--rids <executing-rid>` verifies its source, hashes and RID before CI enables the
shared test-only copy target. Package-mode builds reject that manual copy hook.
Configured matrix entries are not evidence of successful runs.

The consumer uses a fresh NuGet cache and source mapping to isolate local previews
from released packages with the same development version. Portable, RID-specific
and Native AOT publishing must select the exact package library without a manual
copy. Every mode runs all three ciphers through real HTTP/3, generated controllers,
bearer authorization, bounded upload/rejection, request-abort cancellation, healthy
subsequent requests and owned cleanup. The full development host also runs wire,
cipher, SNI, live publication, certificate-policy, bounded mutation and issuer-chain
suites through package references. The reflection-based full harness is not an
AOT consumer; the focused generated-controller application is.

For a local single-RID reproduction, staging accepts explicit `--rids <rid>`,
packing requires `-p:EmbedIOQuicPreviewRid=<rid>`, feed sealing requires
`--preview-rid <rid>` and the consumer requires `--preview`. These flags identify
an incomplete development preview and are absent from distribution CI. Ordinary
package validation requires all six RIDs. No preview is published by this workflow.
Developer ID signing remains unvalidated. Runtime results on all six RIDs, the oldest
glibc and musl, macOS 14 and ad-hoc signed hardened-runtime apps, the malware scan of the
seven-package payload, browser sessions and endurance runs are recorded in the
development record, and the earlier providers are removed.

## Oldest glibc and musl

.NET 10's own floors are glibc 2.27 on x64 and Arm64, from Ubuntu 18.04, and musl
1.2.3, from Alpine 3.17 ([.NET 10 supported OS versions](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md#libc)).
The glibc builds need no symbol newer than 2.27, which the audit checks, so .NET's
floors are the oldest systems the package can run on. The portable workflow's
`floor` job checks them on each Linux RID:

1. `validate.py --consumers-only` builds and checks the portable, RID-specific and
   Native AOT consumers on the runner, as the portable job does, and
   `--peer-directory` keeps the independent peer's environment.
2. `validate_floor.py` starts every consumer's server in a container of
   `Dockerfile.floor-glibc` (Ubuntu 18.04, glibc 2.27) or `Dockerfile.floor-musl`
   (Alpine 3.17, musl 1.2.3). The images hold only the OS packages .NET needs. The
   script copies the one runtime the pinned SDK bundles and mounts it read-only, so
   the server runs on the runtime that built it. The peer stays on the host and
   reaches the server over the shared host network. Each consumer runs all three
   cipher profiles and the missing-runtime control, and the script checks the libc
   version the image reports.
3. A Native AOT binary runs only on the glibc it was built against or a newer one
   ([Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)),
   so on glibc the script publishes the AOT consumer again in the image's `build`
   stage, on Ubuntu 18.04 with its own clang, and runs that binary. The musl AOT
   consumer built on the validation image's current Alpine runs on Alpine 3.17
   unchanged.

The floor images are pinned by digest and package version and stay outside
Dependabot, because they move only when .NET's floors do. To reproduce the glibc
floor on a Linux host with Docker, after building the feed:

```sh
python3 test/EmbedIO.Quic.PackageConsumer/validate.py --rid linux-x64 --feed TestResults/quic-runtime-feed --output TestResults/quic-floor-consumers --consumers-only --peer-directory "$PWD/TestResults/quic-floor-peer"
docker build -f native/EmbedIO.Quic.Tls/Dockerfile.floor-glibc --target runtime -t embedio-quic-floor native/EmbedIO.Quic.Tls
docker build -f native/EmbedIO.Quic.Tls/Dockerfile.floor-glibc --target build -t embedio-quic-floor-build native/EmbedIO.Quic.Tls
python3 test/EmbedIO.Quic.PackageConsumer/validate_floor.py --rid linux-x64 --feed TestResults/quic-runtime-feed --consumers TestResults/quic-floor-consumers --image embedio-quic-floor --build-image embedio-quic-floor-build --dotnet-root "$(dirname "$(readlink -f "$(command -v dotnet)")")" --peer "$PWD/TestResults/quic-floor-peer/bin/python" --output TestResults/quic-floor-validation
```

The musl floor builds its consumers in `Dockerfile.musl-validation` and takes that
image's runtime, as the `floor` job in `quic-portable.yml` shows.


## Intel macOS test peer

The pinned cryptography 50.0.2 dependency publishes no Intel macOS wheel. Its
version remains unchanged. On that native builder, `build_peer_wheel.py` uses the
same reviewed OpenSSL build's static SDK to prepare a separate test-only wheel
from the exact pinned source archive. Rust 1.99.1, Cargo's unchanged locked
packages and `peer-wheel-requirements.txt` constrain its build tools. The script
verifies archive paths, wheel identity/RECORD, license retention, native module
architecture/dependencies and the actual OpenSSL backend version. No QUIC engine
implementation is taken from these dependencies.

The dedicated `quic-peer-wheel-osx-x64` artifact is not part of the runtime feed
or native package. The package consumer accepts it only for Intel macOS and checks
its workflow source, builder source, archive/version pins, wheel hash and native
build-receipt identity against the runtime package. It installs that specific
hashed wheel, then restores the unchanged full peer lock. The static TLS-only
build disables automatic legacy-provider loading in this test environment.
Source-building does not establish upstream platform support; the real Intel
consumer still has to pass its complete protocol, policy and AOT suites.

To reproduce in the same fresh Intel macOS build environment, after `build.py`:

```sh
python3 native/EmbedIO.Quic.Tls/build_peer_wheel.py --native-work "$RUNNER_TEMP/embedio-quic-native" --native-output TestResults/quic-native --work "$RUNNER_TEMP/embedio-quic-peer" --output TestResults/quic-peer-wheel --source-commit "$(git rev-parse HEAD)"
python3 test/EmbedIO.Quic.PackageConsumer/validate.py --rid osx-x64 --feed TestResults/quic-runtime-feed --peer-wheel-directory TestResults/quic-peer-wheel --output TestResults/quic-package-validation
```

Musl consumers run Docker as the checkout owner's UID/GID with disposable writable
HOME and .NET CLI directories. This retains Git's ownership validation and leaves
artifact files readable by the runner; it does not add a safe-directory exception.


The test-wheel audit reads the module's own install name with `otool -D` before
checking `otool -L` imports. The leading ID row describes the module itself, as
[Apple explains](https://developer.apple.com/forums/thread/736719); it is not an
external library load. Every actual load still must be an absolute canonical
system path, and dynamic OpenSSL remains disallowed. Both raw listings are saved
before validation so a rejected dependency remains inspectable in build logs.
The Intel source wheel compiled in CI 39662188674, but its earlier audit failed
without retaining that listing. The corrected audit and complete Intel consumer
run still require new-head hosted validation.
