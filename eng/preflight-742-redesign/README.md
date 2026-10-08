# PR 742 whole lifecycle/admission redesign preflight

This harness reconstructs source independently from its publishing branch and does not modify the PR branch. No collection-success status is a performance acceptance verdict.

## Arms

- D: actual dev `0fe26024b114bb6e78411a9b86276086c045d03d`.
- C: the exact nine-blob candidate tree `ea21543ca4d267938834ac24bead069aac2327c0`, reconstructed using fixed recipe `06b737df0d9aab66caed9c446a572b3a8ce4efae`.
- N: exact tree `4c5943fec3a85089cefa8a1b6dc8c0f6502567ce`, C plus the explicitly hashed send, receive and ordered-tail patches, in that order. Source/test blobs, full patch digest and disposable tree are recorded before builds.

The common measurement harness comes from the fixed source, not from arbitrary arm-local edits. No per-item tracing is enabled. Prior measurements on different EPYC models are historical context, not causal old/new comparisons.

## Mechanisms and correctness

S returns the generation-bound lease only on first send. Retained sends carry their fixed lease and return nongeneric ValueTask. First-admission, writer transfer, pre-credit serialized memory, protocol debit/refund, deadline and disposal ownership remain distinct and tested.

R makes an existing route entry the immutable receive binding and makes request registration/removal transactional. Terminal actions carry the exact entry. Peer completion and actual route retirement have different admission/lifetime effects but share one final credit-publication boundary. Baseline negative controls retain unreachable registration, replacement-terminal and already-acquired-DATA failures. A thread-local hook is used only on the correctness control to pause the old mark/lookup boundary; this modified control is never timed.

Q bypasses transient waiter/task/node construction only when the gate-held proof establishes every earlier waiter is live/current, connection-eligible and stream-blocked. All other cases retain the existing queue path. Public probe semantics, credit windows and fairness are unchanged. Six keyed/resolved/first-lease and cancellation-token controls measure the previous232 B versus proposed0 B per eligible admission locally; remote execution verifies that mechanism again.

Private-field test fixtures may migrate to equivalent binding assertions; no observable lifecycle/credit assertion or test case is removed. Original default/experimental tests, formatter, reference/maintainability, deterministic allocation and writer checks remain. Existing single-consumption and consumer-pooling negative controls remain; the latter removes only the builder attribute from N so it does not undo receive ownership changes.

## Predeclared diagnostics

- JIT D/C/N whole-design screen:54 processes. Three Latin rotations within each workload block. Original SHM Server1x16 and Duplex100x16; original TCP/SHM c8 size1/10000 with all streaming operations and unary controls. Original durations, warmups, windows and four-CPU affinity. All failures and raw results retained without selective retries.
- C/N original controller micro: full232-row population, four independent balanced process repeats in each PGO mode and NativeAOT. The old resolve→accept→consume→flush short API remains unchanged and visible. Its extra first-use gate is not eliminated by sender/route changes; a new fused API is not substituted for it.
- NativeAOT D/C/N RPC screen: same two SHM RPC cells and three Latin rotations,18 processes. Minimal host changes only out-of-measurement JSON serialization and verifies actual native execution. Rooted machine code and binary hashes are retained for result-shape review.

CPU/op, allocation/op, drain and throughput are kept separately. c8 includes drain in process counters and does not support formal latency claims with recording disabled. The integrated screen establishes D→N/C→N whole-treatment directions; it does not attribute throughput independently to S, R or Q. Struct sizes are source-layout evidence, not measured allocation savings.

Only encouraging joint evidence proceeds to the full unchanged dev-versus-final acceptance and exact PR-head verification. Negative or inconclusive required results remain blockers.

The separate pre-existing persistent compressed-request reservation finding is excluded from N and does not change this screen. Original no-hook source builds also run the identical120-row complete warm/cold lifecycle allocation matrix before correctness-control barriers are added to C. Every byte result is retained.
