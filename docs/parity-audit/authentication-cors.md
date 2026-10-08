# Authentication and CORS parity

Both listeners and Neo assets agree on Basic authentication: valid credentials
return 200, missing/wrong/malformed credentials return 401, and the tested
`WWW-Authenticate` realm/charset remains identical. This is an in-memory fixture
account, not evidence of password-storage or identity-provider security.

CORS preflight status and allowed origin/method/header fields agree. An allowed
origin receives the expected origin header; a different origin receives no
allow-origin header while the ordinary request still returns 200. That result
illustrates browser-enforced CORS behavior and is not an authentication boundary.

The only observed common response-header difference is the existing omission of
identity content encoding, described under [static files](static-files.md).
Digest/token authentication, custom verifiers, credential Unicode boundaries,
origin wildcards/case handling and security adversarial tests are outside this
probe. Evidence: `auth-*` and `cors-*` HTTP cases; see the [audit method](README.md).
