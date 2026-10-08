# Bounded readonly inner lease D/G2/R screen

This prepares the unchanged 90-launch/198-row bounded JIT screen for R. Source
preparation and Python fixtures are the only local execution. Publication, builds
and timing are not performed by preparing this helper. Original full JIT and
NativeAOT acceptance remains required after any favorable bounded result. The
prior NoGo remains in force; neither failed first-binding A nor validator V is
included in these production roots.

## Frozen source and required proof

- Helper base: `7b84cd6a60dc9f8d8e7dfa8e29dc82c4adc2d535`
- Helper tree: `a5fcfa74feb6b6b6418af97da84cc93f71f0703d`
- D: `0fe26024b114bb6e78411a9b86276086c045d03d`
- G2 production: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`
- R production: `df4383c7d00ee4a31c129ae42958e013f4ff5385`
- R patch SHA-256: `1e1699632cc556481e7cde1df2b3bb44f9a7c0ff6a7ea45fc0ef0a6f148628ff`

The unchanged quiescent recipe reconstructs G2. Only the frozen three-token R
production patch is applied on top. D receives only the same two original
benchmark-harness files as prior screens. Production tree/source checks reject
test overlays and A/V changes. All inherited helper files, workload validators,
prior evidence, settings and budgets remain unchanged. New files are confined to
this helper directory and its new workflow.

Run 37855801421 supplies fresh G2/R safety and actual compiled-path evidence.
The independently verified safety populations are three complete 2142-case
suites, fourteen focused cases on each default arm, original allocation and
120-row lifecycle gates, and 171 original writer checks. An artifact-integrity
pass alone is not a semantic copy-removal pass. The workflow and runner require
the final hash-pinned combined JIT and Native machine proof before any build or
timing. Pending, incomplete, wrong-source or hash-mismatched proof is rejected.

The mechanism review must retain the first owned 40-byte state-to-wrapper
snapshot before user callbacks and prove removal of the second whole-lease copy
into the synchronous unsized sender without a compensating copy. Slow admission,
refund and deferred paths retain their required by-value ownership copies. Both
actual byte[] state-machine layouts remain unchanged. Captured JIT bodies are
Tier1/Synthesized PGO; the original Dynamic-PGO question remains inconclusive.
G2's sender inlines admission while R calls the controller. R's root code and
clearing also differ. These tradeoffs must remain visible; no CPU, latency,
throughput, allocation benefit or acceptance is inferred from copy removal.
Native evidence is compiled-only and does not establish runtime coverage.

## Exact inherited workload and retention

`screen.py` is byte-identical to the prior first-binding runner after A-to-R
candidate-label substitution only. Commands, settings, order, validators,
timeouts, comparisons and failure handling are unchanged. Six complete D/G2/R
permutations run the original five workloads in the original order:

1. TCP Client100x16
2. SharedMemory Client100x4096
3. SharedMemory Server1x16
4. SharedMemory c8 size 1, operation all
5. SharedMemory c8 size 10000, operation all

Exactly 90 launches produce 198 complete rows: 54 RPC and 144 c8. Every arm
occupies each position twice. Each c8 report retains unary/c2s/s2c/duplex order.
RPC retains 30 warmups, five seconds and the 200000-operation cap. c8 retains
one connection, concurrency eight, 8192/65536-byte receive windows, warmup one,
duration two and recording off. SDK 10.0.112/runtime 10.0.12 and the original
R2R0/tiering1/PGO1/QuickJitForLoops1 settings remain. Processor count and same-host
affinity remain four CPUs.

Every launch has one attempt. No retry, resampling, replacement, failed-row
exclusion, new workload, threshold change or tracing is added. All raw reports,
logs, commands, exits, cap flags, measured windows, counters, latencies, failures
and each of the six pairs remain. Production source and all application and
dependency binary files are hashed before and after the screen. The complete
retained artifact must be strictly below 24 MiB before upload. Workflow
permissions are contents:read, credentials are not persisted, and official
checkout/setup-dotnet/upload Actions are commit-pinned.

## Interpretation boundary

R/G2 is this patch's measured effect relative to G2. R/D is the separate direction
relative to shipping dev. An R/G2 improvement does not establish R/D acceptance
or erase remaining G2 regressions. No comparison attributes a CPU percentage to
copies, initialization, spills or code size. Collection success is not a timing
acceptance decision. A favorable screen still needs the original full JIT and
NativeAOT acceptance workload and independent review; prior NoGo remains.
