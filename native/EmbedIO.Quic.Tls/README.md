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
