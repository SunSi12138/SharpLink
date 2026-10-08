# Same-binary quiescent-retirement diagnostic

This is a diagnostic-only experiment. It creates no shipping candidate and does
not replace any original D-baseline acceptance. Shipping G2 remains exact
`398d484fb5b8ab8adb75a74db7d577d929d2dc77`; complete patch SHA-256 is
`5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b`.

The source-cleared adapter is tree
`7a9f9f313526f926755da2fcd2119303b86f7a1f`, incremental SHA-256
`cec7837c6c6c70819bcfc729f13acd91ce325ae63bfd86bf16290d823bcea02f`.
It initializes SHARPLINK_DIAGNOSTIC_QUIESCENT once at module load, after the
unchanged endian guard; values are1/0, missing means1, invalid values fail.
A non-inlined accessor performs a volatile read of a private non-readonly field.
Both the fast attempt and captured-candidate fallback are gated together. OFF
uses the F body. There are no timed counters or later mode setters.

Final six-process generated-RPC route proof is pinned and the diagnostic audit
guard is enabled. The separate counter build is treef6c0f3b2, patch9f2c6538;
it is never used for timing. Both modes show zero route activity for all72 unary
calls; measured Server1 has64 helper/claim/fast finishes ON and0 OFF, while
registration/DATA/completion/drain, wire, retirement/pool and shutdown checks
pass in both. The shipping tree and both source artifacts are reconstructed in
separate disposable worktrees. The benchmark and stream-load harnesses must
match the original recipe byte-for-byte.

## Primary: original-tiered72 launches

One runner builds two host sets once: untouched G2 and one diagnostic source.
The three arms are G2, ON and OFF. ON/OFF use literally the same app paths,
working directory, DLL bytes, source identity and command arguments. Only the
process-start flag differs. Every child uses one constant output path; the raw
file is renamed after process exit into its labeled archive, without changing
its bytes. G2 receives the ON flag value but its untouched code never reads it.

All six permutations run the same four original SHM workloads in the same order:

1. Client100x4096:30 warmups,5-second measurement,200000-operation cap
2. Server1x16: the same original arguments
3. c8 size1: operation-all, concurrency8,8192/65536 byte windows,
   warmup1/duration2, recording off
4. c8 size10000: the same original arguments

ReadyToRun=0, TieredCompilation=1, TieredPGO=1, QuickJitForLoops=1 and
ProcessorCount=4 are retained. Exactly72 launches/180 rows are required.
ON versus OFF estimates toggle/mechanism influence under those runtime flags.
ON versus untouched G2 measures adapter/build influence only. Neither contrast
establishes original D-baseline acceptance.

## Secondary: distinct non-tiered24 launches

ON/OFF run Server1x16 and c8 size1 in six alternating AB/BA blocks, retaining
both original workload arguments and c8 unary/c2s/s2c/duplex phase order.
ReadyToRun=0, TieredCompilation=0 and TieredPGO=0 are explicit. There are exactly
24 launches/60 rows, in a separate output directory and comparison set.

The total predeclared population is96 launches/240 rows. Four-CPU affinity,
source identities and all actual application/dependency hashes are retained
before and after each population. The runner verifies ON/OFF argv/environment
identity except the flag. Every raw CPU/allocation/count result, actual window,
drain, failure and operation-cap flag is retained. No failed or unfavorable cell
is retried, removed or replaced. Missing/invalid populations fail validation.

Identical IL/DLL bytes do not promise identical tiered-PGO native machine code.
The secondary run is distinct evidence; it cannot silently substitute for the
primary population. Untimed selected-method JIT-code capture is wired after untraced collection,
linked to the same timed Runtime DLLs. The pinned strict normalizer accepts only
identified relocation/type/GC-handle addresses and fails other differences.
Separate real generated-RPC route counters are wired in the correctness job. Their
instrumented/timing-perturbed results must never feed either untraced population.

## Correctness and limits

The adapter correctness job builds one test binary and checks both flag modes
against startup-immutability tests and all14 existing quiescent semantic cases,
with unchanged DLL hashes before/after. This is adapter validation, not a new
full shipping-source CI result. The correctness job reruns the pinned route-count proof in separate temporary
worktrees, building once and using one binary for all six ON/OFF processes.
Probe builds and full counter checkouts remain outside uploaded artifacts;
raw JSONs, generated-source/binary identities, logs and source hashes remain.
The author's final six raw reports and manifest are also retained in this kit. No performance acceptance or Ready result is automated.
