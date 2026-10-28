# Independent interpretation: #931 default-PGO Client-only run 40646151477

## Decision

**Client-only experiment: inconclusive (mixed directions; not equivalence). Full #931 ac17c8d8: NoGo remains, keep Draft.** No performance run, dispatch, product edit, push or merge was performed by this reviewer. This reviewer did not implement the prior candidate. Review observed live GitHub at 2026-10-28 11:10–11:14 UTC.

Live [run40646151477](https://github.com/SunSi12138/SharpLink/actions/runs/40646151477): completed/success, attempt1, exact tool `51f5bd0ec3f600196f2e26ea48a466ec2cea62d8`, ownership-931 job122675327843 success; performance-smoke skipped. [PR931](https://github.com/SunSi12138/SharpLink/pull/931) remains open/Draft at `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`. CI success here means evidence collection completed, not performance acceptance.

## Evidence and verification

Persistent evidence checkout is `5a6cf6c490b43c083fb772e2c6f2663fa94bbe16`; [index](https://github.com/SunSi12138/SharpLink/blob/5a6cf6c490b43c083fb772e2c6f2663fa94bbe16/index.json). Independently SHA256-checked the archives before extraction:

- PGO `931-pgo-12.tar.gz`: `34210a62a927ac8e3c9168c8a3e9e4d884998fad4826fc8c94895b09f281afe7`.
- FullOpts `931-fullopts-12.tar.gz`: `953a7723162c29949586c5c51447e8d582d210a2bb02608aa548fd8700da8214`.
- Original `931-original-48.tar.gz`: `d2bddd26a4405cd4320589198164ec55f15894f205a3c4a4ac6b46f867a033a5`.
- R1 `931-r1-loop-exit.tar.gz`: `b1e73d631c7fa2111d603914cd901c342f89128d246c2cb813b0a7a3e34cc444`.

The mirror is a privacy-scoped archive, not all private original environment records. No raw measurements were removed or reweighted here. Full paths inside the original evidence describe historical sources, not local execution requirements.

Frozen arms: old Client `cc7c458b559295e0fc61ec0062bb220faf880a76`, new Client `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`; **both** use candidate Runtime/Server/Abstractions/other product DLLs. The common probe and generated module are identical. Independently recalculated all 9 frozen DLL hashes per arm and checked each measured process's binary inventory and loaded source/version/path. Only `SharpLink.Client.dll` differs. Prepared source hashes match archived source. Both original source build logs show zero warnings/errors, and package-verification logs report all 9 package/symbol pairs at the respective SHA.

SDK10.0.102; private runtime and ASP.NET Core10.0.13; Ubuntu24.04.5 X64. Default tiering/PGO, 1 worker/1 ready connection, dynamic intercepted TCP100, 3s warmup +6s measurement. CPUs0,1 are **same-core SMT siblings**, not dedicated physical CPUs. Actual command lines and timestamps confirm fixed AB/BA/AB/BA/AB/BA.

Preparation gate: both preflights exit0, exactly4352 RPCs, fixed 250ms pauses after1024/2048/3072/4096. Raw native listings independently matched every gate row. Four actual TCP closed-generic targets have positive-size Tier1 in each arm; lower/instrumented tiers remain and are not accepted as Tier1. Their proof applies only to the preflight processes, not every timed call.

Measurements: all12 raw/host/tick records, exit0, no timeouts, zero validationFailures, no inventory error, no failed or incomplete pair. **354648 completed RPCs and exactly354648 nonnegative integer latency ticks**. Independently recomputed mean, P50/P95/P99/P99.9/max from ordered ticks and operation throughput from count/elapsed. All match. Recomputed all paired ratios and 20000-replicate seed529 descriptive bootstrap endpoints exactly. CPU/allocation ratios derive from the recorded process measurements, not latency ticks; no claim of raw counter recomputation beyond those fields.

## All paired observations

Candidate/baseline; ratios above1 are adverse for CPU/latency, below1 adverse for throughput.

| Pair | Throughput | CPU/op | P99 | P99.9 |
|---|---:|---:|---:|---:|
|1|1.027123|0.967934|0.999306|0.963857|
|2|1.005589|0.996350|0.982726|0.891917|
|3|0.919160|1.099135|1.226892|1.554280|
|4|1.022092|0.979646|0.947402|0.727453|
|5|1.010544|0.986970|0.976675|1.144290|
|6|1.031038|0.962559|0.936401|1.002287|

| Metric | Mean paired delta | Descriptive95% ratio interval |
|---|---:|---:|
|Throughput|+0.2591%|[0.967442,1.024839]|
|CPU/op|−0.1234%|[0.971267,1.040547]|
|P99|+1.1567%|[0.954501,1.101685]|
|P99.9|+4.7347%|[0.863737,1.271879]|
|Allocation/op|+0.3150%|[0.971330,1.030529]|

Five P99 pairs are favorable, pair3 adverse; P99.9 splits3/3. Pair3 is retained; no predeclared invalidity criterion failed. Same GC1/GC2 count1/1 in every process, GC0 typically8 (last baseline7). Host snapshots show selected CPUs busy94–98%, others low and reported steal0, but they do not prove dedicated hardware or diagnose pair3 as external noise. No basis to discard it. Every latency/CPU/throughput interval crosses1; this is not an equivalence test and supplies no Go threshold.

## Comparison with existing evidence

Independently verified FullOpts all12 hosts,247595 RPC ticks, hashes and ratios: throughput+0.9045%, CPU/op−0.5653%, P99−0.9901%, P99.9−0.4999%, mixed directions and intervals crossing1. Allocation was−7.1299% under FullOpts, but PGO's allocation estimate is near zero; neither clears product acceptance.

Independently recomputed all original48-process raw paired ratios and verified48 host exit codes. Original full-product dynamic100 remains throughput−2.6687%, CPU/op+2.8508%, P99+38.0809%, P99.9+21.6436%. All6 original P99 pairs are adverse; intervals for these4 metrics exclude1 in the adverse direction. This experiment changed full product, while later Client-only profiles fix candidate Runtime/Server and use separate runners. Do not subtract cross-run means to assign exact Runtime/Server effects or treat absolute QPS differences as a PGO speedup. The current Client-only default-PGO experiment fails to reproduce a **consistent** adverse Client increment; it neither identifies Runtime/Server as the cause nor establishes full-product recovery.

## Actual native mechanism and bounded next step

In this run's actual `InterceptedAsyncEnumerable<int>.<GetAsyncEnumerator>.MoveNext` optimized Tier1, baseline size4194B/main `sub rsp,280`; candidate5109B/main `sub rsp,288`. More importantly:

- Baseline IG14 offset0x0189 tests `r14d` once, successful path reads Current at IG15 and proceeds directly to yield state at IG16.
- Candidate IG13 offset0x018E tests `r15d`; successful path reads Current at IG14, then IG15 jumps to IG65 offset0x0851, **tests the same `r15d` again**, and reaches yield at IG66. False path also goes through default item assignment IG16 then IG65.
- These are the real TCP request path, not a scalar stand-in. The existing R1 transformation targets precisely this duplicate result test/default assignment. This establishes a narrow native-mechanism reason to inspect R1, **not** evidence that the branch caused the observed latency regression.
- Dynamic iterator Tier1 size2357→2662B; server bridge1984→1984B. Common identical Runtime DLL pump nonetheless compiled7492→8041B, illustrating profile-dependent code variation despite identical source; whole-body size cannot be converted to causal CPU or tail percentages.

Under [technical decision tree1.3](https://github.com/SunSi12138/SharpLink/blob/ba99c6f9467fd85233afbc0dc1c84cf839e1b5a0/doc/3.0-release-handoff/technical-decision-tree.md), the actual-TCP-native trigger for an **R1-only bounded non-timed comparison** is supported. Recommended next experiment:

1. Reuse independently reviewed product patch `94adeae886c4ddfd0c02ec2dcb700cd97e9cad80` (report head87f76fb), do not stack R2/other changes. Independent reviewer must verify exact final source/tool HEAD and existing semantic tests' applicability.
2. Compare ac17c8d8 Client versus R1 Client, all other DLLs frozen common candidate; same actual TCP generated request, pinned runtime10.0.13, fixed non-timed preflight4352 operations and4 pauses, all4 true optimized Tier1 targets required. Retain all tiers/identity/host outcomes. No adaptive warmup, retry or timed measurements in this mechanism step.
3. Stop on missing gate or unchanged target branch shape; preserve artifacts. If duplicate branch removal is actually demonstrated, prepare one independently reviewed frozen original-contract performance campaign for the new candidate, including original full-product baseline to test recovery. Do not compare only R1/ac17 and infer baseline equivalence. Formal acceptance criteria must precede execution; do not invent a new percentage exemption.
4. If R1 has no mechanism/performance benefit, record failure and use only the tree's explicitly triggered R2 or retain NoGo. New user stretch vs2.0.3 (+10%QPS/−20%P99) requires its own matched evidence and does not waive any3.0 functional/release requirement.

Reproduction output: `recompute.py`, `independent-recompute.json`, `original-ratios-recomputed.json`, `native-verification.json`, and two gzip-preserved extracted intercepted Tier1 assembly files in this review directory. See [reproduction instructions](README.md). Source evidence and tool plan are immutable GitHub links, not unpublished desktop paths.
