# Listener retirement: explicit test-case accounting

Owner-authorized Neo v2 removal of the Microsoft and inherited Mono implementations. This is an incremental ledger; it is not full retirement acceptance.

The SDK C# parser inspected each method attribute. The 291 Microsoft rows below each have an identical EmbedIO row in the same method. Only the obsolete attributes were removed; managed arguments, assertions, deadlines and iteration counts remain unchanged. The single unsupported-drain case is replaced by early rejection of retired numeric mode 1. Three additional cases guard the persisted identifiers 0, 2 and 3.

Native-wrapper-only fixtures, Boolean backend matrices, samples, platform apps and the independent Mono replacement still require separate accounting. No discovery floor has been lowered; reconcile it from actual full reports after test migration compiles.

| Fixture | Retired Microsoft rows with exact managed peers |
| --- | ---: |
| BrotliResponseTest.cs | 2 |
| ColdStartListenerRegressionTest.cs | 3 |
| HeadResponseTest.cs | 5 |
| HttpApplicationLifecycleAuditTest.cs | 1 |
| Issues/Issue105_NativeWebSocketShutdown.cs | 7 |
| Issues/Issue129_Customization.cs | 12 |
| Issues/Issue163_TypedRouteValidation.cs | 18 |
| Issues/Issue170_SuffixRanges.cs | 1 |
| Issues/Issue318_StartupDeadlock.cs | 1 |
| Issues/Issue438_ClientBanning.cs | 13 |
| Issues/Issue438_PermanentBans.cs | 1 |
| Issues/Issue439_BasicAuthenticationRegistration.cs | 2 |
| Issues/Issue457_ProxyDisconnects.cs | 10 |
| Issues/Issue464_EndpointConfiguration.cs | 6 |
| Issues/Issue479_SameSiteCookies.cs | 21 |
| Issues/Issue490_SpaRoutes.cs | 10 |
| Issues/Issue495_BenchmarkEndpoints.cs | 12 |
| Issues/Issue502_LargeMessages.cs | 4 |
| Issues/Issue505_BinaryResponses.cs | 8 |
| Issues/Issue510_ChunkedStreaming.cs | 4 |
| Issues/Issue521_RouteCase.cs | 5 |
| Issues/Issue524_BasicAuthentication.cs | 2 |
| Issues/Issue534_WebSocketSends.cs | 2 |
| Issues/Issue545_IPBanningIsolation.cs | 2 |
| Issues/Issue547_MessageCallbacks.cs | 14 |
| Issues/Issue556_EarlyWebSocketMessages.cs | 11 |
| Issues/Issue558_UnreadRequestBodies.cs | 12 |
| Issues/Issue564_HeadResponses.cs | 6 |
| Issues/Issue566_IdentityContentEncoding.cs | 6 |
| Issues/Issue567_ResponseCharset.cs | 9 |
| Issues/Issue570_ControllerConcurrency.cs | 6 |
| Issues/Issue573_ZipReadOnly.cs | 2 |
| Issues/Issue574_LargeResponses.cs | 2 |
| Issues/Issue583_WebSocketCookies.cs | 1 |
| Issues/Issue587_ProgressEvents.cs | 2 |
| Issues/Issue588_StreamingClose.cs | 5 |
| Issues/Issue59_CookieAttributes.cs | 9 |
| Issues/Issue64_WebSocketResponseCookies.cs | 6 |
| JsonUtf8ResponseTest.cs | 4 |
| ListenerAdapterTest.cs | 9 |
| ManagedWebSocketHardeningTest.cs | 2 |
| NativeWebSocketShutdownTest.cs | 1 |
| QueryFormatPolicyTest.cs | 7 |
| QueryPreconditionWireTest.cs | 1 |
| QueryRangeWireTest.cs | 2 |
| QueryRoutingTest.cs | 5 |
| RepresentationRangeWireTest.cs | 2 |
| RequestCodingChainTest.cs | 15 |

Total: 291 paired backend rows in 48 source files. The managed counterpart of every removed row remains. The original inventory is retained under ignored TestResults/retirement-test-audit/cases.json.

The runtime-specific graceful-drain case is recorded separately: GracefulDrainValidationTest.UnsupportedListenersDoNotPretendToDrain(Microsoft) becomes RetiredMicrosoftModeIsRejectedBeforeListenerCreation. This checks the supported v2 configuration contract rather than continuing to instantiate a removed backend.

## Paired iteration sources

Removed only the Microsoft iteration from 14 explicit two-mode arrays. These are iterations inside existing tests, not removed NUnit cases. The retained EmbedIO iteration and its assertions are unchanged.

| Fixture | Paired arrays changed |
| --- | ---: |
| BrotliResponseTest.cs | 1 |
| RequestCodingChainTest.cs | 3 |
| Issues/Issue163_TypedRouteValidation.cs | 1 |
| Issues/Issue170_SuffixRanges.cs | 2 |
| Issues/Issue564_HeadResponses.cs | 2 |
| Issues/Issue566_IdentityContentEncoding.cs | 2 |
| Issues/Issue567_ResponseCharset.cs | 1 |
| Issues/Issue574_LargeResponses.cs | 1 |
| Issues/Issue575_XmlResponses.cs | 1 |

## Adapter-specific retirement

These 43 cases instantiate or specifically inspect internal adapters that no
longer exist. They are retired with those implementations; they are not relabeled
as managed cases or added to the retained engine's coverage count.

| Fixture | Cases retired | Removed implementation exercised |
| --- | ---: | --- |
| NativeResponseDisposalTest | 6 | SystemResponseStream header preparation/disposal wrapper |
| Issue534_NativeSends | 6 | SystemWebSocket send/close gates over a controlled runtime socket |
| Issue105_NativeWebSocketShutdown | 13 | SystemWebSocket controlled shutdown and validation; its seven real managed cases remain |
| Issue502_CloseAdmission | 5 | SystemWebSocket and ProcessSystemContext admission/receive paths |
| Issue547_MessageCallbacks | 9 | Private SystemWebSocket.MapCloseStatus; shared module cases remain |
| Issue59_CookieAttributes | 4 | Native Set-Cookie2/version-one cloning and implicit native scope flags |

Native cookie version-one cloning and implicit scope flags were properties of
the removed backend. Do not claim equivalent support from the retained listener;
applications should set the intended cookie scope explicitly and validate their
wire contract during v2 migration. Shared cookie flag, handshake and rejection
coverage remains in this fixture.

## Ported shared contracts

Five formerly Microsoft-only cases now exercise the retained engine: pre-canceled
accept (one), no HTTP serialization after an upgrade during shutdown (one),
chunked uploads with optional body consumption (two), and rejected writes that
must not commit cookie headers (one). Their assertions and per-case deadlines
are unchanged. Dead Microsoft-only platform skips and exception allowances are
removed; all surviving managed assertions and simultaneous-close iterations
remain in place.

The ordinary solution builds with zero warnings. The nine directly affected
fixtures report 76 focused cases on Windows, all passed, none skipped. This is
focused evidence; full-suite discovery, Linux/macOS, standalone tools and every
final-head hosted check remain outstanding. The discovery floor is unchanged
until the full supported suite is actually accounted and reported.
