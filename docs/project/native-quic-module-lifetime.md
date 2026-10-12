# Standalone native QUIC module lifetime

Issue [#307](https://github.com/WilliamSmithEdward/embedio-neo/issues/307)
tracks Linux process teardown after native-only QUIC use. This correction is
scoped to the internal native provider on the v2 integration branch.

## Reproduction

On unpatched integration source `a47857d6`, the native-only independent datagram
fixtures complete both echo and unsupported-datagram negotiations and report two
passing cases. The child test process then exits with SIGSEGV/139 after its summary;
the Microsoft.Testing.Platform supervisor reports exit code 7. The JSON crash report
is retained. A separate native HTTP/3 application-datagram control passes and exits
normally, so this is not claimed to affect every native workload.

The reproduction uses network-isolated image
`sha256:46f173c7a38ab18c682bd071376c9eb2b1350cc9b202a48b52ec38748427e0c3`,
.NET 10.0.12, MsQuic 2.6.2 and aioquic 1.3.0. Loopback remains available.
The original session fixture from the broader unaccepted WebTransport branch is
not imported: it requires APIs that are not on the integration base. Its historical
successful workload/crashing-process evidence remains preserved separately. An
initial attempt to run that fixture here failed at its missing controller type
and also crashed during process exit; that setup failure is not a successful
session reproduction.

## Correction and ownership

The first native API open retains one additional Linux module reference per loaded
EmbedIO engine assembly. A lock serializes initialization. Each ordinary API owner
still calls MsQuicClose and frees its own library reference. Child registrations,
configurations, listeners, connections and streams retain their existing closure
and parent-lease semantics. This preserves their resource cleanup while preventing
the last module mapping from unloading before CLR/thread teardown completes.
Windows and macOS behavior is unchanged.

An API function table and the OS library mapping have different lifetimes;
[MsQuicClose](https://microsoft.github.io/msquic/msquicdocs/docs/api/MsQuicClose.html)
releases the table, while
[NativeLibrary.Load](https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.nativelibrary.load)
returns a loader handle. The retained mapping is intentional. Do not free it in a
ProcessExit handler: that would reintroduce unloading before all native/CLR threads
finish. No patched MsQuic binary or new runtime package is introduced.

## Acceptance and tradeoff

The focused Linux controls pass after correction and the process exits normally.
They cover independent native datagrams, HTTP/3 application datagrams, raw streams
with empty/1 KiB/1 MiB exact-byte and FIN checks, and 513 repeated
API/registration/configuration lifecycles with parent-first and child-first closure
and invalid-configuration cleanup, plus eight collectible managed load contexts.
Every tested SafeHandle closes; module mappings remain unchanged and all eight
managed contexts become collectible.

One loader reference is retained per loaded engine assembly, including copies in
collectible contexts. Collecting such a context does not free that native reference.
Repeated ordinary lifecycles in one assembly do not add anchors; repeatedly creating
new assembly contexts adds loader references to the same mapping. Stable mappings
and collectible managed contexts do not prove a bound on every native allocation
or loader reference count. Applications that require complete native unloading
should use process isolation until the underlying teardown is understood and fixed.

Collectibility is checked in a standalone test-only program that uses the ordinary
core build. The first coverage run of the original NUnit context case failed because
[Coverlet 10.1.0's tracker](https://github.com/coverlet-coverage/coverlet/blob/v10.1.0/src/coverlet.core/Instrumentation/ModuleTrackerTemplate.cs)
registers process-wide event handlers that retain instrumented modules. Moving this
assertion into a required standalone program avoids that external root. No library
coverage filters or collection assertions are disabled; the complete coverage suite
still runs against the normal test output.

CI runs the seven-case native-only subset in three fresh processes on each desktop
platform before the complete coverage suite. The independent peer and MsQuic are
pinned by existing setup. A test summary alone is insufficient: each process must
exit successfully. The full discovery floor adds six new NUnit cases and a required standalone context check; existing coverage,
deadlines and the separate raw-runtime rebind quarantine are preserved.
Final-head repository gates must pass before integration. The issue records actual hosted outcomes; configuring jobs alone does not establish platform acceptance.

The captured crash-report stack includes CLR signal handling and does not identify
the original native faulting callback. No precise MsQuic/OpenSSL teardown mechanism
or upstream fix is claimed. Reconsider retention only after a supported dependency
version passes the same unpinned fresh-process workloads; no automatic version-based
retirement is implemented. No main merge or release is implied by this integration.
