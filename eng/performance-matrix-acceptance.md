# Full performance matrix: plan, run isolated shards, validate completeness

This tool verifies full-matrix **structure, source/binary provenance and correctness**. Its output always says `performanceDecision: not-assessed`. Version equivalence, tail-latency regressions, allocation budgets, recorder perturbation and the #732/#931/#933 experiments retain their independent acceptance decisions.

## Plan without running workloads

Use a clean checkout of the final candidate, with this tool committed into it:

```bash
python3 eng/performance-matrix.py plan --platform linux \
  --output artifacts/full-matrix/plan.json
```

`--platform` is `linux`, `macos` or `windows` (default: current host). This executes the **existing** `run-performance-matrix.sh` in `SHARPLINK_MATRIX_PLAN_ONLY=1` mode. The shell enumerates the same loops/arguments but never calls dotnet, creates output directories or publishes/runs binaries. Stream `all` expansion and report/recorder version are read from their C# source; changed enum/argument shapes fail closed pending review.

Current Linux/macOS full plan: **135 shards, 2,835 reports, 19,170 cells, 133.125 hours of warmup + measurement**. Windows excludes UDS: 108 shards, 2,205 reports, 14,910 cells, 103.542 hours. Build, AOT compilation, process startup, drain and failures are additional. Each shard retains five repetitions, full payload/concurrency defaults and both pools except AnonymousPipe's single pool. Shards are transport × profile × runtime × workload; AOT has no oneway-backpressure shard. The longest timing-only shard is 129.17 minutes.

Plans contain every report filename, exact invocation arguments, `(operation, concurrency)` result cell, shard command and counts. Validation regenerates the plan from the runner and rejects shortened/edited plans. A single JSON file can contain many cells; counting files alone is insufficient.

## Run one shard on an otherwise idle, matching platform

```bash
python3 eng/performance-matrix.py run-shard \
  --plan artifacts/full-matrix/plan.json \
  --evidence artifacts/full-matrix/attempt-01 \
  --shard tcp-balanced-jit-unary
```

This command **does run real load**. Do not run it concurrently with other workloads on the same machine. It refuses a reused shard directory and requires clean, exact source and matching host platform. Inherited matrix overrides are removed; only the saved full plan supplies parameters. If a shard fails, retain its directory. Diagnose first; a justified new attempt uses a new root, never overwrites the old evidence.

The wrapper saves `runner.log` and `execution.json`, including actual exit status and source identity/cleanliness after execution. The shell's optional `SHARPLINK_MATRIX_PROVENANCE` hook captures `provenance.json` immediately after build/publish and before first execution of each runtime/project. It records actual SDK/runtime inventory, CPU, OS/architecture, input environment, original binary paths, and copies the executed entry binary (plus SharpLink DLLs for JIT) into `binaries/` with integrity identities. No identities are inferred from a report's assembly version. Do not hand-author sidecars for real runs.

For NativeAOT, the binary identity and planned runtime kind are retained. The report's actual `.NET` runtime version must also appear in the captured installed toolchain inventory. If a cross-published/bundled runtime differs from that inventory, validation stops: inspect and arrange a matching toolchain before a new experiment rather than relabeling the old run. This is a supported-evidence constraint, not a performance conclusion.

Direct legacy invocations of `run-performance-matrix.sh` remain unchanged unless the optional planning/provenance variables are supplied. They do not gain an execution receipt automatically; use `run-shard` for this acceptance tool.

## Validate a shard, then the complete assembled attempt

```bash
python3 eng/performance-matrix.py validate \
  --plan artifacts/full-matrix/plan.json --evidence artifacts/full-matrix/attempt-01 \
  --shard tcp-balanced-jit-unary --output artifacts/full-matrix/shard-check.json

python3 eng/performance-matrix.py validate \
  --plan artifacts/full-matrix/plan.json --evidence artifacts/full-matrix/attempt-01 \
  --output artifacts/full-matrix/full-check.json
```

The first result is explicitly `scope: single-shard`. Only the second checks the complete platform matrix. Output report paths must be new and outside shard directories. Downloaded artifacts must retain this layout:

```text
attempt-01/
  tcp-balanced-jit-unary/
    execution.json
    provenance.json
    runner.log
    binaries/...
    jit-r1-tcp-balanced-p1-1-unary-0.json
    ...
  ... every other planned shard ...
```

Validation rejects missing/extra report files, missing/duplicate/extra result cells, truncated attempts, process failures, source mismatch, different parameters, wrong JIT/AOT binary type, missing environment metadata, modified archived binaries, sample loss, zero/invalid timing, missing/nonfinite percentiles and nonzero failures/cancellations. Its numerical correctness checks follow `PerformanceReportValidator` and additionally bind exact planned configuration/cells and provenance. It does not change the original recorder validator (27 matrix + 10 stream reports).

**Raw oneway-backpressure is not a failure waiver.** The report may expose saturation. Any nonzero `Failure` remains a failed correctness result under the existing strict validator semantics, even if coverage is complete. Keep the raw report and diagnose/classify the failures; this tool does not invent an allowed-error threshold or silently subtract them. OneWay measures local acceptance, not receiver business completion.

## Manual GitHub scheduler (structural evidence only)

`full-performance-matrix.yml` wires the existing `plan/run-shard/validate` contract;
`performance-evidence.yml` remains smoke-only. The new scheduler has **only** a
manual `workflow_dispatch` trigger: publishing this workflow or opening a PR does
not start 378 workload jobs. Have an independent reviewer approve the exact tool
commit before using it. No execution is implied by installing or testing it.

Inputs:

| Input | Contract |
|---|---|
| `candidate_sha` | Required complete lowercase 40-character frozen source commit. |
| `baseline_sha` | Optional complete frozen baseline commit, resolved and recorded **only**; never run or compared here. |
| `mode` | `plan-only` (default), `selected-shards`, or `full-structural`. |
| `shards` | Selected mode only: 1–6 comma-separated `platform:shard` IDs from the full plan. No wildcard, duplicate or unknown ID. |
| `resources_confirmed` | Default false. Both execution modes require true after resource authorization/preflight. This input is not permission to purchase resources. |
| `max_parallel` | 1 (default) through 4 **per platform**; combined Linux/macOS/Windows concurrency can be three times this. |

The workflow/tools are checked out at the dispatch's exact `github.sha`, also
checked against `github.workflow_sha`, separately from the candidate. The scheduler
records both SHAs. The candidate's `eng/performance-matrix.py` and
`eng/run-performance-matrix.sh` must byte-match the reviewed tool checkout: the
existing runner invokes its own capture hook. An older or changed contract stops
before load; do not overlay files, dirty the candidate or relabel its SHA to make
it pass. Review a compatible tool revision instead. Candidate source constants
and the complete runner plan are regenerated and checked at planning, execution
and aggregation. On the isolated Windows runner, Git `core.autocrlf=false` and
`core.eol=lf` are configured **before both checkouts**. This preserves the
committed shell runner's LF bytes; a clean Git status alone would not detect
checkout-generated CRLF, which Bash cannot execute. No candidate files are
rewritten after checkout. Baseline omission is allowed only because this is explicitly a
single-candidate **structural** campaign, not a comparative acceptance mode.

Example after publishing the reviewed workflow on a frozen branch (commands are
instructions, not evidence that a dispatch was performed):

```bash
# No dotnet setup, build or workload in this default planning run.
gh workflow run full-performance-matrix.yml --ref "$FROZEN_TOOL_BRANCH" \
  -f candidate_sha="$CANDIDATE"

# Only after reviewing the plan and confirming the required resources:
gh workflow run full-performance-matrix.yml --ref "$FROZEN_TOOL_BRANCH" \
  -f candidate_sha="$CANDIDATE" -f mode=selected-shards \
  -f shards=linux:tcp-balanced-jit-unary -f resources_confirmed=true

# Full structural coverage, not a performance Go. 378 separate workload jobs.
gh workflow run full-performance-matrix.yml --ref "$FROZEN_TOOL_BRANCH" \
  -f candidate_sha="$CANDIDATE" -f baseline_sha="$BASELINE" \
  -f mode=full-structural -f resources_confirmed=true -f max_parallel=1
```

A connector without workflow dispatch cannot execute these commands by inventing
inputs on another workflow. Use an authorized supported dispatch route and verify
its actual run ID and `headSha` against the reviewed tool commit. This scheduler
adds no PR/push/tag trigger and no release/publish step.

### Isolation, limits and resource preflight

Linux, macOS and Windows use **three independent matrices: 135 / 135 / 108**,
respectively, rather than one matrix exceeding the 256-job matrix limit. Every
cell is a separate standard hosted VM job; no shard is backgrounded alongside
another workload. `fail-fast: false` preserves other cells when one fails. Each
job keeps all five unchanged repetitions, full payloads/concurrencies, warmup,
duration and original strict correctness rules. The fixed job timeout is 240
minutes, below the hosted six-hour limit; build/AOT/drain overhead can still cause
a legitimate failure. Never shorten the plan to fit a timeout.

Plan-only creates all three full plans but schedules zero workload jobs. A
selected run is a bounded operational preflight, **not full acceptance**. The
full timing floor is 369.792 runner-hours (133.125 + 133.125 + 103.542), before
build/publish/startup/drain. Standard public-repository runners do not require paid
runner minutes, but this does **not** establish available artifact storage,
concurrency, queue capacity, bandwidth, or aggregate runner disk space. Check
those against the actual account and measured selected-shard artifact sizes
before full execution. The aggregate currently downloads all retained shards to
one standard Linux runner; capacity must cover both download/extraction and
assembled evidence. If it cannot fit, stop and review a bounded/streaming
aggregation change rather than discard binaries or raw reports. No paid/large
runner or quota/security setting is selected by this workflow. The concurrency
input does not grant authority to change account limits.

Reference: [GitHub Actions limits](https://docs.github.com/en/actions/reference/limits).
Hosted availability and actual SDK may vary; the existing provenance hook records
the actual SDK/runtime, CPU, OS/architecture and executed DLL/AOT bytes, not the
requested SDK as if it were measured. Runners are isolated at the job level, not
claimed to be physically exclusive or noise-free machines.

### Immutable attempts and failure retention

Artifacts are named with run ID and attempt; shard names additionally include
candidate SHA, OS and shard ID. Every upload uses `if: always()` and never
`overwrite: true`. Shard artifacts retain this disjoint layout, so merging their
extracted paths does not flatten or overwrite different OS/shard results:

```text
output/
  evidence/<platform>/<shard>/execution.json, provenance.json, runner.log, binaries/..., reports...
  jobs/<platform>/<shard>.start.json
  jobs/<platform>/<shard>.finish.json
  checks/<platform>/<shard>.json
```

The start receipt is created before SDK setup; setup failure therefore remains
an incomplete job. Workload failure still invokes single-shard validation and
writes a failure receipt. A timeout/cancellation may prevent the finish receipt
or even an upload; missing evidence always fails aggregation, never counts as
zero failures. Actions logs are additional evidence for failures occurring
before a start receipt. Uploads are best effort under hard cancellation, not a
guarantee that a killed runner can finish uploading.

The aggregate runs even after a shard failure, downloads **only the current run
attempt**, checks every selected receipt's tool/source/run/attempt identity, and
revalidates the raw reports and archived binaries. It does not trust a green job
or the saved per-shard check. Full mode invokes the original full validator for
all three platforms; selected mode invokes only the selected-shard checks and
always sets `fullThreePlatformCoverageAndCorrectnessPassed: false`. All modes
retain `performanceDecision: not-assessed`. A plan-only run has no execution
summary at all. Successful planning is not structural or performance acceptance.

Do not use “rerun failed jobs” to assemble a release campaign: its new attempt
number intentionally cannot borrow the previous attempt's plan/shards. Keep the
failed run, diagnose, then use a justified new dispatch (or rerun **all** jobs)
with a new attempt identity. No previous failures are deleted or silently mixed
into a claimed full attempt. Artifact retention is 30 days; archive the entire
accepted and failed evidence before expiry through the separately authorized
evidence process. There is no auto-retry, artifact deletion or archival push in
this workflow.

### Comparative performance remains a separate gate

The existing runner executes five repetitions of **one source**. Baseline and
candidate running on different hosted jobs cannot provide a paired equivalence
verdict. Even serial baseline-then-candidate shards would not implement a
predeclared alternating protocol, and two longest shards already exceed this
workflow's 240-minute budget before build overhead. This scheduler deliberately
does not implement or claim that protocol. Before comparative release acceptance,
freeze baseline/candidate, exact toolchain/environment, alternating same-runner
order, statistical method and the **original** thresholds in a separately
reviewed campaign. Do not invent thresholds, treat nonsignificance as equivalence,
claim any ratio target met, or replace the original recorder/allocation and
#732/#931/#933 gates with a structural pass.

## Offline regression tests

```bash
python3 -m unittest discover -s eng/tests -p 'test_performance_matrix*.py' -v
```

Fixtures are labelled synthetic, are never performance evidence and run no benchmarks. They cover the production shell enumeration, platform totals, shard partition, missing/duplicate cells/reports, wrong source/runtime/configuration, incomplete execution, binary integrity, provenance and sample failure controls.
