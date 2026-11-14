# #775 per-call stream admission and physical ownership

Base:8d2c052c9cdd6d4cf810c36220ec3bedfc549541, accepted facts/receiver ABI slice.
This stage enables ClientStreaming, ServerStreaming and Duplex on the existing scoped
receiver. Ordinary no-stream Unary/OneWay keep their existing path. OneWay client streams
remain fail closed until an independently bounded wire count/discard grammar is integrated.

## Acceptance is per request, not per binding

Use one private RequestAccepted control frame, zero payload, flagsNone, nonzero request ID,
on the same physical Session. The existing pending call owns its acknowledgement, original
deadline, cancellation and producer-start claim; there is no second request registry.
Ack completes only an asynchronous signal. Starting a producer rechecks that exact pending
generation under its completion gate. Cancellation/deadline before that claim means zero
producer entry; a start that wins may require physical cleanup after logical cancellation.
Late/duplicate Acks cannot resurrect a completed call or start twice. Unknown/future,
unpublished or non-stream correlations are protocol errors, without sparse-ID allocation.

Server admission obtains the exact original module protection before collectible facts,
promotes that same protection to stream accounting, reserves call/stream resources and
receiving routes, and then publishes acceptance immediately before the generated Stub.
Authentication, activation and interceptors that reject before reaching that terminal
produce no Ack. Publication shares the server call's terminal gate and bounded normal
send queue. Business decode/handler failure after acceptance is an established-call failure.

The original registration/facts/Codec remains protected through invocation, active input
decode and asynchronous service/iterator disposal. Pending-call logical completion cancels
but does not dispose a producer CTS while physical producer code still owns it. Framework
tasks and physical connection activity retain that owner through Stop/Drain and transport
cleanup. Client-local dynamic registration remains a separate migration; this slice verifies
fixed local Codec owners and retired server registrations across both stream directions.

## Guard and regression contracts

Keep Unary/OneWay and static/module-wrapper precedence documented in the facts ABI report.
ClientStreaming uses Codec-null → scoped identity/shape → caller cancellation → one policy/
control capture. Server/Duplex retain invocation-time capture and Resolved Codec/progress
checks. Unsupported OneWay-stream and invalid family/descriptor combinations still fail
before producers. No producer executes inline in the receive loop.

Keep the99/67/receipt49 inventories unchanged. The earlier facts49 inventory has explicit
unsupported-stream controls that cannot remain the contract once those shapes are enabled:
replace only those obsolete expectations with the corresponding Codec/identity/cancellation
guard checks, record exact old/new names before the first run, and retain every other facts
control. Do not count previous fail-closed successes as enabled-stream acceptance.

## Frozen before hosted execution

Windows/Linux x64 × real TCP/SharedMemory. All RPC, generated runtime and GC/lifecycle
execution is GitHub-only; locally develop and run basic builds/source checks only.
Retain prior watchdogs and ten-GC/live-owner ALC conditions. Add an independent exact stream
inventory and process watchdog before its first Action. No performance run is scheduled.

Required cuts: held acceptance; queued/rejected/expired/canceled pre-Ack calls; Ack versus
client terminal/start claim; duplicate/late/cross-call correlation; asynchronous user
producer; cancellation and Stop while producer/consumer cleanup remains physically held;
server same/changed generation retirement with old inbound decode and outbound iterator;
original facts/Codec and mapper identity; final ALC/quotas/routes reclaimed. Admission or
physical-owner mismatch, premature CTS/module release, producer entry after a lost start
claim, changed guard order or failed retained control stops the subject. Preserve its
failure evidence and repair a concrete cause before running a new subject; no retry-to-green
or relaxed budgets. Final #775/#805 canonical/package/AOT/performance gates stay open.

### Exact facts49 migration (before first Action)

- `facts unsupported ClientStreaming remains asynchronous fault at invocation` → `facts ClientStreaming Codec null precedes identity and cancellation`
- `facts unsupported ClientStreaming preserves fail closed priority` → `facts ClientStreaming scoped identity precedes cancellation`
- `facts unsupported ServerStreaming rejects before enumeration` → `facts ServerStreaming Codec null remains at resolved invocation`
- `facts unsupported Duplex rejects before enumeration` → `facts Duplex Codec null remains at resolved invocation`
- `facts unsupported streaming starts no producer` → `facts rejected streaming guards start no producer`

The other44 facts cases and the99/67/receipt49 inventories are unchanged.

### Frozen subject and gate

Before the first hosted run: `64` exact new cases in
`eng/775-slice/streams-expected-cases.json`; new process watchdog180s.
Existing99/67/receipt49 keep60s/120s/120s, revised facts49 keeps120s.
Both OS jobs have20min hard limits; each synchronous Codec cut15s, replacement5s,
ALC collection at most10 full-GC iterations with clients/server kept live.
No timing assertion or inventory/budget will be weakened after a failure.
The Action verifies exact names, uniqueness, transport, report kind and tested SHA.
A failure stops that subject's cell (other OS continues to preserve independent evidence);
retain every original run before correction. No automatic retries, PR or merge.

New coverage includes all three real generated stream shapes; held/queued/rejected/expired
acceptance; real duplicate and late Ack; cancellation between Ack and start claim; fixed
local producer/consumer physical Stop; old dynamic inbound Codec and outbound iterator/Codec
through retirement, service disposal and ALC. Authentication uses the existing pre-terminal
policy cut (not a new authentication implementation). Client-local dynamic streaming and
OneWay input grammar remain unimplemented. The changed-hash/ABA/receipt controls from prior
stages remain mandatory regressions, not newly claimed stream-generation coverage.
