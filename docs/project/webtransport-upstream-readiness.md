# WebTransport: upstream provider readiness

[Issue #303](https://github.com/WilliamSmithEdward/embedio-neo/issues/303)
tracks current-wire reliable reset and complete WebTransport integration.
William's decision is to keep the native candidate **test-only** and await
upstream support. No modified native library, experimental application facade,
or new provider is shipped by this decision. Existing HTTP/3 providers and
defaults are unchanged; current WebTransport support is not advertised.

## Why the preview setting is insufficient

The tested stock MsQuic versions, 2.5.10 and 2.6.2, use reliable-reset transport
parameter `0x17f7586d2cb570`. The independent quic-go 0.63.0 peer uses current
parameter `0x1d` (and recognizes a different legacy parameter). MsQuic 2.6.2
also uses preview frame type `0x21`, rather than current `RESET_STREAM_AT` type
`0x24`. Enabling a local setting does not establish compatible peer negotiation.

See the pinned [MsQuic transport parameters](https://github.com/microsoft/msquic/blob/v2.6.2/src/core/crypto_tls.c),
[frame definitions](https://github.com/microsoft/msquic/blob/v2.6.2/src/core/frame.h),
and [quic-go transport parameters](https://github.com/quic-go/quic-go/blob/v0.63.0/internal/wire/transport_parameters.go).
The checked upstream main commit
[`52d3fb9663e5baa8d4747f20897370b05fad7e4a`](https://github.com/microsoft/msquic/blob/52d3fb9663e5baa8d4747f20897370b05fad7e4a/src/core/crypto_tls.c)
still uses the preview parameter. The checked latest release is
[v2.6.2](https://github.com/microsoft/msquic/releases/tag/v2.6.2).
[Upstream PR #6024](https://github.com/microsoft/msquic/pull/6024) is closed
without merge; it is not an available receive-accounting fix.
These are pinned observations, not a promise about future releases.

Wire identifiers alone are insufficient: reset final size, retained prefix,
stream and connection flow-control accounting, and terminal ownership must all
match the [reliable-reset specification](https://datatracker.ietf.org/doc/html/draft-ietf-quic-reliable-stream-reset-11).
Full sessions must also satisfy the
[WebTransport over HTTP/3 specification](https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3-16).

## Preserved experiments and their limits

The isolated local prototype was reconciled onto accepted engine source,
including Linux module retention and request-timeout buffer ownership. Its
experiments use pinned test-only native builds, independent peers, ordinary TLS
trust plus certificate pins, and isolated loopback networking. All original
failed runs remain preserved. The prototype is not part of this documentation PR. These selections span
local checkpoints and overlap; they are not one final-head acceptance run.

| Experiment | Verified result | Limit |
| --- | --- | --- |
| Stock Linux reset control | 14 cases: 6 pass, 8 negotiation failures, zero skips | The required current-peer cases genuinely fail; no prefix exchange occurs |
| Same reset control with test-only native candidate | 14 pass, zero skips | Reset/header mechanism proof, not full session acceptance |
| Session and receive models | 167 pass, zero skips | Models are not native packet evidence |
| Actual native receive controls | 23 pass, zero skips | Sparse extent, held credit, closure and sibling checks; not complete application acceptance |
| Actual publication and adoption gap | 8 pass, zero skips after correction | Raw owner/quota/error mechanisms; no HTTP/3 SETTINGS/CONNECT proof in this fixture |
| Facade and control ownership | 197 pass, zero skips | Includes modeled boundaries; overlaps other selections |
| Unsupported/incoming stream boundaries | 12 pass, zero skips | Explicit test boundary inputs are not current-peer negotiation proof |
| Independent application peer | 10 pass, zero skips | Validates SETTINGS, CONNECT acceptance/rejection, cancellation, close/control faults and healthy siblings; no negotiated session flow-control acceptance |

The gap experiment exposed loss of an original initialization error during
native retirement. Its local correction preserves the exact error instead of
reporting only consequent cancellation. A stricter fixture reproduced three
failures before correction, then passed all eight cases. This correction remains
in the parked prototype; it is not represented as a shipped fix.

The tested native artifact is SHA-256
`5ec530bc2fbce77c72cf0af02250e1d4d7ef2b4c23138b5362b8bbb3e2079783`.
It contains separately licensed test-only changes and private observation
contracts. Its passing controls do not establish a production distribution,
Windows/macOS acceptance, complete session quotas, or browser interoperability.
Do not install it as an application workaround or advertise support from its
preview flags. Test-only source retains the MsQuic and quictls license notices.

## Criteria for reopening implementation

Keep #303 open for the concrete provider and integration gap. A future upstream
release must be verified against an independent current peer; a version number
or negotiation event alone is insufficient. Before enabling support, require:

- Current reset negotiation and real retained-prefix delivery after reset,
  including final-size consistency, offsets, cancellation and cleanup.
- Stream and connection credit accounting under sparse/duplicate delivery,
  session quotas, blocked credit, shutdown and healthy concurrent sessions.
- Actual HTTP/3 SETTINGS and CONNECT admission, early associated streams,
  datagrams, application authorization, capsules and terminal error identity.
- Windows, Linux and macOS interoperability and lifetime validation on the
  exact dependency artifacts, with capability checks bound to the live provider.
- Documented migration and dependency/license review, and every exact-head
  CI/security/malware check passing before integration.

No date or release commitment is made. Upstream readiness does not automatically
enable a provider or remove compatibility guards. When a suitable release is
available, recheck the applicable protocol specification and resume #303 with
these gates. [Upstream MsQuic #6376](https://github.com/microsoft/msquic/issues/6376) now
tracks the current-wire request; its related receiver issue remains #6224.
No recurring monitor is configured.
