# Independent raw-evidence review — logical forwarder mechanism attempt 1

## Verdict

**PASS: the retained dataset is valid for the bounded non-timed mechanism question. Positive mechanism evidence is sufficient to proceed to separately authorized, frozen and independently bound end-to-end TCP timing. This is NOT a performance Go, release approval, or a claim of guaranteed per-item savings.**

Tool: `01f6aad58dd14278497a219dfad4f96906b8919a`.
Baseline: `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`.
Candidate: `d17e1ac691325b8948ca8663ba2d2b836c3ac2a0`.

I independently read/revalidated raw proofs and recomputed all allocation rows/slopes. I did not build, execute CLR, run any benchmark, modify product/tool code, or add/replace samples. Review scripts perform filesystem/hash/JSON and existing Python-validator checks only.

## Identity, completeness and failure retention

- Author REPORT.md SHA256: `252060c66d8077ab50404a65504c9ebc46993b4739b472592670755fbb4f8158`, verified.
- Census SHA256: `d892d473c25bf8c0f10818fb04ea994675fa2c63f7b518cc7062dedcfbd10b48`, verified; **189/189 listed file hashes match**. Independently enumerating all files under prepared, evidence and launch roots exactly matches their census subsets: no missing or extra files there.
- Tool and both source worktrees remain clean at their exact pinned commits. Prepared and run plans agree with the reviewed plan. Prepared probe source is covered by the verified census; unchanged frozen tool binds preparation and execution.
- Snapshot DLL inventories and executable configuration snapshots rehashed successfully. Only `SharpLink.Client.dll` differs between arms; probe and other executing DLLs are identical. Loaded path/source/MVID presence and snapshot hashes in all four proofs pass the reviewed validator. This checks recorded loaded identities and current bytes, not a new live process inspection.
- Baseline Client SHA256: `a18bec77e6693a4189ede579ac4c1293043a2f7e44f19bcdc7c0d1fbb928c3f4`.
- Candidate Client SHA256: `6ce3458f1b12f4c98c2bdb4b2b6962921268bf7388fbbab1d501879dcacf114d`.
- Shared probe SHA256: `4abc40d2170da02f8b578f483c33da2422ccee89b945705bd3873df378f89042`.
- Independently rehashed official locked Core/ASP.NET runtime archives (SHA512) and compared installed members: **193 Core + 337 ASP.NET members**, exactly matching the run receipt. Runtime binary hashes also match. All proof records report fixed 10.0.14, X64, workstation GC, FullOpts/tiering disabled, processor count 2 and expected private framework locations. No fresh CLR runtime-list command was run by reviewer. SDK10.0.102 is the frozen prepare version check; SDK archive provenance was not newly established.
- Baseline build, candidate build and probe build logs each record zero warnings/errors. Launch prepare/run exit receipts are zero. Four scheduled jobs are present in the declared order, each exit 0 and PASS, with four matching individual process records and empty process logs. Each contains all 24 cells. No retained failed/replacement attempt or changed warmup was found; this conclusion is about the complete retained campaign, not an assertion that filesystem evidence can prove no process ever ran elsewhere.

## Allocation results: every cell retained

Bytes per complete invocation; 128 warmups and 1024 sampled calls per cell. The reviewer independently divided raw allocatedBytes by 1024 and matched every recorded allocation-pairs row exactly.

| Telemetry | Caller | Source | Items | Baseline B/call | Candidate B/call | Candidate − baseline |
|---|---|---|---:|---:|---:|---:|
| off | direct | sync | 0 | 808.000000 | 816.000000 | +8.000000 |
| off | direct | sync | 1 | 808.328125 | 816.328125 | +8.000000 |
| off | direct | sync | 100 | 808.328125 | 816.328125 | +8.000000 |
| off | direct | pending | 0 | 1192.609375 | 1064.000000 | -128.609375 |
| off | direct | pending | 1 | 1328.640625 | 1064.132812 | -264.507812 |
| off | direct | pending | 100 | 14792.476562 | 1064.000000 | -13728.476562 |
| off | AsTask | sync | 0 | 808.000000 | 816.609375 | +8.609375 |
| off | AsTask | sync | 1 | 808.000000 | 816.609375 | +8.609375 |
| off | AsTask | sync | 100 | 808.000000 | 816.609375 | +8.609375 |
| off | AsTask | pending | 0 | 1192.000000 | 1152.609375 | -39.390625 |
| off | AsTask | pending | 1 | 1327.867188 | 1240.609375 | -87.257812 |
| off | AsTask | pending | 100 | 14791.765625 | 9975.125000 | -4816.640625 |
| on | direct | sync | 0 | 1704.531250 | 1712.531250 | +8.000000 |
| on | direct | sync | 1 | 1704.531250 | 1712.531250 | +8.000000 |
| on | direct | sync | 100 | 1705.468750 | 1712.531250 | +7.062500 |
| on | direct | pending | 0 | 2226.000000 | 2234.000000 | +8.000000 |
| on | direct | pending | 1 | 2496.929688 | 2506.000000 | +9.070312 |
| on | direct | pending | 100 | 29430.242188 | 29437.843750 | +7.601562 |
| on | AsTask | sync | 0 | 1704.531250 | 1712.531250 | +8.000000 |
| on | AsTask | sync | 1 | 1704.531250 | 1712.531250 | +8.000000 |
| on | AsTask | sync | 100 | 1704.531250 | 1712.531250 | +8.000000 |
| on | AsTask | pending | 0 | 2225.062500 | 2233.062500 | +8.000000 |
| on | AsTask | pending | 1 | 2497.062500 | 2505.062500 | +8.000000 |
| on | AsTask | pending | 100 | 29427.320312 | 29396.414062 | -30.906250 |

## Both slopes: all eight groups

The first slope is B1−B0; the long slope is (B100−B1)/99. Independently recomputed values match all saved allocation-pairs slopes exactly. Tiny negative values are differences of observed totals, not negative allocations.

| Telemetry | Caller | Source | Baseline B1−B0 | Candidate B1−B0 | Baseline long slope | Candidate long slope |
|---|---|---|---:|---:|---:|---:|
| off | direct | sync | 0.328125000 | 0.328125000 | 0.000000000 | 0.000000000 |
| off | direct | pending | 136.031250000 | 0.132812500 | 135.998342803 | -0.001341540 |
| off | AsTask | sync | 0.000000000 | 0.000000000 | 0.000000000 | 0.000000000 |
| off | AsTask | pending | 135.867187500 | 88.000000000 | 135.998974116 | 88.227430556 |
| on | direct | sync | 0.000000000 | 0.000000000 | 0.009469697 | 0.000000000 |
| on | direct | pending | 270.929687500 | 272.000000000 | 272.053661616 | 272.038825758 |
| on | AsTask | sync | 0.000000000 | 0.000000000 | 0.000000000 | 0.000000000 |
| on | AsTask | pending | 272.000000000 | 272.000000000 | 272.022806187 | 271.629813763 |

Interpretation:

- **Off/direct/pending:** long slope 135.998342803 → −0.001341540 B/item, consistent with elimination of the logical forwarding allocation slope in this observation. Zero-item pending still includes the terminal false move; it is not a zero-await control.
- **Off/AsTask/pending:** 135.998974116 → 88.227430556 B/item. Candidate retains a real conversion bridge, so the direct-await result cannot be generalized to AsTask.
- **Telemetry-on/pending:** both arms remain near 272 B/item, with the logical and telemetry layers still present. Candidate on/AsTask happens to show a lower 100-item total than baseline, but that small single-observation difference does not establish a fallback optimization.
- **Sync:** candidate is approximately +8 B/call, including +8.609375 in off/AsTask. These are unfavorable retained results, not filtered noise to discard. All cell totals include fixture/harness/runtime work.

## Actual path, backing and continuation ownership

Both allocation and attribution proofs pass actual wrapper identity checks: candidate/off is CompletionOwnedLogicalInvocationAsyncEnumerable; baseline and telemetry-on use the general LogicalInvocationAsyncEnumerable. Real ActivityListener presence determines telemetry-on; this is not the earlier test-runner tracing false-path problem.

Each attribution arm retains 24 full traces, 832 total move samples including each terminal false: 416 pending and 416 synchronous. Every pending trace within a configuration has one consistent backing/continuation type pair across 0/1/100 items, as independently enumerated in verified.json.

| Pending configuration | Baseline public backing / intercepted promise continuation state | Candidate public backing / intercepted promise continuation state |
|---|---|---|
| Off/direct | Logical AwaitMoveNextAsync AsyncStateMachineBox / logical box | Intercepted compiler iterator d__12 / diagnostic AwaitOne consumer box |
| Off/AsTask | Logical AwaitMoveNextAsync AsyncStateMachineBox / logical box | Intercepted compiler iterator d__12 / ValueTaskSourceAsTask |
| On/direct or AsTask | Logical AwaitMoveNextAsync AsyncStateMachineBox / TelemetryAsyncEnumerator box | Logical AwaitMoveNextAsync AsyncStateMachineBox / TelemetryAsyncEnumerator box |

Baseline captured intercepted iterator is d__8; candidate is d__12. The raw records retain stateType, callback targetType and method: the common Task-box callback target is a runtime ThreadPool helper, while **stateType identifies the relevant async owner**. Candidate AsTask has the distinct ValueTaskSourceAsTask callback/state. Thus the result is not inferred merely from a small driver or an absent native body. All synchronous samples have no pending backing/captured continuation, as expected.

The diagnostic AwaitOne Task/reflection is confined to separate attribution processes. It is the consumer used to expose the topology, not a replacement forwarding layer inside the allocation loop. The candidate/direct controlled-pending chain genuinely lacks the logical AwaitMoveNextAsync box between public caller and intercepted iterator.

## Source lifecycle and allocation observation

Per allocation arm, summed sampled counts are Sources=24,576, Disposals=24,576, Subscriptions=425,984 and Results=425,984. Per attribution arm they are Sources=24, Disposals=24, Subscriptions=416 and Results=416. More importantly, the validator checks each individual cell against calls × (items+1) for pending and zero subscriptions/results for synchronous source; all match.

The frozen C# fixture uses reusable ManualResetValueTaskSourceCore plus IThreadPoolWorkItem, no per-item fixture Task. Public move consumption selects direct await or one AsTask conversion; diagnostics inspect backing without separately consuming the ValueTask. Source counters corroborate one source GetResult/subscription per pending move. They do not instrument every internal framework promise consumption.

GC.GetTotalAllocatedBytes(true) covers cross-thread allocation. Measurement windows exclude initial wrapper-type discovery, warmups, explicit pre-window collections, attribution reflection, and report creation. This does not make process-wide totals exclusively product allocations: consumer state machines, reusable-source setup per invocation, scheduling/runtime work and real telemetry remain included symmetrically. The observed slopes are descriptive, not allocation-stack attribution.

## Timing/environment boundary

All retained artifact event timestamps were independently compared to current filesystem mtimes and match the event receipt. Runtime-validated plan publication is **2026-10-28 15:46:31.775287 UTC**, final gate publication **15:46:34.304172 UTC**. Four proof mtimes fall inside that interval. These are artifact event times, not instrumented starts/stops or benchmark duration measurements.

Current scanner verify.py mtime is **15:47:02.682495 UTC**, integrity.json **15:47:08.471686 UTC**, both later than the final gate. This supports the corrected record: overlap by that identified later heavy archive scan is **not evidenced**, rather than proven contamination of the mechanism run. Filesystem mtimes alone are not a complete process-lifetime audit. Earlier read-only inspection could overlap, and a fully acknowledged quiet window was not established.

Scanner privacy.json has since changed: current mtime at this review is **15:50:53.445410 UTC**, not the author's earlier 15:48:19.807312 event. It is outside the mechanism evidence census; the change does not invalidate that census or establish earlier overlap. I retain this distinction rather than treating a mutable scanner artifact as an immutable launch timestamp.

No sample should be removed or rerun on this basis. Scheduling can influence W and single-run allocation totals; it does not erase the separately controlled and actually captured pending ownership graph.

## Decision and limits

**Proceed to an actual end-to-end TCP experiment after its separate preparation/binding/quiet-window authorization.** Unlike a static size argument, this dataset combines a real removed pending ownership layer with materially lower observed pending allocation slope on the intended direct path. That is adequate reason to test a potential benefit, not proof that it survives transport costs or improves throughput/tails.

Required interpretation remains:

1. Allocation-process actual forwarding suspensions W were not measured and need not equal item count. Attribution deliberately blocks completion and cannot supply W for the allocation process.
2. One fresh allocation process per arm is not a randomized repeated performance experiment or confidence interval. No guaranteed exact 136 B/item, executed-continuation count, cross-core-hop count, QPS/P99 gain or 2.0.3 comparison follows.
3. Static intercepted untimed ServerStreaming with synchronous interceptor invocation/disposal is the tested scope. No dynamic/timed/duplex/all-feature result is implied. Prior correctness review is separate.
4. Preserve AsTask bridge, telemetry fallback and synchronous per-call overhead results in any later narrative. End-to-end TCP evidence must establish its actual paths and its own complete fixed dataset; do not pool with the older 607 campaign.
5. This review authorizes no additional sample itself and imposes no native-code-closure expansion. Existing minimal mechanism question is answered well enough to move on, without a replacement run.

## Reproducible offline review artifacts

- `verify.py`: independent census/source/runtime/proof checks and recalculation; Python only.
- `verified.json`: all 24 recalculated rows, eight slope groups, lifecycle sums, exact grouped backing/continuation strings, current event mtimes and identities.
- Original roots: `/workspace/scratch/9e5903e2fbfb/logical-forwarder-mechanism-{prepared,evidence,launch}-01f6aad-attempt1`.
- Author report/census root: `/workspace/scratch/9e5903e2fbfb/logical-forwarder-mechanism-report-01f6aad-attempt1`.
