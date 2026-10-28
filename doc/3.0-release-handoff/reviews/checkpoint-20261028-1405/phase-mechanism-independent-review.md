# Independent interpretation: phase-enumerator attempt 1

## Decision

**STOP the current 607 phase-specialized implementation as a performance-promotion candidate. Preserve it and its evidence; do not start timing, extend warmup, or collect cold helpers merely to turn the completeness gate green.**

The observed mechanism does not substantiate the proposed benefit: the smaller driver redistributes code into helpers, observed aggregate code and static instructions grow, object size does not shrink, and no continuation/state-machine elimination is established. This is an evidence-based research prioritization decision, not proof that the candidate is slower, incorrect, or universally incapable of improvement. A separately specified structural hypothesis that removes real wrapper/state-machine/suspension work is the better next research direction; it needs its own review and frozen experiment.

## Scope and identity

- Reviewed tool: `4f2c1751fa7c6b47a2560984a863dd459b5678dc`.
- Baseline: `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`; candidate: `6077617167adbfd561e494d733fac2e1779f218f`.
- Raw evidence: `/workspace/scratch/9e5903e2fbfb/phase-mechanism-evidence-4f2c1751-attempt1`.
- Prepared artifacts: `/workspace/scratch/9e5903e2fbfb/phase-mechanism-prepared-4f2c1751-attempt1`.
- Author report and receipt: `/workspace/scratch/9e5903e2fbfb/research-compiled-rpc-20261028/mechanism-attempt1`.
- This review performed Python/file/static analysis only. It invoked no CLR, compiler, probe, download, performance workload, or CI and modified no product/tool source or sealed evidence.

## Independent verification

1. Recomputed SHA256 and byte lengths for **193/193 receipt entries**. The receipt set exactly equals all files recursively present in the prepared and evidence directories; no file is missing or extra within that corpus.
2. Verified current tool HEAD and source hashes against prepared metadata; verified both product worktrees are clean at their frozen commits. Both snapshots contain nine DLLs and differ only in `SharpLink.Client.dll`. DLL/deps/runtimeconfig hashes match preparation.
3. Rehashed both retained runtime archives using SHA512 against the reviewed official lock and re-compared installed archive members: **193 Core members and 337 AspNet members**, all matching. Rechecked runtime binary hashes. This is independent local verification against the reviewed official lock, not a new online metadata retrieval.
4. Revalidated all eight proof files, including runtime 10.0.14, framework paths, loaded assembly source versions/MVID presence/locations, snapshot hashes, two-CPU configuration, and exact 24-cell inventories.
5. Reparsed all four raw `jit.txt` files; every reconstructed native inventory is exactly equal to its saved JSON, including compilation sequence, selected optimized bodies, static instruction counts, missing roots, and unresolved calls.
6. Gate/process records agree: eight scheduled processes, all exit code 0; four scalar gates Passed, four native completeness gates Failed. Overall `gate.json` remains Failed and `complete.json` is absent. Each native process has 6,144 completed RPCs (256 per cell). The sealed corpus contains one process result per scheduled job and no replacement process. Absence of unrecorded activity elsewhere cannot be proven from a receipt.
7. Retained baseline/candidate/module/probe build logs each report success with zero warnings/errors. SDK 10.0.102 is the frozen preparation setting; this reviewer did not relaunch SDK verification.

## Process and compilation census

| Native process | RPCs | Compilations | Tier0 / Instrumented Tier0 / Tier1 / FullOpts | Selected optimized bodies | Missing optimized roots | Unresolved call sites |
|---|---:|---:|---|---:|---|---:|
| tcp-native-default-pgo-baseline | 6144 | 83 | 36 / 25 / 22 / 0 | 22 | dynamic-iterator | 4 |
| tcp-native-default-pgo-candidate | 6144 | 112 | 49 / 36 / 27 / 0 | 27 | dynamic-iterator | 12 |
| tcp-native-fullopts-baseline | 6144 | 28 | 0 / 0 / 0 / 28 | 28 | none | 4 |
| tcp-native-fullopts-candidate | 6144 | 35 | 0 / 0 / 0 / 35 | 35 | none | 8 |

The four scalar processes each contain all 24 cells: allocation processes use 1,024 observed calls/cell after 128 warmups; continuation processes use 64 diagnostic calls/cell after 128 warmups. There are no latency/QPS measurements.

## Native completeness: three distinct explanations, none valued at zero

**Not optimized under fixed default PGO:** the real `PhysicalTcp.__IScalarStream_SharpLinkRequest_ACC1F20B0513B621` dynamic deferred iterator is present as Instrumented Tier0 in both arms, so this is not missing TCP reachability. Candidate `BeginDispose`, `Complete(Exception)`, and `FinishTimer` have Tier0 and Instrumented Tier0 bodies but no accepted Tier1 bodies. FullOpts contains these optimized bodies; it is a separate diagnostic and cannot repair the primary PGO gate.

**Cold outlined work:** baseline FullOpts has four unresolved call sites to `CompleteAbandonedStreamAsync`. Candidate FullOpts has eight unresolved sites across seven named targets: `FailDispose`, `FailMove`, `TransferCancellation`, `TransferDeadline`, `CompleteAbandonedStreamAsync`, `get_Deadline`, and `get_TimeProvider`. Success-only cells do not deliberately enter failure/ownership-transfer paths. Cold-path attribution follows source/control-flow inspection, not observed branch counters.

**Inlining/indirect limits:** the candidate FullOpts driver visibly calls `get_Deadline` and `get_TimeProvider` in its exception-filter region (`jit.txt` lines 530 and 534). Possible inlining at another call site does not explain away these explicit calls. Raw inline summaries do not establish complete per-callee attribution; external framework and indirect-call closure is not fully measured. Every reported unresolved family entry is a parsable named target without a selected optimized body—there are no remaining parser-error entries.

Failure of completeness is expected evidence to retain, not a reason to infer product failure or assign missing helper costs zero. Because the positive structural premise is not supported even in the visible optimized inventory, expanding collection solely to materialize the missing cold bodies is not justified for this candidate.

## FullOpts observed footprint: outlining, not an established aggregate reduction

| Observation | Baseline | Candidate | Candidate minus baseline |
|---|---:|---:|---:|
| Family separately observed methods | 10 | 17 | +7 |
| Family native bytes | 5,392 | 5,492 | +100 |
| Family static instructions | 1,252 | 1,411 | +159 |
| Entire observed scoped native bytes | 19,088 | 19,188 | +100 |
| Entire observed scoped static instructions | 4,350 | 4,509 | +159 |

The 18 non-family optimized bodies have identical method-name/native-byte/static-instruction inventories between arms. The family accounts for the entire visible FullOpts difference. This is equality of that inventory, not an assertion that machine bytes or dynamic execution frequencies are identical.

The baseline compiler driver is 4,496 bytes/983 static instructions; candidate driver is 520 bytes/154 instructions. Candidate separately observed work includes `Resume` 974 B, `BeginMove` 1,179 B, `FinishInvocation` 370 B, `BeginDispose` 633 B, `FinishTimer` 650 B, `FinishMove` 97 B, and `Complete` 173 B. Selecting only the 520-byte driver would conceal the outlined work.

These are **partial observed footprints**, not complete transitive/native-image size, retired instructions per RPC, simultaneous stack peak, or elapsed performance. Asymmetric missing cold bodies prevent a complete total-cost comparison. Default-PGO optimized aggregate totals must not be used as a savings claim because the accepted body sets are incomplete and differ.

## Object layouts and allocation observations

Baseline enumerator class sample: 200.048828125 B/object; candidate enumerator: 200 B/object. Baseline owner: 112 B/object; candidate owner: 112.048828125 B/object. Each observation is a 4,096-object allocation sample. The combined samples are identical, approximately 312 B for owner plus enumerator; their small residuals do not establish precise object-layout differences. **No layout shrink is established.** Value-state structs are not reported as actual async-builder box size.

Below are all 24 allocation rows, directly recomputed from raw process-wide allocated bytes divided by 1,024 calls. These include source/harness/background effects and are single-process observations, not repeated statistical estimates. The separate continuation EventListener was not active in these allocation processes.

| Channel | Timed | Producer pending | Items | Baseline B/call | Candidate B/call | Difference |
|---|---|---|---:|---:|---:|---:|
| static | False | False | 0 | 808.000000 | 808.000000 | +0.000000 |
| static | False | False | 1 | 808.000000 | 808.328125 | +0.328125 |
| static | False | False | 100 | 808.000000 | 808.328125 | +0.328125 |
| static | False | True | 0 | 1216.000000 | 1216.640625 | +0.640625 |
| static | False | True | 1 | 1351.867188 | 1352.132812 | +0.265625 |
| static | False | True | 100 | 14814.625000 | 14813.210938 | -1.414062 |
| static | True | False | 0 | 832.609375 | 832.609375 | +0.000000 |
| static | True | False | 1 | 832.609375 | 832.609375 | +0.000000 |
| static | True | False | 100 | 832.609375 | 832.609375 | +0.000000 |
| static | True | True | 0 | 2279.742188 | 2292.695312 | +12.953125 |
| static | True | True | 1 | 3280.609375 | 3302.796875 | +22.187500 |
| static | True | True | 100 | 102625.375000 | 110999.265625 | +8373.890625 |
| dynamic | False | False | 0 | 1320.000000 | 1320.609375 | +0.609375 |
| dynamic | False | False | 1 | 1320.000000 | 1320.609375 | +0.609375 |
| dynamic | False | False | 100 | 1320.000000 | 1320.609375 | +0.609375 |
| dynamic | False | True | 0 | 1808.000000 | 1808.609375 | +0.609375 |
| dynamic | False | True | 1 | 1944.609375 | 1944.476562 | -0.132812 |
| dynamic | False | True | 100 | 15405.476562 | 15407.281250 | +1.804688 |
| dynamic | True | False | 0 | 1344.609375 | 1344.609375 | +0.000000 |
| dynamic | True | False | 1 | 1344.609375 | 1344.609375 | +0.000000 |
| dynamic | True | False | 100 | 1344.609375 | 1344.609375 | +0.000000 |
| dynamic | True | True | 0 | 3048.609375 | 2938.617188 | -109.992188 |
| dynamic | True | True | 1 | 4009.976562 | 4173.960938 | +163.984375 |
| dynamic | True | True | 100 | 96668.585938 | 110179.703125 | +13511.117188 |

Timed/pending/100 is descriptively higher in the candidate: static +8,373.890625 B/call and dynamic +13,511.1171875 B/call. Synchronous cells are approximately unchanged. These observations provide no demonstrated allocation benefit, but they do **not** identify a causal allocation regression, its significance, QPS, or tail latency. Do not extrapolate percentages into performance targets.

## Continuation evidence

Each arm has identical deterministic source-resumption counts for each matching cell: zero for synchronous sources; `(128 + 64) * (items + 2)` for pending sources, including terminal move and disposal. The pending fixture uses a reusable source rather than a per-item fixture Task. Identical source counts do not prove identical internal continuation counts.

Consumer-visible pending moves differ with scheduling. For timed/pending/100, static pending observations are 4,140 versus 4,512; dynamic are 4,956 versus 4,345, out of 6,464 MoveNext calls. All synchronous-source diagnostic rows report zero pending moves. Explicit post-terminal pending disposals are zero because underlying disposal is already incorporated into the terminal move; this does not establish eliminated disposal work.

TPL aggregates also point in mixed directions: `TraceOperationBegin` 135,513 → 136,951, `TaskWaitBegin` 123,435 → 125,040, but `AwaitTaskContinuationScheduled` 5,273 → 3,941. These process-wide, instrumented totals include warmup and harness effects and cannot be assigned to one hidden production continuation. There is **no demonstrated structural elimination of state machines, continuations, or ownership work**.

## Next-hypothesis boundary

- Archive the current 607 result as a stopped optimization hypothesis, without reverting its prior correctness review or rewriting prior ac17/R1 NoGo history.
- Do not promote this candidate to timing based on its driver size; do not rerun the fixed matrix merely to improve tiers, cell outcomes, or gate appearance.
- For the next research proposal, require an explicit mechanism showing which real wrapper/state-machine/suspension or timed-path conversion disappears, what ownership responsibilities remain, and how async lifetime/cancellation/deadline/error semantics are preserved.
- Require independently reviewed correctness and a new bounded mechanism protocol before claiming structural benefit; only a supported positive mechanism would justify a separately reviewed paired performance experiment. No next implementation or experiment was performed by this reviewer.
- This evidence is not an official 2.0.3 comparison, all-five-RPC acceptance, NativeAOT acceptance, release approval, or proof of the user’s end-to-end +10% QPS / −20% P99 target.

## Reproducibility files

- `/workspace/shared/phase-mechanism-review/verify_evidence.py`: offline verification and derivation script; no CLR invocation.
- `/workspace/shared/phase-mechanism-review/verified-evidence.json`: independently reconstructed census, footprint, complete allocation/layout and continuation data.
- This report: `/workspace/shared/phase-mechanism-review/MECHANISM_REVIEW.md`.

**Final disposition: identity/provenance and reported arithmetic VERIFIED; native completeness remains FAILED; stop promotion of current 607 and move to a structurally different hypothesis.**
