# #735 integrated review: correctness fixes, performance still blocked

## Evidence and decision

Measured baseline: `0fe26024b114bb6e78411a9b86276086c045d03d`.
Measured candidate: `885306c66ee2e61c6fdd400bc52e2143aa48d6db`.
Workflow: Resolved Flow State Performance Evidence, run `37190672744`.
Artifact: `11299511677`, SHA256
`227b679f62c8fefd821714f39b7f0db251d577e9e9fe06418c55d51ab54d5ad8`.

**Acceptance verdict for that candidate: NOT GO.** The positive CPU results answer
whether lookup removal has value, but do not satisfy the simultaneous allocation
and NativeAOT constraints. The successful workflow establishes complete evidence,
not that these constraints pass. No CI job, timeout, population or threshold is
relaxed by this follow-up.

Microkernel percentages are candidate-internal keyed versus resolved API
**ns/item improvement**, not revision throughput gains. A negative value means
resolved latency is higher; -100.82% means approximately 2.0082x keyed latency,
not a physically impossible throughput decrease greater than 100%.

| Integrated microkernel control | ns/item improvement |
|---|---:|
| NativeAOT send no-wait | +18.35% |
| NativeAOT receive pair | +18.60% |
| NativeAOT short-stream | **-11.28%** |
| NativeAOT send contention | **-64.59%** |
| NativeAOT receive-pair contention | **-100.82%** |
| JIT PGO ON receive-pair contention | **-120.32%** |

E2E below is the actual pinned-baseline versus candidate binary comparison:

| Transport / E2E scenario | Throughput | Allocated bytes/item delta |
|---|---:|---:|
| TCP Server1x16 | -5.26% | **+73.44** |
| SharedMemory Server1x16 | -2.82% | **+54.24** |
| TCP median of six 100-item scenarios | +4.30% | see per-scenario raw reports |
| SharedMemory median of six 100-item scenarios | -1.61% | see per-scenario raw reports |

The short-stream allocation increase repeats across all three reports for both
transports. It is not dismissed as hosted-runner timing noise. Historical results
remain archived but are not current acceptance evidence.

## Fixed per-route cost control

A separate local CoreCLR attribution probe uses the same stateless dispatcher,
streamId 0, no flow callbacks, and 10,000 Register/Unregister lifecycles per repeat.
It runs three repeats after warmup against the integrated dev source and the
follow-up candidate. Results are 208/208/208 B per lifecycle for dev and
256/256/256 B for the candidate: **+48 B/route**. An independent allocation-only
probe of DispatcherEntry is consistent with its growth from roughly 48 to 96
bytes. The common entry layout is unchanged by the correctness fixes below.

This is a mechanism-level control, not replacement E2E evidence. It supports a
fixed route-footprint contribution, but does not claim to explain every byte of
the +73.44/+54.24 E2E deltas. The full saved receive lease and additional lifecycle
bookkeeping are per-route storage; the remaining allocation and short NativeAOT
regressions still require remediation and fresh comparable evidence.

## P1: acquired DATA must retain receive ownership

A route acquisition pins more than dispatcher object lifetime. It also pins the
receive generation and consumption callback from before AcceptReceived until
the dispatch has consumed or transferred that frame's ownership.

Previously synchronous request cleanup could publish terminal and clear the
callback before an acquired dispatch ran. Pausing after the debit reproduced
lost consumed credit. Pausing before the debit reproduced a closed/stale lease.
Both are deterministic; no sleep or scheduler probability is required.

The corrected synchronous request path completes mailboxes immediately, so
terminal/discard behavior can make progress, but does not release route keys,
publish receive terminal, or clear callbacks while acquisitions remain. A cold,
one-shot drain continuation performs final retirement when the last acquisition
releases. It uses the existing lazy completion holder; no per-item task or new
common-entry field is introduced. A scheduling reference handles release before
the continuation is installed. Final cleanup failures are observed by the final
release rather than hidden in an unobserved fire-and-forget Task.

The same boundary is used by synchronous stream completion and unregister.
Actual normal and compressed PreAdmission dispatch paths are covered. Release
errors are kept outside the acquisition/dispatch catch, so a deferred cleanup
failure cannot accidentally release the same acquisition twice.

Unregister still publishes the existing logical detached notification immediately.
An atomic bit in the existing entry state pins pooled-dispatcher lifetime until
final receive cleanup finishes; it does not add a per-route field. A dedicated
barrier test verifies that a detached entry cannot return its dispatcher to the
pool while generation-bound terminal credit is still being published.

Tests in `StreamManagerReceiveRetirementTests` cover:

- request completion, stream completion and unregister;
- stream 0 and nonzero stream IDs;
- actual plain and compressed pre-admission paths;
- paused before receive debit and after debit/before consumption;
- exact returned credit and terminal, plus new-generation admission with a
  one-state capacity limit proving the old tombstone was removed;
- duplicate completion, release during synchronous dispatcher completion,
  synchronous completion failure, and deferred callback-cleanup failure.

## P2: failed binding must not allocate ownerless receive state

Dispatcher binding now happens before callback mutation and before receive-state
resolution. An already-bound pooled dispatcher is rejected without invoking the
resolver, without attaching a new state, and without clearing the original
route's resolved callback.

Under the request registry lock the key must still be free before resolving and
publishing the receive generation. A failure after resolution but before route
insertion flushes only the captured, unpublished generation. Failure cleanup
also detaches the unpublished dispatcher state and returns active-route accounting.
It never resolves by key to discover what to roll back.

Tests include the real pooled dispatcher's bind rejection with a one-state flow
controller and a live old route whose consumption callback must survive a failed
second binding. The former proves new capacity remains available; the latter
proves rollback does not corrupt an existing owner.

## Verification boundary

The tests and fixes in this document are a successor to the measured candidate,
not a claim that the old artifact measures the corrected tree. Exact source-tree
IDs, pre-submit build/regression/full-suite results and new CI runs are recorded
in the PR follow-up. The measured short-flow cost is not fixed by this lifetime
correction. Keep the PR Draft until both correctness and the explicit allocation /
NativeAOT acceptance concerns are resolved.
