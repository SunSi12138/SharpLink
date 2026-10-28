# Fenced read-mostly Signal: publication argument and scope

Experimental branch: `codex/research-wakeup-fenced-write-elision`, base `f724c7aa`.
This is a design argument awaiting independent review, not a performance result or a machine-checked complete .NET proof.

## Change and unchanged authorities

Only when the first `_state` read is exactly `Latched` (1), execute `Interlocked.MemoryBarrier()`, reread `_state`, and return if the second value is also exactly 1. Otherwise execute the original arm-claim / OR-latch / crossing-recheck algorithm using the fresh value. A successful full-fence read does **not** replace the publication fence with an acquire load. Timed arms and `arm | Latched` values are never eligible for elision. `_core`, timer ownership, generation increments, continuation storage/clearing, and scheduling/context flags are untouched.

Signal is a coalescing readiness notification, not a counting semaphore. Multiple publishers observing one extant latch may share that notification; their actual state remains in mailboxes/stop/policy publication. Removing the write preserves that abstraction only if consumer checks cannot miss all the publications whose signals were coalesced.

## Relevant ordering contract

.NET's documented model grants full-fence semantics to Interlocked operations (including Interlocked.MemoryBarrier) and Thread.MemoryBarrier. Volatile Read is acquire, Volatile Write is release, and Monitor.Exit is release; these alone are insufficient for a producer Store→Load publication check. No argument here assumes a volatile read magically obtains the latest wall-clock value. No argument depends on Monitor.Exit being stronger on a particular processor.

Primary reference: https://github.com/dotnet/runtime/blob/v10.0.1/docs/design/specs/Memory-model.md

## Forbidden cycle (Dekker/store-buffer shape)

Let Wq be a publisher's mailbox/stop/policy write, Rp its **post-fence** latch read, Cs the consumer's Interlocked clear/consume of the particular latch Rp observes, and Rq the consumer's subsequent relevant publication check. Consider the bad execution in which Rp sees the pre-Cs latch while Rq sees the pre-Wq publication.

* Producer full fence imposes Wq → Rp.
* Observing the latch before its clearing gives the same-location read-before-overwrite (from-read/coherence) edge Rp → Cs; it is not a claim of freshness.
* Consumer's Interlocked clear gives Cs → Rq.
* Missing Wq gives the corresponding publication-location old-read edge Rq → Wq.

Together they form Wq → Rp → Cs → Rq → Wq. This is the store-buffer/Dekker outcome disallowed with full fences on both participating publication/check paths. An unfenced producer lacks Wq → Rp and admits exactly this outcome. A second read is essential: placing the fence after the old first read and returning without rereading still lacks the required Wq → Rp edge for the deciding read.

Queue publication is represented by its concurrent queue's publication location, not by arbitrary unsynchronized payload data. Mailbox storage itself must remain safely published by Channel; Signal is not a replacement for the queue's memory-safety contract.

This argument must be validated against the exact target JIT/ISA: x64 TSO store buffering with a full fence; ARM64 coherent normal memory with the required full barrier (or an equivalently strong implementation). The Python model covers a TSO-style store buffer, not the entire ARM memory model. A passing model does not certify emitted code.

## Consumer transitions

1. **Ordinary idle wait:** RunAsync checks both queues and `_stopped`, then WaitAsync uses Interlocked.Exchange to install an arm. If it consumes the observed latch, it synchronously completes true and the pump loops to recheck publication. If a fresh arm is visible to producer's deciding read, the producer does not elide and retains the original claim/crossing logic. A stale value of a consumed latch plus a missed relevant queue publication is the forbidden cycle above, not a permitted “we assume freshness” case.
2. **Timed batch coalescing:** WaitForMoreUntilFlushBoundaryAsync calls ConsumeLatched (Interlocked.CompareExchange), then rechecks stop, both mailboxes, and the flush-policy identity before installing a timed wait. Those rechecks must not be removed or moved above ConsumeLatched. They give Cs→Rq. If the old latch is not actually present, the CAS does not consume a notification and the producer's original arm/latch path remains responsible for a later wait.
3. **Generation reuse/ABA:** Exactly-1 can recur across generations. The deciding read need not be the same latch the first read saw; use the actual latch write observed by the deciding read in the cycle argument. Every consumption of an exact-1 latch leads to the rechecks above. Elision never claims a numbered arm, so it does not add an ABA completion channel.
4. **Timed deadline wins:** If timer CAS clears an armed token, a late producer with fresh zero follows the old latch path. If its read observes a real exact-1 latch, that readiness already represents outstanding work; the same consumer recheck obligation applies. No new timer operation or token comparison is introduced.
5. **Already completed wait:** The consumer does not Reset `_core`/arm a new generation until its previous result has been consumed. Existing single-consumer rules continue to govern this; Signal elision touches neither result nor continuation fields.

## Every production Signal caller

| Caller | Publication before Signal | Consumer observation / requirement |
|---|---|---|
| SendPump.TryEnqueue / EnqueueAsync successful ordinary/progress send | Channel.Writer.TryWrite(frame) completes | HasNormalFrames/HasProgressFrames or TryRead; concurrent queue publication contract remains intact |
| EnqueueWhenFull final synchronous reserve succeeds | Same Channel publication | Same; full fence follows the completed publication |
| ResumePendingEnqueues accepted pending node | Channel publication under admission gate; node Accepted/Frame ownership transition precedes later Signal | Same; no frame moved back to producer after publish; existing cleanup/completion ordering unchanged |
| Flush-policy changed callback | Volatile.Write immutable `_current` before callback | Capture() acquire read after ConsumeLatched; identity check detects a change before arm |
| Stop | Interlocked `_stopped=1`, both Channel writers completed | `_stopped` read after latch clear or after wake; terminal queue drain remains physical owner |
| ReportFaultOnce | Interlocked `_faulted`, `_stopped=1`, writers completed with error | Same stop/drain path; original exception delivery unchanged |

There are no other production WakeupSignal instances/callers at this source snapshot. Tests exercising standalone Signal are still coalescing-notification tests; a caller wanting signal counts would already violate the current abstraction.

## Linearization and continuation ownership

For an elided signal, the post-fence read of exactly1 is a valid linearization point within an existing ready epoch. Original idle/arm paths retain their existing atomics as linearization points. If a consumer or another producer changes `_state` after that read, the publication/check cycle prevents both sides from losing the work. No extra writer can claim a completion merely because it elided an idempotent latch write. The original losing-claim path still sets a real latch (TwoWritersCannotDoubleCompleteOneArm must continue to pass).

OnCompleted's context-aware path, global-queue preference, trampoline-only inline execution and Execute's clear-before-invoke sequence are byte-for-byte unchanged. The optimization does not make a consumer run on the producer, change execution context, reorder frames, choose priorities, change flush policy, or discharge physical ownership early.

## Specific negative controls / required checks

* Unfenced exactly-1 return: rejected; model has a lost-wake terminal trace.
* Fence after first read but no reread: also rejected; the deciding read still precedes publication fence.
* Arm-or-latch low-bit test instead of equality1: rejected; it could skip an armed-generation claim and strand a waiter.
* Assume Channel producer lock is a portable full fence: rejected; use explicit ordering contract.
* Assume stress tests on x64 prove ARM: rejected.

Required independent review: verify this graph for both consumer modes, all caller publication mechanisms, repeated latch generations, and emitted fence instructions. Product performance acceptance remains blocked pending that review and fixed contended/uncontended cost evidence. No runtime timing has been run.

## Narrow codegen spelling correction

First correctness commit `2e2c77d` used Thread.MemoryBarrier. Its actual x64 Instrumented Tier0 body contained a wrapper call, not an exposed fence instruction (592-byte Signal body). It passed all 88 focused tests and both guards, but did not establish the underlying emitted fence. The candidate now calls Interlocked.MemoryBarrier directly: the current runtime primary source marks this as an intrinsic; its full-fence contract is unchanged. This is a codegen hypothesis to validate, not a claim that an instruction or cost has already been measured. The original output is retained at artifacts/attempts/001-thread-wrapper.

References: https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked.memorybarrier?view=net-10.0 and https://github.com/dotnet/dotnet/blob/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/runtime/src/libraries/System.Private.CoreLib/src/System/Threading/Interlocked.cs
