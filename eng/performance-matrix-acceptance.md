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

## Offline regression tests

```bash
python3 -m unittest discover -s eng/tests -p 'test_performance_matrix*.py' -v
```

Fixtures are labelled synthetic, are never performance evidence and run no benchmarks. They cover the production shell enumeration, platform totals, shard partition, missing/duplicate cells/reports, wrong source/runtime/configuration, incomplete execution, binary integrity, provenance and sample failure controls.
