# #732 frozen-M0 phase diagnostic: observed post-warmup tier transitions

## Outcome

[Run41903010153/job126830138147](https://github.com/SunSi12138/SharpLink/actions/runs/41903010153/job/126830138147) completed successfully at exact tool HEAD `7f45a1882b7cb237c82a6d4e952e57a3dd452032`. The two planned fresh diagnostic processes completed, each reporting 44,000 checksum-validated calls and 54 captured phase markers matching the sidecar exactly. Both traces report zero lost events. This is a **limited instrumented diagnostic**, not performance acceptance. Historical M0 NoGo and M1 Hold remain unchanged.

The matched categories/names covering seven Server dispatch, Server/Runtime pump and `RpcMethodFacts.ToDescriptor` targets show live-load events labeled later tiers after the 1,000-call warmup. This observes post-warmup tier activity in this diagnostic and supports investigating tiering as a contributor to the earlier phase-drift hypothesis. The retained summary aggregates by category; it cannot reconstruct each method-ID/code-version trajectory or prove that calls used those versions. It does not establish how much of the old timing difference was caused by JIT.

## Frozen identity and shape

- Product M0: `37548b7a35d50803212d2b0a637306636cc0a1d3`; original harness/tools base: `47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f`.
- Actual isolated build SDK10.0.102; actual target private runtime10.0.11, workstation GC, effective processor count2, allowed CPUs0,2. VM-reported core IDs0 and1, not proof of exclusive physical-host cores.
- Trace tool10.0.750501 uses its separately recorded hosted runtime installation; that inventory is not the target probe runtime. Parser package TraceEvent4.0.0 locked restore.
- Original synchronous one-item TCP service and RunCalls invocation/checksum body retained. Cells I/T, P/T, I/U, P/U in fixed order; 1,000 initial calls + five2,000-call chunks each. Admission absent, telemetry disabled, concurrency1. No explicit Cancel regression.
- Diagnostic removes the old per-call ticks array/store and adds phase events/JSON boundary writes and tracing. Its durations are not equivalent to original timing windows.
- CausalProbe.dll SHA256: `440afb9fa9f6173cb7b430092264877c16de343942b12a317e8f46c71bb2014e`.
- Trace0 SHA256: `cf393e6a01af5d2d4c5de8b1944bfc5eed9be27aa4b6e483d5893468fdac6801`,10,392,674 bytes.
- Trace1 SHA256: `3b81cec5809f5c4546643354483ea8ca941d1df6ad25f5b5c0b512e138c7b67a`,10,415,174 bytes.

## Phase-aligned findings

Chunk numbers here are one-based. These are live method-load observations, not rundown timestamps.

- Process0: the matched categories covering seven target names contain QuickJitted live-load events during I/T warmup, QuickJittedInstrumented events during I/T chunk4, and OptimizedTier1 events during P/T chunk3.
- Process1: the same categories contain QuickJitted live-load events during I/T warmup and QuickJittedInstrumented events during I/T chunk5. The pump categories contain three OptimizedTier1 live-load events during P/T chunk1; the Server dispatch/ToDescriptor categories contain four during P/T chunk4 (one dispatch JIT-start occurs in chunk3). These are category-level observations, not reconstructed individual method trajectories.
- Targets comprise DispatchRpcAsync, InvokeServiceAsync, ResolveMethodCall; ServerGeneratedBridge.PumpOutboundStreamAsync and its actual Int32 MoveNext; Runtime RpcSessionGeneratedServerBridge.PumpOutboundStreamAsync; RpcMethodFacts.ToDescriptor.
- General runtime JIT activity continues into I/U chunks:258 and355 JIT-start events in the two processes. These counts include other methods and cannot be attributed solely to the seven targets.
- Four complete collection spans and four complete runtime suspension spans are observed per process. Collection duration is not pause time. Recorded suspension overlap during I/T chunks is2.933ms/2.851ms, during P/T chunks2.500ms/2.520ms; the remaining suspension is during P/U warmup. No incomplete collection/suspension endpoints are reported. These observations do not allocate old latency differences to GC.
- Kernel scheduling/off-CPU data was not collected. ThreadPool runtime events cannot substitute for a kernel scheduling trace.

## Explicit coverage defect and retention limit

Both summaries correctly report `coverageStatus=incomplete-or-limited`: the parser did not classify generated Facts proxy/stub methods. Source inspection identified an incorrect name assumption in the classifier, not evidence that those methods did no JIT work or were all inlined. Frozen M0 emits `SharpLink.Generated.<manifest>+__Proxy_<16hex>` and `__Stub_<16hex>`; `GetContractArtifactIdentity` is just the contract hash. The classifier required MethodFactsExperiment/IFactsRpc substrings and therefore missed this actual naming shape. The synthetic fixture had not represented that generated naming accurately.

Raw traces/logs were not uploaded, per user instruction. They stayed on the now-finished disposable runner and are unavailable for reclassification. Only bounded identity/event summaries are retained locally in BOUNDED-RESULTS.json and normal CI output. There is no claim of independent raw-trace reproduction. The two-process budget is exhausted; no third diagnostic or repeated timing was run. A tool-only classifier fix and realistic synthetic positive/negative controls can prepare future work but cannot retroactively complete this run's proxy/stub coverage.

## Preparation failures preserved

1. Head37a31e44, run41901539146/job126824963557: original-loop source equality failed before runtime installation or RPC execution. Local original materialization had added a trailing blank line. Corrected exact bytes and verified six frozen Git blob IDs; did not relax equality. Zero diagnostic processes.
2. Headf59135e, run41902344537/job126827813806: all8phase/source checks and private10.0.11 installation passed, but actual SDK10.0.114 failed the frozen102 gate before build or RPC. Corrected by a private SDK installation, retaining the102 gate. Zero diagnostic processes.
3. Reviewed final tool7f45a188: completed exactly the two planned diagnostics. No sample extension, deletion, adverse-pair replacement or performance rerun.

## Next decision boundary

Do not run another O/A/B timing campaign yet or declare a warmup duration sufficient from these two instrumented processes. Correct generated-name mapping using a verified emitted type/method inventory and synthetic controls. Any new collection or revised fixed warmup/order/measurement plan must be independently reviewed and separately authorized; the current run does not reopen its process budget. No product code, D2 operation, Cancel regression, merge, release, deletion or performance threshold was changed.
