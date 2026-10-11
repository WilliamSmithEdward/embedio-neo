# Shared HTTP/1 listener shutdown

Issue #312 investigates macOS resets in `Issue595_TwoServers.StaticAndApiServersSurviveRepeatedConcurrentTrafficAndIndependentShutdown(True)` from PR #309. The frontend and API listeners use separate prefixes on one physical endpoint. After the API listener is disposed, the next frontend request sometimes fails with a connection reset. Both hosted attempts failed; the routing tests themselves passed.

## Confirmed ownership defect

A completed HTTP/1 connection retains its last logical listener while waiting for another request. Stopping that listener previously closed the idle transport even when the physical endpoint still served another listener. Independent raw TCP and TLS peers reproduce this on Windows without connection-pool retries: eight of twenty initial controls fail against the unchanged core, while final-owner, unread-body and unfinished-response controls pass.

The correction returns eligible idle transports to the shared endpoint's pending ownership set under its admission gate. Final endpoint shutdown seals that gate and releases the returned transports. Logical listener retirement removes its association with the connection; later requests can bind to another live listener. A body-free response that has completed but whose actor has not yet transitioned to the next request is eligible too.

Unfinished responses, unread request bodies, tunnels and draining/closing connections retain forced cleanup. This does not treat client receipt of the declared response length as proof that server response completion has occurred. Ownership checks also apply to detached pending contexts, so a stale logical request cannot forcibly close a transport already handed to another owner.

## Validation and limits

Twenty-eight focused cases pass locally across TCP/TLS, Stop/Dispose/graceful drain, idle/response-completion boundaries active surviving-owner handoff and final-owner/unfinished-response/unread-body cleanup. Shared cases issue the next request on the original connection and check its remote port; a fresh-connection retry cannot conceal closure. The existing two-server cases pass unchanged locally. Exact-head full-suite and hosted macOS validation remain required before acceptance; local results do not establish the precise timing of the captured macOS failure.

No public API, default, target, dependency, timer, retry policy, coverage setting or CI deadline changes. The discovery floor changes only to account for the added regression cases. This change targets the PR #182 integration branch and is not a release.

The initial full Windows coverage run passed all 4,744 cases (zero failures, 10 expected skips) before reconciliation with the updated integration base. The reconciled solution builds both targets with zero warnings/errors; all 28 final focused controls pass. Final-source full coverage and hosted platform checks remain pending.
