# Independent hosted result review — run40712925050, attempt1

## Verdict
Evidence integrity PASS. This candidate has no demonstrated throughput or CPU-efficiency gain in the bounded workload. Retain two unfavorable latency observations, particularly static1 P99.9; they are descriptive risk signals, not multiplicity-adjusted formal proof of a regression. Successful CI does not establish equivalence, non-inferiority, performance acceptance or release eligibility.

Scope: tool90d98e3cb84ce36425b99c88c8112632194834b5; baselineac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c versus candidate2153cd9228c1de25e89a4bb68f0e3da4dc19f867/tree02a2824f344c3614fff8de1d8a10ed7a5be87c21. NOT official2.0.3; not pooled with local76b8de5.

## Integrity and independent recomputation
- Artifact12827506989 ZIP122341542bytes SHA256710bbdf0d9630ccf6290543afb7609e34fdefb331231cc6a73d96888ca4bf179 verified. All596 extracted files exactly equal their ZIP members; no missing, duplicate or extra extracted files.
-48 TCP processes/24 paired comparisons and14 scalar processes complete. Exact planned names and chronological rotated-scene AB/BA schedule; no overlapping processes. All TCP host records success/exit0. No failed/missing/extra slots or replacements evidenced in this campaign. Driver contains one matrix invocation then summary then scalar; complete.json present. No raw evidence changed.
- Validated TCP runtime10.0.14, framework paths/configs, processorCount2, frozen snapshot inventories/hashes, loaded assembly source stamps/MVID presence/locations, observed wrapper/channel and connection/error contracts. Both nine-package verification records identify expected source commits. Both actual runtime archives' SHA512 match official metadata and independently reviewed pins.
- Independently processed all3,551,642 raw duration ticks: count, P50/P95/P99/P99.9/max/mean, QPS and items/sec match all48 raw summaries.
- Independently rebuilt all four cells' six process-pair ratios for all six metrics; baseline/candidate medians and ranges, ratio of medians, arithmetic mean paired ratio, and seeded20000-replicate bootstrap endpoints match every reported field. Statistical replication unit is the process pair, never an individual RPC.
- Underlying CPU-time/GC allocation start/end counters are not separately exported; their paired statistics are verified from recorded per-process metrics, not reconstructed from duration ticks.
- All14 scalar environment records have expected private framework/source identities. All12 scalar allocation cells independently reconstructed from raw allocation files; five FullOpts baseline/candidate results exactly equal in every cell.

## All four cells: paired-mean change
| Cell | QPS | CPU/RPC | P99 | P99.9 |
|---|---:|---:|---:|---:|
| static1 | -0.01% | -0.33% | -1.44% | +24.28% |
| static100 | -0.35% | +0.55% | +2.95% | -5.94% |
| dynamic1 | -1.42% | +0.47% | -3.04% | -11.23% |
| dynamic100 | -0.51% | +0.50% | +3.24% | +7.35% |

All QPS/CPU/P99 descriptive95% intervals cross zero change. Static1 P99.9 paired mean+24.28%, interval+2.80%..+52.72%; six paired ratios1.35659,1.83359,0.99166,0.95002,1.35568,0.96947. Three adverse and three favorable pairs; retain every pair, including large unfavorable values. Baseline/candidate P99.9 medians94.1965/108.2625us; ratio-of-medians+14.93% is distinct from paired-mean+24.28%.

Dynamic1 P50+0.995%, descriptive interval+0.167%..+2.077%. Static100 TCP B/RPC-5.084%, interval-9.389%..-1.217% is also disclosed; identical scalar allocations mean this cannot be assigned to reduced iterator-object allocation. No broader performance win follows from this isolated transport-level allocation observation.

All24 metric/cell intervals are descriptive, with only six process pairs each, no multiple-comparison correction and no predeclared equivalence or acceptance margin. Do not call selected intervals formal statistical significance, do not discard outliers and do not rerun to green. Code/native size plays no part in the performance judgment.

## Host and interpretation limits
Ubuntu24.04 image20261023.334.3 on Microsoft-hypervisor AMD EPYC9V74 guest. Four logical CPUs, two guest-reported cores, two threads/core. IMPORTANT: cpu-mapping.txt maps selectedCPU0 andCPU1 BOTH toCORE0; they are guest-reported SMT siblings, not two independent physical cores. The frozen plan selected the first two allowed logical CPUs, so this is faithful execution but a material workload/topology limitation.

Selected-CPU steal counters are zero across96 CPU/process observations; zero steal does not prove no external contention or dedicated physical hardware. Guest loadavg1m at process boundaries ranges3.08..3.77, median3.44; it cannot attribute activity to other jobs or be subtracted as a noise correction. Observations cover startup/warmup/shutdown as well as measurement. Fixed nonrandom order, default tiering, one worker/connection, loopback server sync-yield and shared client/server process limit generalization.

Measurement marker15:11:34.967UTC; first TCP15:11:34.995; last TCP completed15:19:08.930. Local campaign is separate, with different guest topology/contention and no statistical pooling permitted.

Machine-readable independent calculations: verification.json. Reproducible read-only offline verifier: verify.py. No downloads, CLR execution, benchmark reruns, CI or publication by this review.
