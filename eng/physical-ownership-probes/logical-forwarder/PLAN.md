# Logical-forwarder paired full-product diagnostic LF1

## Frozen source and scope

Baseline `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c` versus published candidate `484a5d279a84a3cd9e717c1ec94d3a5a415112df`. Candidate full tree `5c0060b2fbd7ca41deb78022170148992d32eea6` equals independently reviewed local `d17e1ac691325b8948ca8663ba2d2b836c3ac2a0`; Git API publication creates a genuinely new commit identity. It contains exactly the two product files and two correctness test files changed from ac17. All product assemblies are freshly built/packed at their actual source SHA; no prior DLL is relabelled.

Published source branch: `codex/logical-forwarder-d17-source-backup`. Local git fetch independently confirmed the remote tree. Product correctness and the fixed four-process/24-cell mechanism experiment passed independent review; neither is a TCP performance result. This diagnostic isolates d17 from ac17, **not official 2.0.3**, and cannot establish the user's final +10% QPS/−20% P99 goal.

## Exact reuse / narrow changes

Base tool: `90d98e3cb84ce36425b99c88c8112632194834b5`, tree `def0a220aa9b5f58e8f5e600b5322460bd45eaff` (same tree as reviewed local748ea99). No existing file or #963 campaign is modified. New files provide:
- `paired_logical_forwarder.py`: original full-product driver with separate source pins/imports/output semantics, new cold-probe path, and existing `--keep-going` option to attempt the declared TCP schedule once even after failures. No replacement samples.
- `logical_forwarder_common.py` / `logical_forwarder_validation.py`: original preparation/identity and raw/tick validation with new pins and added actual logical-path validation. Original statistical summarizer unchanged.
- `tcp/logical-forwarder-probe`: original TCP source with only a cold assertion/record block after its existing first-call sample, before warmup, plus serialization of that record. Operation, warmup, measurement, timer, sampling and statistics loops are byte-identical; an offline test checks the meaningful C# slices. No added per-call/item observer/counter.
- One standard Ubuntu24.04 hosted job, bounded push/REQUEST trigger, pinned actions and same SDK/runtime installation scheme. No scheduling platform, extra PR or change to #963.

The original `tcp/prepare.py`, `tcp/module`, `tcp/run_matrix.py`, `tcp/summarize.py`, `client_ablation.py`, and complete `phase-scalar` directory remain byte-identical. Scalar is the original **synchronous short-circuit** layout/allocation/JIT diagnostic, not the new pending mechanism probe; no pending-allocation claim is derived from it.

## Predeclared experiment

- SDK10.0.102; private runtime-only Core+ASP.NET10.0.14, exact rollForward disabled; official runtime archive SHA512 checked before extraction. SDK installed by the same pinned setup-dotnet action/version as prior tool, exact version checked.
- TCP: static-intercepted / dynamic-intercepted ×1/100 items ×6 paired rounds =24 pairs /48 fresh processes. Existing scene rotation and alternating AB/BA order; each sample3s warmup +6s measure; one worker, pool min/max1, same process contains loopback TCP server/client; default JIT tiering/PGO/ReadyToRun. No request timeout; heartbeat interval3600s. Ordered raw ticks, load/CPU observations, all four cells and unfavorable/failing samples retained.
- Scalar: original10 FullOpts allocation processes (five/arm) +4 tiered diagnostic processes. Its existing2000 warmup/20000 measured synchronous invocations per case remain unchanged. Scalars run only after TCP passes input integrity; if preparation/TCP fails, missing later outputs remain visible failures, not silently replaced.
- First two allowed logical CPUs, same fixed affinity for every arm/process, selected before any timing. Record complete available affinity, lscpu CPU/core/socket mapping and runner image. Do not assume two physical cores, SMT separation or dedicated host; no changing CPUs because results are unfavorable.
- Single hosted job, no intentional parallel local CLR/build work. All build/pack/preparation and build-server shutdown occur before the measurement marker. Shared-host workload is not controlled; raw per-process `/proc/stat` and loadavg retained, no outcome/load-based exclusions or automatic retries. No cross-host/sample pooling with earlier607 or R1 campaigns.
- Existing summarizer remains exact: all six paired ratios, ratio/median/ranges, descriptive20000-resample bootstrap with seed529. Not an equivalence test or host-noise correction. No newly fitted favorable threshold, no favorable-cell selection. QPS, P50/P99/P99.9, allocation/CPU and all failure evidence reported.

## Actual path, not static inference

Before warmup, inspect the actual enumerator graph reached after the first item. Record `logicalPathProof` containing ClientActivitySource.HasListeners, actual internal ClientCallsEnabled, telemetry-wrapper presence, CompletionOwned/general logical owner presence and `observedBeforeWarmup`.

Fail if telemetry is enabled, the actual logical owner is ambiguous/missing, baseline exposes CompletionOwned, or static candidate fails to reach CompletionOwned. Dynamic candidate may expose either observed logical owner; preserve its graph/proof rather than assume that static optimization applies. Source inspection predicts inner completion ownership beneath the dynamic wrapper, but only actual process evidence can establish it. Existing channel/deferred-stream/interceptor checks remain required. Post-window raw validation independently recomputes owner flags from wrapper types; path observations are emitted separately. No live observer is inserted into the timed invocation loop.

Telemetry-on is intentionally outside this TCP matrix: mechanism evidence shows fallback and no removed forwarding layer there. Direct await is the existing TCP consumer; AsTask conversion is outside this matrix and can restore a bridge. Do not generalize this workload to all public RPC kinds/configurations.

## Review and bounded trigger

`REQUEST.json` in this tool revision is dormant (`review-required`). **No measurement is authorized merely by publishing this tool.** Independent review must name the exact tool tree/commit and the final authorized REQUEST payload before the request push.

After that review, a single child commit on `codex/logical-forwarder-performance-validation` changes only REQUEST.json and has exact message `run: logical-forwarder paired diagnostic LF1`. Payload keys:
```
{
  "state": "authorized",
  "campaign": "logical-forwarder-d17-48tcp-v1",
  "reviewedToolCommit": "<exact published tool parent SHA>",
  "reviewedToolTree": "<exact reviewed full tree SHA>",
  "baseline": "ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c",
  "candidate": "484a5d279a84a3cd9e717c1ec94d3a5a415112df",
  "candidateTree": "5c0060b2fbd7ca41deb78022170148992d32eea6"
}
```
Workflow is restricted to that branch and REQUEST path, exact commit message, first run attempt only. Guard checks repository/ref/event, clean request checkout, single parent, parent SHA/tree, only-REQUEST diff, fixed sources/campaign. No workflow_dispatch, rerun, force push, merge, release or PR creation is part of this plan. Future new experiments require distinct review/request; this is a scoped request guard, not an access-control boundary against a maintainer intentionally editing the workflow.

After Configure isolated paths creates the artifact directory, collected build failures and incomplete results upload with run SHA/id/attempt. Checkout or initial request-guard failures occur before that directory exists: only GitHub step logs are guaranteed for those early failures, and the always-upload step may report no files. Driver creates fresh output; no reuse of old campaign directories. First C# build and actual experiment have not run at tool submission. If a tool bug appears, preserve failure and get a new exact tool review before any replacement campaign.

## Offline checks

```
python3 -m unittest discover -s eng/physical-ownership-probes/logical-forwarder -v
python3 -m py_compile eng/physical-ownership-probes/{paired_logical_forwarder,logical_forwarder_common,logical_forwarder_validation}.py eng/physical-ownership-probes/logical-forwarder/*.py
git diff --check
```
