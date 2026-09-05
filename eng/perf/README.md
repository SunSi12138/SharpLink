# Deterministic allocation gate

Issue #251 adds a hard managed-allocation regression gate for a small set of steady-state hot paths. The gate is intentionally not a throughput benchmark: it measures process-wide managed allocation with `GC.GetTotalAllocatedBytes(precise: true)` after warmup and enforces checked-in absolute budgets.

## Covered cases

- `rpc-add-sharedmemory-c1`: tiny unary RPC, one in-flight worker.
- `rpc-add-sharedmemory-c8`: the same RPC with eight concurrent workers on the steady-state client/session path.
- `rpc-oneway-sharedmemory-c1`: tiny OneWay send path.
- `send-pump-idle-wake-balanced`: enqueue -> idle send-pump wake -> force-flush -> drain cycle.

Every case uses at least five independent samples. A sample is normalized only when every requested operation completes successfully. The gate checks both the median B/op and the min/max spread; missing/malformed budgets, runtime-major mismatch, unstable samples, empty filters, non-Release builds, and operation failures all fail closed. JSON is written on both pass and failure.

## Running locally or in CI

```bash
dotnet restore test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj
dotnet build test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release --no-restore
./eng/run-allocation-gate.sh
```

Harness self-tests can be run with:

```bash
dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
  -c Release --no-build -- \
  --allocation-gate-self-test \
  --output artifacts/perf/allocation-gate-self-test.json
```

A negative-control run can inject managed allocation into each measured target operation:

```bash
./eng/run-allocation-gate.sh \
  --filter rpc-add-sharedmemory-c1 \
  --inject-bytes-per-operation 512
```

That command is expected to fail once budgets are calibrated. It exists to prove that the gate is capable of rejecting a concrete per-operation regression rather than only producing evidence.

## Budget maintenance

`allocation-budgets.json` is an explicit contract, not a rolling baseline. Do not automatically rewrite it from CI output. A budget change must be reviewed like a production performance change and should include the gate JSON from the old and new code plus the reason the new ceiling is acceptable.

Use Linux Release builds and the repository-pinned SDK. Warmup, operation counts, concurrency and case definitions live in `AllocationGateRunner`; the JSON contains only the policy thresholds and sample count so a threshold change is obvious in review.

When the repository moves to a new .NET runtime major, the gate deliberately fails because `runtimeMajor` no longer matches. Re-run all cases on the new runtime, inspect at least five repeated CI executions, update the checked-in budgets in a dedicated PR, and retain before/after artifacts in the PR discussion. A runtime migration must not silently inherit the previous runtime's allocation budget.

Throughput and latency remain soft evidence in the existing performance workflows. They are not coupled to these deterministic allocation thresholds.
