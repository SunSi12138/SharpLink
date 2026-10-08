# Readonly inner lease safety and compiled-path preflight

This bounded stage reconstructs pristine G2 and candidate R, reruns correctness
and allocation controls, and collects actual byte[] JIT and NativeAOT machine code.
It makes no throughput comparison, timing acceptance, readiness, or promotion
claim. Failed first-binding A and validator V remain excluded; their historical
files and evidence are unchanged. Collection success leaves independent path and
ownership review pending.

## Frozen reconstruction and safety populations

Inherited helper: `34c17de5c565db796bf5ead8cbce88e787133dec`.
G2 production: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`.
R production: `df4383c7d00ee4a31c129ae42958e013f4ff5385`.
G2 with tests: `6d96ee8d21339bdef6e3d492f854afcbef02b329`.
R with tests: `54a4426f42e8edba19e98457d7a8a1477fcfe940`.
`author-manifest.json` pins production and identical-test-overlay trees, patch
hashes, exact focused populations and the original 2128-case suite population.
All inherited helper files, workloads, source settings, validators, allocation
budgets and original tests remain unchanged. The full G2 patch is reconstructed
from the original recipe and verified by its original SHA-256.

R contains exactly three token edits: the synchronous
`SendUnsizedStreamChunkResolvedAsync` lease parameter becomes `in`, and its two
wrapper calls explicitly pass `in creditLease`. `source-contract.py` rejects any
other edit in the three affected production files; tree checks exclude all other
production changes. The client and generated-server wrappers stay by value. The
first state-to-wrapper ownership snapshot, its position before user callbacks,
all sized/first-binding paths, and deferred/refund copies stay unchanged.

The same new ownership tests are overlaid on both G2 and R. Both complete default
unit suites and the complete R experimental suite must pass exactly 2142/2142
cases (the original 2128 plus 14 new) with zero failures/skips. Each focused population (3/4/4/2/1) is also checked on both
default arms. These 14 cases cover owned snapshots before callbacks, pooled-state
generation rejection, post-admission original-identity refunds, genuinely pending
serialized-budget ownership, and by-value dispatch/deferred signature guards. No prior A safety result substitutes for a fresh R or G2 suite.
The original allocation gates, both full 120-row warm/cold lifecycle controls,
<=0.01 B/request gate, actual-session construction controls, production callback
probes, writer source checks and original 171/171 experimental writer checks
remain. R retains whitespace/reference/maintainability checks.

Five disposable roots separate G2/R pristine production, G2/R test overlays, and
R's experimental writer overlay. All compiled-path collection uses only pristine
production roots. The experimental writer hook is isolated in its own root.
Safety logs, reports, exact populations, source identities and produced-binary
hash manifests are retained. Safety binaries are not archived and are not claimed
as independently rehashed retained binaries. Actual pristine JIT output and native
ELFs are archived in full.

## Actual matched JIT collection

Pinned SDK 10.0.112 and runtime 10.0.12 build the original RPC project. A separate
metadata probe reads the actual Release assemblies: both byte[] root field maps,
awaiter fields, managed sizes and the owned 40-byte lease representation must stay
identical. It checks by-value parameters on both wrappers, sized sender and async
slow helper, and readonly-byref only on R's synchronous unsized inner sender.
This probe has no production instrumentation and proves no machine-code savings.

There are exactly eight diagnostic processes, one G2/R pair for each original
workload:

- TCP Client100x16
- SHM Client100x4096
- SHM Server1x16, a first-send control
- SHM Server100x16, the real generated retained-item path

Each retains the original 30 warmups, five-second diagnostic window,
200000-operation cap, four-CPU affinity, R2R0/tiering1/PGO1/QuickJitForLoops1
settings, and unchanged workload validator. Only disassembly diagnostics and
method filters are added. There is no retry, resampling, extra warmup, promotion
forcing, fallback workload or rate comparison. Raw workload reports, including
failures, are evidence-collection output rather than a timing screen.

The frozen original JIT parser preserves every tier/version and its original
strict Dynamic-PGO verdict. This separate collection accepts matching optimized
root wrappers and MoveNext methods under their actual labels, including
Synthesized PGO. Tier, profile, PGO and OSR labels are never relabeled or mixed.
Only compiler-generated state-machine ordinals may be normalized; canonical
reference signatures are required, and an Int32-only capture cannot qualify.
Generated RPC sources and full built binaries are retained for independent
byte[]/unsized-codec mapping. Retained wrappers, unsized inner sender, deferred
helper, admission/controller and refund code are included alongside both roots.

## Native compiled-path inspection

The unchanged minimal real-RPC NativeAOT adapter uses the original RPC contracts,
service, generated callers, transports and loop. Its only adaptation is generated
JSON metadata outside that loop. Both pristine arms publish with the original
Release/linux-x64/PublishAot=true/StripSymbols=false settings. Adapter hashes match
and tracked source hashes are checked before adaptation, afterward and after
builds. No NativeAOT workload is executed; the four source-map targets describe
compiled paths to inspect and do not claim native runtime coverage.

Every ELF/debug-symbol publish file is archived with its modes. Full nm and
objdump streams are losslessly compressed; focused excerpts are matched by
address so aliases do not hide bodies. Source maps include actual generated
byte[] client/server RPC and retained Server100 routes. Missing or inlined
symbols remain unresolved inspection work, never proof of absent cost. Failed
arm/build/collection results are retained, and independent native arms are still
collected when possible.

## Required independent mechanism review

1. Map actual byte[]/unsized client and generated-server retained calls to exact
   matching root bodies. Server1 is a first-only control, not retained coverage.
2. Preserve the first 40-byte state-to-owned-wrapper snapshot at its original
   pre-callback boundary. No mutable producer-field alias may replace it.
3. Show the second owned-local-to-outgoing 40-byte lease copy is removed and the
   inner sender reads the owned snapshot via its 8-byte readonly pointer. The
   outgoing payload difference is not a promised stack-frame-size reduction.
4. Exclude an unconditional defensive/compensating whole-lease copy elsewhere.
   Distinguish all five lease fields from the separate 40-byte RpcDeadline.
5. Verify async slow admission captures a by-value lease before the synchronous
   borrower returns; retain the expected slow-path and refund ownership copies.
6. Preserve stale-generation rejection, deadline/cancel, publication, credit
   refund, writer/budget cleanup, sized paths and first binding. No extra hoisted
   lease/byref field, box, task or allocation may appear.

Stop before timing if correctness/allocation fails, retained code coverage is
missing, the ownership boundary changes, or the copy merely shifts elsewhere.
The existing broader acceptance requirements remain unchanged.

## Bounded artifacts and local verification

Artifacts are safety/index, separate pristine JIT-build packages, one JIT package
per arm/cell, and separate NativeAOT index/G2/R ELF/code packages: 17 possible
packages, each strictly below 24 MiB before upload. Every file is hashed. A package
becomes ready only after successful copy/hash/budget checks and atomic placement;
one failed package does not prevent earlier valid packages uploading. Packaging
errors get a bounded failure manifest and fail the job. No failed runtime or
collection result is converted into a pass by packaging.

The workflow changes only its new path and helper directory, has contents:read,
keeps credentials unpersisted, and pins official checkout/setup-dotnet/upload
Actions by commit SHA. Local verification is Python/parser/source/workflow
fixtures, reconstruction and git checks only. No local SDK/C# execution,
installation, security change, publication or timing run is performed.
