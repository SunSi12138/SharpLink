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

## GitHub dispatch recipe (template, not an installed workflow)

`performance-evidence.yml` remains smoke-only. Do not pass nonexistent `tier` or `runtime` inputs to it. Add the following small scheduling workflow through an independently reviewed PR before using it. Do not run this cost without the existing resource authorization. Each job uses one dedicated runner; choose a platform-specific plan and runner label. This example schedules Linux only, not a three-OS claim.

1. `workflow_dispatch` on the frozen candidate branch; checkout its exact `github.sha` and use the existing `./.github/actions/setup-dotnet`.
2. A plan job runs the `plan --platform linux` command above; upload the plan and expose `{"include":[{"shard":"<id>"},...]}` from its `shards[].id` as a job output.
3. A matrix job consumes those IDs with `strategy.fail-fast: false`, a resource-approved `max-parallel`, `runs-on: ubuntu-latest`, and `timeout-minutes: 240`. Download the plan, run `run-shard` with `${{ matrix.shard }}`, then run single-shard `validate`. Use a separate runner per shard, not background processes in one job.
4. With `if: always()`, upload `attempt-01/${{ matrix.shard }}` under a unique name containing SHA, shard, run ID and attempt. Upload failures too. Retain enough storage for binary provenance copies and all raw reports.
5. A final job waits for all shard jobs with `if: always()`, downloads each artifact **into its matching shard directory**, then runs full `validate` on the same exact candidate. A job success or a single-shard pass never substitutes for this full aggregate.
6. Keep the final structural check separate from the independent performance decision and the rest of `doc/releasing.md`.

Each hosted job has a six-hour hard limit; 240 minutes leaves headroom for the heaviest planned shard, not a completion guarantee. A timeout is a failed/partial attempt. The five repetitions are kept together per shard; for baseline/candidate comparisons, arrange the separately reviewed alternating same-machine protocol rather than comparing arbitrary hosted runners.

## Offline regression tests

```bash
python3 -m unittest discover -s eng/tests -p 'test_performance_matrix*.py' -v
```

Fixtures are labelled synthetic, are never performance evidence and run no benchmarks. They cover the production shell enumeration, platform totals, shard partition, missing/duplicate cells/reports, wrong source/runtime/configuration, incomplete execution, binary integrity, provenance and sample failure controls.
