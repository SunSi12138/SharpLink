# Independent review: local76b8de5 attempt1

## Verdict
Evidence integrity PASS; observed performance mixed/inconclusive. No demonstrated overall gain, no equivalence/no-regression finding, no release acceptance. This compares ac17c8d8 with phase2153cd92 (reviewed607 tree), not official2.0.3. Hosted evidence must remain separate.

## Independent checks
- All604 files match author's complete SHA256/size receipt; no raw files modified.
- Exit0 record, complete marker,48 TCP processes/24 pairs and14 scalar processes present. Exact planned inventory; all TCP exits0. Chronological host timestamps match the predeclared six rotated scene rounds and balanced AB/BA sequence, with serial nonoverlapping processes. No evidence of replacement or retry in this retained campaign.
- Independently validated all TCP loaded source stamps, private runtime10.0.14, snapshot DLL hashes, exact framework configs, mode/wrapper/connection contract and successful operation records. Both arms fresh9package verification records; current source worktrees clean at expected identities.
- Root quiet release copied unchanged; release15:01 precedes measurement marker15:04:16.960; first TCP15:04:17.006, final TCP completed15:11:56.423. Dedicated writable CLI_HOME recorded. Root terminal release15:13 follows whole campaign. Receipt is coordination attestation, not proof of physical isolation.
- Read all4,137,425 duration ticks; recomputed P50/P95/P99/P99.9/max/mean and QPS for every sample. All match. CPU/allocation summary values were recomputed from recorded per-process metrics; raw OS CPU and GC allocation start/end counters are not separately exported, so those underlying measured deltas cannot be reconstructed from ticks.
- Independently reconstructed all four cells and six paired ratios per metric, process medians/ranges, ratio of medians, arithmetic mean paired ratio and exact seeded20000-replicate bootstrap. Every summary field matches. RPCs are not treated as independent replications.
- All12 scalar cells independently reconstructed from raw allocation files; baseline/candidate exactly equal in all5 FullOpts pairs. All14 scalar environment records use expected runtime and product source stamps.
- Earlier prepare-only NU1900 failure log/binlog retained under /workspace/shared/phase-performance-preparation/local-build-preflight1. No timed retry is inferred from that preflight recovery.

## Results: paired-mean change
| Cell | QPS | CPU/RPC | P99 | P99.9 |
|---|---:|---:|---:|---:|
| static1 | -1.80% | +5.56% | -1.73% | +4.01% |
| static100 | -1.61% | +0.54% | +5.46% | +5.71% |
| dynamic1 | +3.14% | -4.12% | +0.24% | -12.78% |
| dynamic100 | +1.03% | -1.85% | +1.74% | +9.96% |

Every QPS, CPU and latency descriptive95% interval crosses no change. Dynamic100 TCP B/RPC is -2.15% (interval -3.13% to -1.03%); that isolated transport-level allocation observation does not establish structural iterator allocation reduction or an overall performance win, especially given unchanged scalar allocations.

Selected guest CPUs show steal time1.474%–6.854%, median3.483%, independently recomputed from96 per-CPU/process host counter deltas. These encompass startup/warmup/shutdown, not only the measured window; no post-hoc noise correction is warranted. Unknown sysfs/SMT/cgroup/physical isolation and nonrandom fixed ordering remain limitations. The result is a restricted same-guest descriptive comparison only. Native code size was not used to decide success or failure.

Machine-readable calculations and independent verification source are verification.json and verify.py in this directory. No CLR, measurement rerun, CI or publishing performed by this review.
