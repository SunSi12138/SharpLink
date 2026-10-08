# Actual Tier1/PGO send-path diagnostic

V remains No-Go after screen [37828725331](https://github.com/SunSi12138/SharpLink/actions/runs/37828725331).
This collects code from the original RPC program; it does not rerun acceptance,
compare traced rates, or infer an improvement from NativeAOT code.

## Fixed execution and identity

Exactly 12 processes: D/G2/V once each for TCP Client100x16, SHM Client100x4096,
SHM Server1x16, and SHM Server100x16, in that fixed order. All retain 30 warmups,
five seconds, the 200,000-operation cap and four-CPU affinity. Server1 distinguishes
first-item binding; Server100 exercises the generated sender's later-item route.
There are no repeats, fallback workloads, longer windows or synthetic loops.

The existing timing reconstruction runs from a separate frozen `dbd7ea9b` helper
worktree. D=`0fe26024`, G2 production=`398d484f`, V production=`379e7ca7`;
the unit overlay is excluded. Full source-map digests must equal the archived
screen. Only the RPC project is rebuilt, once per arm, using SDK10.0.112.
The host explicitly selects the measured runtime10.0.12; original runtime flags
remain R2R0, tiering1, PGO1, QuickJitForLoops1, processor count4.

`baseline.json` retains the original provenance hash and all67 RPC output hashes
per arm. Every rebuilt path is compared and every mismatch disclosed. A rebuild
is called historical-byte-identical only if all67 outputs match. Full current
output archives, runtime/JIT hashes and before/after source/binary maps remain.
Artifacts are separate index/D/G2/V packages with a24MiB per-arm file budget,
below the32MiB download limit. Original raw workload reports are labeled diagnostic
and are never fed into throughput comparisons.

## Supported capture and exact filters

The complete fixed method list is in `config.json`. Release assembly-qualified
JitDisasm patterns cover the actual ClientConnection send loop, generated RPC
pump, sized/unsized and lease-returning async MoveNext bodies, session credit
wrappers, and controller admission/validation/refund methods. Each process gets
its own JitStdOutFile and instruction bytes; Diffable and OnlyOptimized stay0.
No debug-only assembly filter, inline printer, checked JIT, profiler, threshold
change, no-inline setting, OSR toggle or system/security change is used.

[The pinned release configuration](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/jit/jitconfigvalues.h#L282)
defines these supported switches. [Method-list and threading documentation](https://github.com/dotnet/runtime/blob/v10.0.12/docs/design/coreclr/jit/viewing-jit-dumps.md#specifying-method-names)
defines the filters and warns about interleaved output. [Codegen headers](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/jit/codegencommon.cpp#L1699)
identify each method's actual tier and PGO source. [OSR guidance](https://github.com/dotnet/runtime/blob/v10.0.12/docs/design/features/OsrDetailsAndDebugging.md)
explains why quick JIT, instrumentation and promotion must remain unchanged.

## Interpretation and stopping condition

Every versioned block and raw stream is retained. Tier0, instrumented tiers,
FullOpts, Tier1 and Tier1-OSR are distinct; Dynamic, Static and Blended PGO are
not interchangeable. OSR entries and generic instantiations are recorded. The
parser checks block completion and actual instruction-byte lines. Mixed or
truncated output and missing optimized Dynamic-PGO caller roots are inconclusive.
Original warmup counts cannot guarantee promotion; no capture retries force it.

Successful root capture still requires independent instruction review. It must
trace the real caller to admission and locate either emitted helper calls or
inlined validation guards in that optimized body. Inlinee counts and symbol
absence cannot establish the path. Unchanged callers require matching signatures,
instantiations, tier/PGO and OSR labels. Intentional helper signatures, dev keyed
versus resolved APIs and changed async ordinals need explicit source-backed
role mapping. G2 may already eliminate the out-store under JIT; an unchanged
G2/V result is useful evidence. If the target optimized path cannot be identified,
stop inconclusive with all raw output preserved. This cannot reconstruct the
exact dynamic code versions or profiles of the already-completed timing run.
