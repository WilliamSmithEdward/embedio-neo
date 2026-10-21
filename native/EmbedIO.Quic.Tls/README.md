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

Public HTTP/3 provider selection remains unchanged. Native runtime packaging,
older libc/musl support, all RID validation and the final provider transition are
still development work; these sources do not add a runtime package dependency.
See [the development record](../../docs/project/quic-development-and-testing.md).

## Issuer chains and ownership checks

Private ABI 2 adds ordered public issuer certificates separately from the leaf's
PKCS#12 credential. The managed adapter uses normal runtime issuer discovery,
keeps a partial chain's final issuer, omits the trust anchor and releases its
chain copies. An older ABI fails preflight before listener readiness. This does
not change public provider selection or client-certificate trust policy.

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
