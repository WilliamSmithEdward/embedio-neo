# HTTP/2 output lifetime during graceful drain

This development correction belongs to program #181 and is not a release.
It preserves the GOAWAY cutoff, admitted-response contract, public API, target
frameworks, timeout settings and forced-cancellation behavior.

## Captured failure

PR #234 at 2601cca failed its Windows regression job in run 38045844162,
job 114195092463. `DrainUnderConcurrentLoadCompletesEveryAdmittedRequest(True)`
recorded 221 handler entries but only 214 successful client responses. All 4802
cases were reported; this was a real assertion failure, not lost discovery.

The unchanged production engine from e733402 reproduced the loss on iteration
49 of a controlled repeated run. Request-identity diagnostics recorded 230
handler entries and 230 completed handler writes, but 217 delivered responses.
There were no duplicate handler entries. The failed client requests included
socket error 10053 in `Http2Connection.ProcessIncomingFramesAsync`, including
requests whose bodies were being buffered. The full diagnostic log is retained
under ignored `TestResults/http2-drain`; it was not replaced with a passing rerun.

## Controlled lifetime defect

The dispatcher previously used one cancellation source for both input and
shared output. After GOAWAY and the last admitted response, it canceled that
source. A queued or committed refusal of a later stream could still own the
output flusher at that instant.

`GracefulDrainDoesNotCancelCommittedRefusalAfterTheLastResponse` makes this
ordering explicit: admit stream 1, send GOAWAY with cutoff 1, write its final
response, queue and commit the refusal of stream 3, then let the last application
finish while that refusal write is held. On unchanged production code the
committed write's cancellation token is canceled. This case fails before the fix
and passes after it without changing the asserted ordering or frame bytes.

Normal drain now stops input separately. It waits for application cleanup and
the credit pump, then queues an output barrier behind any remaining controls.
Forced stop, connection failure and the existing drain deadline still cancel
shared output. The additional cancellation source belongs to the connection;
there is no additional per-request token link or timer.

This preserves admitted work during the shutdown described in
[RFC 9113 section 6.8](https://www.rfc-editor.org/rfc/rfc9113.html#section-6.8).
The controlled case proves the cancellation defect; no packet capture establishes
which exact control bytes were lost in the original hosted failure.

## Validation and limits

The corrected source passes the controlled case and all five existing external
drain cases. A subsequent 100-iteration HTTP/2 drain campaign passes the original
response-count equality and all existing timeouts, with no retries or quarantine.
It uses the same diagnostic fixture and driver as the failing baseline campaign.
The driver invokes the actual fixture, validates every response body and waits
for its original port-refusal assertion; this is not a throughput benchmark.

Final combined-source acceptance on development base 54f4c5c passes: 4805
Windows cases, 4800 passed, five existing skips, zero failures, in 3m 20s. The
493 selected HTTP/2/admission cases pass on pinned Linux using the same
Windows-built IL; all six focused drain cases pass with the actual .NET Standard
2.0 asset on the .NET 10 Windows host. Locked restore, warning-free builds for
both core targets, syntax/suppression guards and changed-source whitespace
verification pass. Documentation links resolve. All hosted final-head checks
remain required. Discovery is 4805: the 4804 native-foundation base plus one
controlled drain case. This does not establish .NET Framework runtime cancellation.

The final fixture retains its original `HttpClient.GetStringAsync` operation.
Request IDs travel in a diagnostic query parameter, rather than replacing that
operation with a manually buffered SendAsync/ReadAsStringAsync pair as the first
diagnostic campaign did. Completed handler writes, missing responses, duplicate
handler entries and terminal client exceptions appear in failure messages. The
original equality assertion, status/body checks and timeouts remain. Final-source
acceptance also passes 100 iterations with this retained client operation. This
second campaign is reported separately from the initial manually buffered
client campaign, rather than pooling samples from different client operations.
