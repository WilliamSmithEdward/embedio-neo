# Cleanup after committed response completion

The Windows job in PR225 run38034652027 failed `DrainPreservesRequestsStillWaitingInTheListenerQueue(True)` during `MultiplexedResponse.CloseCoreAsync`: the request token was canceled after the fixed-length HTTP/2 response had already committed END_STREAM. This is distinct from the client connection-refusal queue investigated in PR225.

Two controlled HTTP/2 cases reproduce the cleanup fault before the correction. Each writes all three declared bytes, then cancels either the context token or the exchange token before closing the context. The independent client receives the complete body, while the observed server completion faults. The fixture joins and inspects server completion directly, so successful client receipt cannot conceal a cleanup assertion failure.

The correction skips cancellation validation only when final headers and the transport's final response end have already committed. No further response write is needed. Context callbacks and resource cleanup still run, and connection-close intent is retained. A response that has not ended still checks cancellation before sending headers or completing its body.

The two new cases and all six existing queue-drain/incomplete-response cancellation cases pass locally. Existing incomplete-response cases retain their assertion that the client cannot receive a successful completed response. No timeout, assertion, retry, quarantine, dependency, target or public API was changed. The discovery floor increases from4625 to4627; exact-head hosted and broader protocol checks remain required before integration.

The same response owner serves HTTP/2 and HTTP/3. The captured failure and deterministic reproductions establish the HTTP/2 path; HTTP/3 validation must remain explicit rather than inferred from the shared class alone. This change is not a native QUIC lifetime correction or a release.
