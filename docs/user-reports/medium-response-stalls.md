# Medium-sized response stalls

[Upstream #574](https://github.com/unosquare/embedio/issues/574) reported delayed 1–6 MB JavaScript responses and subsequent small requests. lucapivato also observed delays with action modules and the Microsoft listener; cho-trackman reported a similar symptom with JSON. The original application's complete request sequence, runtime and logs were not supplied.

## Confirmed correction

The managed listener's response stream inherited `Stream.WriteAsync`, which scheduled synchronous transport writes on thread-pool workers. A slow reader could hold a worker inside socket I/O; cancellation could not interrupt the synchronous write already in progress. Neo now awaits the transport's asynchronous writes directly and forwards the caller's cancellation token. `FlushAsync` retains the existing no-op flush semantics without scheduling a worker, and honors pre-cancellation.

Controlled transport tests fail on the previous implementation and verify no synchronous writes during asynchronous output, pending writes under backpressure, cancellation and the existing `IgnoreWriteExceptions` policy. Requested cancellation propagates even when write exceptions are ignored. Writes on one response must still be awaited in order; concurrent writes to the same response are not supported.

Two adjacent framing defects are also corrected for synchronous and asynchronous writes: empty writes no longer emit a zero-sized chunk that prematurely terminates the body, and headers larger than the initial 16 KB coalescing budget no longer produce a negative body-copy count. Ordinary fixed-length/chunked framing and end-of-response trailers are preserved.

This correction is unreleased. It is not present in NuGet version 1.0.1. Public APIs, supported target frameworks, listener selection, compression and caching defaults, and dependencies are unchanged. Synchronous `Write`/`Dispose`, buffered response disposal and compression finalization can still perform synchronous work; this change does not make every response operation asynchronous. The Microsoft listener already delegates async writes to .NET and is not replaced by this correction.

## Validation and limits

Regression coverage exercises both listeners with 1 MB and 6 MB files, identity/gzip/deflate encoding, caching enabled/disabled, repeated keep-alive requests, following small requests, HEAD and byte ranges. Separate cases cover concurrent 6 MB action/JSON responses, buffered and streaming responses, and a deliberately unread managed socket while another client requests health information. Controlled tests provide the deterministic cancellation/backpressure evidence; real-socket checks exercise the integration.

The reporter's attached ZIP was downloaded for local testing, not imported into the library or committed as test source. Its JavaScript file is 6,282,317 bytes, SHA-256 `c2868026fdcf5ec607c4a4c64c200dfff528231c095e646a9e53bcc04d89a9b5`. On the local Windows/.NET 10.0.11 test host, the six combinations of two listeners and three encodings completed in approximately 54–85 ms; immediately following small requests completed in approximately 1–2 ms. Decoded file bytes matched the original in every case. These single local observations are not a portable performance guarantee or a before/after speedup measurement.

The original 25-second application stall, MAUI code-page exception, and thread-pool exhaustion across all modules are not independently reproduced. The confirmed async-write defect is fixed; a remaining application-specific stall needs a minimal reproduction rather than assuming the same cause.

## Investigating a remaining stall

1. Record the exact Neo package version, OS/runtime, listener mode and hosting model. Include the server configuration and surrounding request timeline; redact credentials and private payloads.
2. Compare request-header completion, handler entry, body-transfer completion and the next small request. A context's age includes time before module dispatch, so a short handler duration does not by itself locate the delay.
3. Compare identity and compression, cold and warm cache requests, and the two listener modes where available. For a local server, `curl --max-time 10 -H "Accept-Encoding: identity" -o response.bin http://127.0.0.1:5000/test.js` bounds the observation. Use `curl --compressed -H "Accept-Encoding: gzip"` to decode a compressed response before comparing bytes. Compare complete bodies, not just HTTP 200 headers.
4. Await asynchronous response writes and give application-owned writes an appropriate cancellation token. Do not use `.Wait()` or `.Result` inside request handlers, or assume increasing thread-pool limits repairs blocking I/O. `IHttpContext.CancellationToken` tracks server lifetime; remote disconnect is not a guaranteed cancellation signal. See [streaming response closure](streaming-response-close.md).
5. Share a redistributable sample file or generated equivalent and a complete runnable reproduction in the [fork issue](https://github.com/WilliamSmithEdward/embedio-neo/issues/83). Browser request order and whether the client consumes the response are useful evidence. A body-size cap can limit symptoms but is not a general repair.
