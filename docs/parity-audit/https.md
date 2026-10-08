# HTTPS parity

The managed listener serves the expected encrypted response when the test client
trusts exactly the generated localhost leaf certificate and retains hostname
validation. An ordinary client rejects that self-signed certificate, and a TLS
client rejects an incorrect hostname with the corresponding name-mismatch flag.
A healthy pinned request still succeeds after those rejected connections.
Cancellation completes with `Stopped`. These five outcomes agree across upstream
and both Neo assets on the tested Windows runtime.

The certificate has a private key, loopback/localhost SANs, server-auth usage and
PKCS#12 provisioning with a default key container. The first fixture attempt used
the generated certificate directly and failed the Windows handshake with an EOF;
the fixture was corrected to match the maintained Schannel provisioning approach.
That fixture failure is not claimed as an application regression.
No certificate is installed into an OS trust store, and no native
HTTP.sys binding is registered by this audit.

TLS-version/cipher policy, native HTTPS registration, client certificates,
certificate renewal/revocation, mobile WebView trust and physical devices are not
compared. The broader [MAUI HTTPS validation](../platforms/maui-https-validation.md)
remains separate evidence with its own pinned environments.

Evidence: `https/` cases and build/runtime metadata. See the [audit method](README.md).
