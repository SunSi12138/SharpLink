# Current-dev physical output tail baseline — frozen V1

Product subject: dev `2c77ffb55b10c111abea8fac0c128c1ac2dc93ad`. No production delta.
Temporary audit fixtures only; no PR, acceptance gate, C/D repair, allocation or throughput work.

## Source decision before the run

Current OneWay-input is **NO for the old C/D synchronous-throw gap**: current
`SharpLinkServer.Interceptors.cs` captures stable InputStreamDrain before invocation;
both dynamic Singleton catch and Call lease catch forward synchronous failure to
`CompleteDynamicOneWayInputAsync`. That helper awaits the captured physical child
drain before ServiceLease/module disposal. `StreamManager.InputStreamDrain.cs` closes
admission, abandons all captured mailboxes, and awaits their physical child completion.
This is the #984 compensation absent in C/D. No OneWay RPC experiment is needed.

Output remains a specific source-level hypothesis: generated Runtime pump detaches
MoveNext/send/async Dispose at deadline. Its observers only await/catch. Current
Server bridge awaits logical pump only; dynamic singleton/Call lease cleanup joins
input but has no output physical join. Task reachability is not an independent module lease.

## Exactly four cases, once each

TUnit `RuntimeAssemblyIntegrationTests.PublicDeadlineShouldRetainPhysicalOutputTail`:

1. `(Server, MoveNext)`
2. `(Server, Dispose)`
3. `(Duplex, MoveNext)`
4. `(Duplex, Dispose)`

These exact discovered case names, rather than a partial-class selector, define
inventory=4. Discovery may run without executing tests; execution runs once with
maximum parallelism 1 and original platform timeout 120s. No focused OneWay,
ALC collection stress, other test, repetition or replacement sample is authorized.

One ubuntu-24.04 TCP job, hard15min; SDK10.0.115, runtime10.0.15 must exist and actual
test runtime is recorded. Build<=480s, selected test process<=150s (platform120s).
NuGet audit and warnings-as-errors remain unchanged.

## Public entry and deterministic cut

Existing DynamicHarness TCP Connect/manifest handshake and PluginBundle are reused.
New generated contract/service/custom Codec live only in existing test plugin projects.
Public RegisterAssembly registers actual contract and service assemblies at both peers.
A healthy generated Warmup stream must serialize and deserialize one item through
the real custom codec. Then public generated Server/Duplex proxy invokes held user
IAsyncEnumerator. Duplex input is a finite empty public producer, not another held tail.

The client public TimeProvider has frozen timestamp/UTC; the server uses unchanged
System TimeProvider and **the transmitted two-second request budget**. This isolates
server deadline from client expiry/cancellation: client own monotonic deadline never
expires. No production callback, fake writer/session, injected owner, sleep, timer
fault, changed server grace or hidden registration is used.

Held MoveNext is cancellation-ignoring user code. On deadline its Dispose completes
synchronously in MoveNext mode. In Dispose mode Dispose releases pending MoveNext
and then waits its own gate, isolating the disposal tail. Each mode must show the
other phase exited before classification. All gates are released only in finally,
after the user has already received DeadlineExceeded and unregister observation.

Existing finite phase watchdogs:5s to enter/terminal/disposal/physical exit;
UnregisterAssemblyAsync uses unchanged2s grace bounded externally5s; existing
WaitUntilAsync3s observes eventual Released only after gates release. No waiting for
success replaces pre-release observations. No salvage/reacquisition. Finally performs
normal public contract cleanup and existing harness Stop/Dispose only.

## Required observations and classification

Buffer and print JSON only after cleanup. Record user-gate held state, each phase,
logical exception, original module state/calls/streams, full unregister result,
service disposal, actual invocation/linked-enumerator CTS cancellation and `_disposed`
(reflection reads only), actual Codec created/serialize/deserialize/dispose counters,
existing framework/deferred-task diagnostics, elapsed times, runtime and source.

**YES per cell** only if healthy public prereqs succeed, server DeadlineExceeded reaches
client before gate release, selected MoveNext or Dispose remains physically active,
and original module reports released/zero protection or service is disposed while
held, followed by successful physical exit and module/resource convergence after release.
The test intentionally fails at `PHYSICAL_OUTPUT_TAIL_V1` only after preserving all
these observations. Codec is contract-owned, not service-module-owned: report its
retention/disposal separately; do not infer it was prematurely disposed from service release.

**NO per cell** if all phases captured, physical ownership remains through original2s
grace, and release afterwards converges. **INCONCLUSIVE** for missing guard, codec
warmup failure, wrong logical terminal, timeout/cleanup failure, discovery mismatch,
runtime mismatch, or missing raw evidence. A passing workflow is not the oracle.
Two cells sharing source do not prove unexecuted send-tail behavior.

Stop after this single job. Preserve all failures and raw/HTML/JSON/environment/source
material. A tool defect requires explicit new tool revision and parent decision,
not a silent rerun. No product fix this turn. After terminal artifact offline readback,
delete only this temporary ref with exact lease; preserve its commit and local evidence.
