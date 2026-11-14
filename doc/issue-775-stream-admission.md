# #775 per-call stream admission and physical ownership

Stage-five historical record. The subsequent [client-local generation slice](issue-775-client-local-modules.md)
adds local dynamic owners and repairs a deferred stream-head argument lifetime found by its
real-transport controls. The original 64 stream controls remain mandatory regressions.

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

### First hosted counterexample and correction

Subject08c362b02277d34cc3a6e8218d9c399c05b202d0 / run43549531784:
Windows passes all five inventories on both transports. Linux passes prior99/67/receipt49/
revisedfacts49 on both transports and reaches the outbound-generation stream case, then
fails: `Successful stream progress preceded RequestAccepted`. The stack is a consumer's
`SlowMoveNextAsync -> TryAcquireLocalAbortDelivery -> TryAcceptStreamData`, not receipt of
an actual StreamData frame. This shared delivery gate also arbitrates a legitimate local
wait before the first Ack. Requiring acceptance there was too early. Move only the protocol
check to the actual receive-loop StreamData boundary; keep pending terminal/deadline gating
for local consumer progress. No inventory, watchdog, GC budget or assertion is weakened.
Original ZIPs and both raw logs retained in streams-20261114 before the corrected run.

## Result: accept the bounded stream slice

Corrected subject **e1d6a46ed4a6e3e810221d52e101feeb3180488f** passed
[run 43549883083](https://github.com/SunSi12138/SharpLink/actions/runs/43549883083).

| Host / .NET 10.0.15 | TCP | SharedMemory | Job |
|---|---|---|---|
| Windows x64 | 99 + group67 + receipt49 + revised facts49 + streams64 | same five inventories | 132309635225 |
| Linux x64 | 99 + group67 + receipt49 + revised facts49 + streams64 | same five inventories | 132309635547 |

All 20 final JSONs pass the exact frozen inventory, uniqueness, count, source, transport
and report-kind checks, both hosted and offline. Both hosts pass ordinary/profile/plugin
basic builds, the precise old-Stub CS0535 negative consumer, and wrong-ABI preflight.
The first subject's 18 completed reports and Linux failure are also retained and checked;
there was no unchanged retry. The corrected run changes only the misplaced wire-progress
check and its explanation, not the frozen cases or budgets.

The new evidence establishes:

- Actual generated ClientStreaming, ServerStreaming and Duplex on both transports. Routes
  are installed before acceptance; held/queued/rejected/expired calls start no producer.
  A received Ack alone is insufficient: cancellation can still win the start claim.
  Duplicate and late real Acks cannot revive the completed request.
- Logical cancellation does not release a physical producer's original CTS. Held
  MoveNext/async Dispose block Client Stop; physical exit releases the bounded producer
  count. Stop also joins an in-flight original response Codec and reclaims its stream route.
- Server stream ownership promotes the already acquired module lease, without reacquiring
  a retired gate. Same-hash replacement waits for old input decode or output iterator
  disposal, then asynchronous service disposal. Original Codec output/result is observed;
  original call/stream counters reach zero and the old ALC collects with client/server live.
- The retained facts controls and active-source audit establish complete scalar facts and
  one cold resolver only. Static guard insertion preserves the five-family entry order;
  existing dynamic local wrapper timing is recorded, not silently normalized.

Private protocol identity is now **Magic 0xBB**, frame **244**, ABI
`sharplink-experimental-775-stream-admission-v5`. No additional public production declaration
or generated signature is introduced beyond the prior facts delta: the new acceptance bridge,
pending owner, frame and test hooks are internal/profile-only. API integer 5 is not a final
production version allocation. The profile remains nonpackable; ordinary product compilation
is unchanged (62 changed/shared source files pass exact-token/build-input guards against
2360a0ca). Keep the executed ordinary Unit result: Linux 2212/2212, Windows 2211/2212 with
the unresolved #387 Stop failure. It was not rerun or relabeled as passing.

**Next: client-local dynamic generation ownership, not PR/performance acceptance.** Preserve
its module-wrapper acquire/Codec/cancellation order and invocation-time policy/deadline
capture; prove both physical stream owners across local retirement before full integration.
OneWay input remains fail closed; its bounded count/discard grammar and formal capability
decision remain with #775. Broader cross-Session/malicious-frame and stream-specific
changed-hash/mapper/authorization/backpressure combinations still belong to final integration;
the retained changed-hash/ABA and mapper controls are not relabeled as these new combinations.
Actual package/API inventory, independent #805 signoff, Trim/NativeAOT, final canonical
regression and valid GitHub-only performance acceptance remain open. No PR/merge or new
performance run; no paused investigation reopened.

Complete source bundle, both subjects' four original ZIPs/four raw job logs, run metadata,
38-report inventory, frozen criteria and workflow snapshot are retained at
`C:/Users/sunsi/Desktop/SharpLink-775-evidence/streams-20261114/`. Development remains in
`C:/Users/sunsi/Desktop/SharpLink-775-Slice`, branch `codex/775-static-bind-slice`.
After capture, remove only this stage's temporary CI branch/workflow. The handoff differs
from the tested subject only in reports and workflow removal; historical runs remain.
