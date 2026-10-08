# R safety and actual compiled-path decision

Run https://github.com/SunSi12138/SharpLink/actions/runs/37855801421, attempt 1, helper `7b84cd6a60dc9f8d8e7dfa8e29dc82c4adc2d535`.

**PASS_MECHANISM_ONLY.** Independent raw safety, artifact integrity, real byte-array JIT retained client/server paths and matching NativeAOT compiled paths establish the intended bounded change. This does not establish faster RPC, lower CPU, full performance acceptance, or readiness.

G2 production: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`.
R production: `df4383c7d00ee4a31c129ae42958e013f4ff5385`.
Production patch SHA-256: `1e1699632cc556481e7cde1df2b3bb44f9a7c0ff6a7ea45fc0ef0a6f148628ff`.
R contains exactly the three reviewed readonly-inner-lease edits on G2. Failed A and V are excluded. Timing roots must exclude the separate test overlay.

## Safety and provenance

All 17 original official artifact ZIP hashes, lengths, CRCs and safe file maps verify, as do the source/helper manifests, all 134 pristine JIT output members and both 15-file NativeAOT publish archives. Original raw workload validators and JIT parsers replay exactly. No rerun, excluded failure, changed threshold or substituted source exists in this result.

- G2 default, R default and R experimental: each 2,142/2,142; zero failures/skips
- Both default arms: all 14 focused ownership controls, identical populations
- Original writer171, formatting, reference and maintainability gates pass
- Four original allocation controls per arm pass unchanged budgets
- Both 120-row lifecycle comparisons retain maximum delta 0.0 B/request
- All eight original JIT diagnostic processes validate on SDK10.0.112/runtime10.0.12 with the original settings

Safety DLLs were not archived; their recorded binary hashes are not presented as independently rehashed safety bytes. Pristine JIT binaries and actual NativeAOT ELF bytes were archived and rehashed. Full evidence and scope limits are in the pinned integrity/safety review.

## Established instruction-level mechanism

Both actual client cells (TCP Client100x16 and SHM Client100x4096) and the generated-server retained cell (SHM Server100x16) preserve the original 40-byte value snapshot before outer deadline/sizing callbacks. The inner readonly pointer targets that owned local, not the mutable root lease field. G2's second whole-value outgoing transfer becomes one address and pointer argument in R, reducing that argument payload by 32 bytes.

R's actual successful inner/controller paths do not materialize a compensating whole-lease copy. Owner, state type, Attached and captured-generation validation remain, together with the original lock, fairness, terminal and dual-credit checks. Deferred and refund outbound calls still copy all 40 bytes by value. Source/metadata retain the exact async ownership boundary, and the new paired tests explicitly exercise callback replacement, state reuse, post-admission refund and a pending serialized-budget await before the synchronous wrapper returns.

The JIT diagnostic captures do not emit deferred starter/refund destination bodies. Their outbound value copies are visible, and these bodies remain by value in source/metadata. The actual matching NativeAOT deferred starter, controller/refund and contended bodies were inspected separately and preserve ownership before returning or suspending. No missing JIT body is described as inspected; Native code is not relabeled as JIT code.

The Native caller/callee chain likewise retains the first owned snapshot and replaces only the second outgoing copy, with no unconditional compensation. Both Native roots are 38 code bytes smaller; the modified inner sender grows six bytes. Root state/frame/clearing and reviewed admission/refund machine code remain unchanged. Native scope is compiled inspection only: no native workload execution, timing or runtime coverage was performed here.

## Tradeoffs that the next experiment must retain

This is not a uniformly smaller/faster emitted program:

- Captured roots and inner senders are Tier1/Synthesized PGO, no OSR. Separately emitted generated dispatch wrappers are labeled Dynamic PGO; those labels remain distinct.
- G2 inlines admission in its inspected JIT inner sender; R calls `TryAcquireSendCredit(byref,int)`. The controller consumes the pointer directly, but its call/prologue overhead remains a real tradeoff.
- TCP client root code grows 7,946→8,094 bytes and local zeroing grows 600→672 bytes; SHM client code grows 8,215→8,237 while zeroing changes 680→672.
- Server100 root code shrinks 12,197→11,196 bytes, reservation 1,240→1,192 and zeroing 968→960. Profile-dependent differences cannot all be attributed solely to the changed copy.
- Managed client/server roots remain 256/304 bytes, lease40 bytes and first-binding lease awaiter56 bytes. There is no A-style root-footprint reduction.

These counts are static code facts, not allocations, path frequencies or CPU shares. Diagnostic workload metrics are not compared. Server1 is first-binding control and does not exercise the retained path.

## Bounded next step and unchanged acceptance

A single original fixed D/G2/R screen is justified to test the actual combined copy/inlining tradeoff, rather than assume a benefit. It must preserve all 90 launches/198 rows, six permutations, durations, processor affinity, runtime flags, windows, operation order, validators, every raw pair and all negative controls. No rerun/resampling to obtain a favorable result.

R/G2 measures the component change; R/dev is the separate baseline comparison. Original G2, V and A negative results remain. A favorable bounded screen would still require the original full JIT and NativeAOT acceptance and exact final PR-head CI. No current performance/Ready claim and no gate relaxation is authorized by this mechanism decision.

The compact server summary pins exact captured-method hashes and the full original local machine-report hash; the full report and all original raw blocks remain retained. Compact publication avoids duplicating megabytes of already archived raw disassembly and does not discard that evidence.
