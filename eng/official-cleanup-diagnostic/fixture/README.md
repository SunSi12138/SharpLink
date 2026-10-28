# Untimed cleanup diagnostic fixture — preparation only

CLI: `PhysicalTcpProbe.dll CONDITION OUTPUT`, where CONDITION is `static-none`,
`static-cancellable`, or `dynamic-none`. The parent protocol schedules each once
for each own-package/own-generator arm (six processes); this fixture has no
warmup, measurement, throughput, latency-sampling, or retry entry point.

The original O203D1 evidence is not modified. The item-1 first call and original
cold scenarios 1–4 retain their ordered-content checks, remote errors,
cancellation, early break, and 15-second cleanup gate. The only behavioral
control is labelled `static-cancellable`: case 4 receives a fresh CTS token that
is never explicitly cancelled. The original `static-none` still passes None.
The original complete twelve-check cold gate is not replaced by this focused
cause diagnostic, and a successful diagnostic control does not pass O203D1.

Both contracts and business bodies are common across arms. `CorrectnessAsync`
uses a non-iterator forwarding method only to expose its original argument token
before the private async iterator applies EnumeratorCancellation linking. The
original producer body and finally counters remain, with observer callbacks
before waiting and on token cancellation/finally. This is explicitly fixture
instrumentation, not an untouched-binary performance measurement.

No transport wrapper, relay, added server interceptor, telemetry listener,
packet capture, privilege, or product modification is used. The existing client
pass-through interceptor records its incoming token. Wire flags and Cancel
payloads are **not observed** and are labelled source-inferred, never reported
as captured frames. Actual server call-state Reason is recorded separately; it
is not presented as a wire payload observation.

OUTPUT.stages.jsonl is flushed at each ordered receipt. It records Started,
Completed, Active, clientLogical; client pending/physical call/stream ownership;
server active call/admission ownership; connection ownership; and the current
last-accepted request's call-state projection. The server registry's existing
SnapshotOwned and cancellation map's existing stripe-locked TryCapture are used
read-only. Projections contain values only, not retained pooled call-state
references. Service argument/effective tokens are distinguished and compared by
CancellationToken.Equals against actual connection and per-call invocation
tokens. Token source hash identities are diagnostic labels only, not equality
proof. A CanBeCanceled=true service token alone does not prove remote-cancel
ownership: a connection token may also be cancellable.

On the unchanged gate's first failure, write the failure receipt before
server-stop cleanup and retain it in final JSON even if stopping the server
subsequently runs the producer finally. Bounded teardown is recorded separately.
The process exits nonzero for a gate, observation, or teardown failure; observers
never manufacture a pass. Startup failure before observers exist remains a
nonzero process failure in the parent's retained host log.

Build properties remain LibraryRoot, GeneratorPath and Arm=OFFICIAL|DEV. Both
projects must compile separately against their own five package DLLs and SDK
Generator. API4/API5, source, protocol, generator and ABI checks remain positive.
No execution is authorized by the presence of these files: exact-tool review and
root release remain required.
