# Bounded first-binding awaiter D/G2/A screen

This prepares the original 90-launch/198-row screen for the first-binding awaiter
candidate. It makes no timing or performance claim before execution and review.
Original full acceptance remains required even after a favorable bounded result.

## Prior proof and source identity

The saved independent review from run 37846827122 establishes the intended layout
and inspected completion/deadline semantics only. Both actual byte[] managed
roots shrink 40 bytes, with matching JIT initialization and NativeAOT state copies.
Both captured JIT MoveNext bodies grow in code bytes. The NativeAOT client frame
and clearing are unchanged and its body grows; the native generated-server body,
frame and clearing shrink. These tradeoffs remain recorded in the hashed report.
Captured JIT roots are Tier1/Synthesized PGO. The original Dynamic-PGO question
remains inconclusive. Smaller state does not establish lower CPU or faster RPC.

`codegen-review.json` pins the final independent compiled-body report, machine
proof and source/binary/artifact integrity report. Preparation, builds and the
runner require that final proof and reject pending review, changed hashes or
different production trees. V's validator/out-reference mechanism is not A proof.
The successful safety stages from run 37844180672 were reused and independently
verified in the codegen run; their original overall diagnostic failure remains
recorded in the inherited evidence.

- D: `0fe26024b114bb6e78411a9b86276086c045d03d`
- G2 production: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`
- A production: `4cb4ce7a13b06c3a6f3213e30215b6769ed49f90`

Preparation starts from helper `48aecc599cb8ee91219d727b10ccdad572a776d4`,
reuses the original quiescent reconstruction and applies only A's frozen two-root
production patch. Both test overlays are rejected. D receives only the same two
original benchmark-harness files as previous screens. Prior helper files and
workload validators remain unchanged and are hash-checked. All production source
and built application/dependency files are recorded before and after the screen.

## Exact inherited workload

`screen.py` is byte-identical to the previous bounded runner after substituting
candidate label V with A. Commands, settings, timeouts, validators, pairing and
failure handling are unchanged. Six complete D/G2/A permutations run these five
workloads in the original order:

1. TCP Client100x16
2. SharedMemory Client100x4096
3. SharedMemory Server1x16
4. SharedMemory c8 size 1, operation all
5. SharedMemory c8 size 10000, operation all

Each c8 report preserves unary/c2s/s2c/duplex order. Exactly 90 launches produce 198
validated rows: 54 RPC and 144 c8. Every arm occupies each position twice.
RPC retains 30 warmups, 5 seconds and the 200000-operation cap. c8 retains one
connection, concurrency 8, 8192/65536-byte windows, warmup 1/duration 2 and recording
off. SDK 10.0.112/runtime 10.0.12 and original R2R0/tiering1/PGO1/QuickJitForLoops1
settings are retained. Processor count and same-host affinity remain four CPUs.

Every launch has one attempt. There is no retry, resampling, replacement,
failed-row exclusion, new workload, threshold change or instrumentation.
All raw reports/logs, commands, exits, cap flags, measured windows, counters,
latencies and failures remain, with every one of the six raw pairs. Collection
success is not an acceptance decision. Artifacts are checked below 24 MiB.

## Interpretation

A/G2 is the measured effect of this first-binding change on the existing G2 tree.
A/D is the separate direction relative to shipping dev. An A/G2 improvement does
not establish A/D acceptance or erase remaining G2 regressions. Neither comparison
attributes a percentage of CPU to initialization, spills or code size. Favorable
screen results still require the original full acceptance workload and review.
