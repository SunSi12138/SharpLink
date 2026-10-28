# #732 controlled three-arm campaign

Status: preparation for independent review. No hosted dispatch or local RPC timing is authorized by this document. After review, the release owner publishes the exact source/tool refs and dispatches **Performance Evidence**, suite `method-facts-732`, on the reviewed tool branch. `repetitions` applies only to the old smoke suite; it never changes this campaign.

## Compare the candidate directly with pre-M

| Arm | Source revision | Meaning |
|---|---|---|
| O | `a6bd0352a931d5c2816e42778055e90efb01144b` | pre-M |
| A | `37548b7a35d50803212d2b0a637306636cc0a1d3` | M0: complete facts, packed scalar representation |
| B | `2756e8810772cf956ee70ed380925ecc86e86a32` | reviewed M1: cached readonly complete descriptor and known/cancellation byte |

Report **A/O** (M0 regression check), **B/A** (representation mechanism), and **B/O** (direct candidate versus pre-M) independently. M0's historical TCP/interceptor/timed short-stream result remains No-Go (mean GM 1.145; P99 GM 1.668). The October 28 local M0/M1 campaign was inconclusive under external build load and large same-arm drift; its apparent 0.42 mean ratio is not optimization evidence. Keep that full local evidence locally, including precise process details; do not upload unrelated local project paths or command lines.

The common harness originates at `e0e0abf2842094b0d51f4269b39ca678485f9fcf`. Its original synchronous service and per-call checksum/tick loop are unchanged. Tool additions are preparation, metadata, Linux monitoring, and three-arm calculations. No production code is changed.

## Freeze everything before measurement

One disposable GitHub-hosted **ubuntu-24.04 x64** job builds all three arms with isolated SDK **10.0.102**, then executes with a separate runtime-only installation containing **Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.0.11**. The console probes consume NETCore; AspNet's installed presence is recorded, not represented as a loaded dependency. Runtimeconfig pins NETCore 10.0.11 with roll-forward disabled. Record the actual host path, loaded CoreLib path/version, loaded product module identities/locations, complete managed DLL inventories, source/tool commits, build commands, configs and OS/image identity.

Select the lowest-numbered allowed logical CPU, then the lowest-numbered allowed CPU with a different `(physical_package_id, core_id)`. Exactly two distinct reported physical cores are required. Record every allowed CPU's package/core/sibling topology, affinity, cgroup quota and cpuset; unknown topology, a single physical core or failed affinity is a preparation failure, not permission to downgrade. Every probe reports its actual allowed CPU set and effective processor count; both must match the frozen two-CPU selection. Hosted topology is the VM-reported topology, not proof of exclusive physical-host cores.

Keep default tiering/PGO/workstation GC. Reject COMPlus or relevant DOTNET JIT/tiering/GC/processor overrides. Record effective metadata and compare each measured process with its frozen describe record. No build, restore, publish or tool installation occurs after the all-builds-complete marker. No production probes or reflection enter the measured service loop.

Publish the timing harness and separate blocking preflight against each arm. Verify equal non-product dependency bytes across arms and identical runtime dependencies between each arm's preflight and timing snapshot. Run the non-timing preflight once per arm: TCP and shared memory, timed and untimed (12 real requests total). Timed requests must have matching active request/state IDs and invocation tokens shared by service/interceptor, distinct from the connection token. Untimed synchronous requests must have no matching state and use the connection token in context. Any failure prevents timing.

Only after all six builds, all describe checks and all preflights succeed, write the manifest and preparation-complete marker. The manifest contains exact paths, commands, order, settings, identities and counts. Keep stdout, stderr, build logs/binlogs and preparation failures.

## Fixed schedule: 18 pairs, 36 fresh processes

First half: **OO OA AB BB BO AA BA AO OB**

Second half: **BO OA AB AA OB BB BA AO OO**

Each contrast has four cross-arm pairs: two in each chronological direction. Each arm has two same-arm pairs. A contrast uses the four same-arm pairs belonging to its two arms, not the third arm's controls.

Every process runs these four TCP, one-item, concurrency-1 cells in order:

1. **I/T:** pass-through interceptor, declared timeout and cancellation.
2. **P/T:** no interceptor, declared timeout and cancellation.
3. **I/U:** pass-through interceptor, untimed/noncancellable.
4. **P/U:** no interceptor, untimed/noncancellable.

No admission controller; telemetry disabled. Per cell: **1,000 warmup calls, then five samples of 2,000 measured completed calls**. Total: **720 samples and 1,440,000 measured calls** (warmup excluded). Preserve every per-call tick, sample elapsed time, frequency, CPU/allocation/GC values, failure and process start/end timestamp. Separate five-second process-name/load/CPU-pressure/cgroup observations characterize residual interference; no silent sample exclusion follows from them.

## Unchanged formulas, applied separately to every contrast

For process j, cell c, sample s, use whole-sample elapsed ticks E, frequency F, and completed calls N:

`M[j,c] = 1e9 × sum_s(E[j,c,s]/F[j,c,s]) / sum_s(N[j,c,s])`.

CPU and allocation are likewise summed over the five measurement windows and divided by 10,000. P99 pools the process/cell's 10,000 per-call durations (each converted using its sample frequency), ascending; take nearest rank 9,900, zero-based index 9,899. Do not average sample P99s, substitute the per-call mean, or pool different processes.

For each contrast X/Y, orient every cross pair as candidate X / baseline Y, regardless of chronological order:

- `L[p,c] = ln(M[X,c]/M[Y,c])`.
- `D[p,T] = L[p,I/T] - L[p,P/T]`; analogously for U.
- Same-arm pairs stay chronological: `S[q,c] = ln(M[second,c]/M[first,c])`.
- `H[q,T] = S[q,I/T] - S[q,P/T]`; analogously for U.
- `noise[T] = max_q(abs(H[q,T]))` over the four same-arm pairs for the contrast; analogously for U.

Report all L/D/S/H, every cross/same pair mean and P99 ratio, all process/cell aggregates, and both envelopes. Mean and P99 geometric ratios are the exponential average log ratio over the four cross pairs. Interaction GM is `exp(sum_p(D[p,T])/4)` and separately U.

For each contrast and T/U separately:

- **Favorable exploratory signal:** all four intercepted L are strictly negative and all four D are strictly less than `-noise`.
- **Adverse exploratory signal:** all four intercepted L are strictly positive and all four D are strictly greater than `noise`.
- **Otherwise inconclusive**, including equality. Favorable GM never overrides an individual-pair failure. Show adverse P99 pairs beside the conclusion.

T is primary; U cannot rescue inconclusive/adverse T. Favorable T without favorable U is specific to the timed shape. An improvement in A/O, B/A or B/O cannot substitute for another comparison or clear the release gate. These descriptive rules are not significance, equivalence or a new tolerance threshold. M0's No-Go, exact-head combined validation with #931 and the historical unexplained BootstrapManifest timeout remain release dependencies.

## Stop, preserve, hand off

A process gets one attempt and a 180-second watchdog. At the first nonzero exit, watchdog, start failure, changed runtime identity or missing/failed/reordered cell/sample, stop; retain stdout/stderr, all partial rows and the execution ledger. No replacement, retry, extension, outlier removal, tuning or adverse-pair rerun. A failed preparation produces no timed process. An incomplete campaign produces no full comparison.

The complete artifact includes frozen common source, preparation manifest, binaries, raw call ticks, environment records, full-precision `analysis.json`, `process-aggregates.csv`, and `calculations.md`. Independent review accepts the tools and fixed design before dispatch; after execution, the release owner interprets all contrasts and residual interference together. No result is predeclared favorable.

## Release-owner execution handoff

Run these commands only after independent approval of the tool commit and plan. They publish experiment refs, not production changes to the release branch. The three source SHAs above are immutable inputs; do not substitute a newer M1 or a combined head.

```sh
# From the controlled-tools worktree; review git log/diff first.
git push origin \
  a6bd0352a931d5c2816e42778055e90efb01144b:refs/heads/codex/732-evidence-pre-m \
  37548b7a35d50803212d2b0a637306636cc0a1d3:refs/heads/codex/732-evidence-m0 \
  2756e8810772cf956ee70ed380925ecc86e86a32:refs/heads/codex/732-evidence-m1 \
  HEAD:refs/heads/codex/732-controlled-performance-tools
# The existing workflow must already be registered on the repository default branch.
gh workflow run performance-evidence.yml \
  --repo SunSi12138/SharpLink --ref codex/732-controlled-performance-tools \
  -f suite=method-facts-732 -f repetitions=1
gh run list --repo SunSi12138/SharpLink --workflow performance-evidence.yml \
  --branch codex/732-controlled-performance-tools --limit 5
# Use the returned ID for this dispatch, not a prior ownership/smoke run.
gh run watch RUN_ID --repo SunSi12138/SharpLink --exit-status
gh run download RUN_ID --repo SunSi12138/SharpLink --dir artifacts/method-facts-hosted-RUN_ID
```

Expected artifact name: `method-facts-732-<reviewed-tool-sha>-<run-attempt>`. Confirm the run used the reviewed tool SHA. A successful campaign has `preparation-complete.json`, `completion.json` with `complete=true`, exactly 36 valid execution rows and `analysis.json` with 1,440,000 completed calls. Read B/O first for the direct candidate comparison, then A/O and B/A for diagnosis; report every T/U decision, all adverse P99 pairs, and residual environmental interference.

If checkout/runtime installation/build/preflight/affinity fails, preserve the workflow logs and uploaded files, diagnose preparation and review the correction before a new dispatch. If any measured process begins and fails or the campaign is inconclusive, preserve the run and stop: a repeat requires a newly authorized experiment, not a workflow rerun. Missing full analysis is an incomplete campaign, not zero regression. No result automatically authorizes production merge or release Go.

## Preparation validation (2026-10-28; no RPC timing)

On macOS arm64, SDK 10.0.102 and runtime 10.0.11, all six source-reference Release publishes succeeded from separate archives of the three exact source refs. All three describe records and all 12 separate blocking-preflight requests passed. Cross-arm non-product dependencies and each arm's preflight/timing dependencies matched byte-for-byte. This validates buildability and semantic preflight, not hosted Linux scheduling or performance.

Ten synthetic tool fixtures pass, covering fixed schedule/orientation, contrast-specific same-arm envelopes, equality/discordant-pair decisions, whole-sample mean and nearest-rank P99, both complete output profiles, CPU/runtime rejection, failed starts/exits/watchdogs and incomplete sample retention. Reanalyzing the retained 640,000-call local campaign produces exactly the existing full-precision JSON; no local process was rerun. The complete original service and `Network` implementation remain text-equal to the frozen harness. `actionlint` and whitespace checks pass.

Local validation logs/binlogs and exact describe/preflight records are retained in ignored `artifacts/method-facts-tools-validation/`; they are not uploaded by this workflow. Linux topology/affinity and private hosted installation are checked by the hosted preparation step before any timed process. No timing, push or dispatch was performed during tool preparation.
