# Fixed method-facts causal evidence

The existing local eight-pair M0/M1 profile remains readable and runnable with its original manifests. The new `controlled-three-arm-v1` profile adds a fixed pre-M/M0/M1 comparison on one disposable hosted runner. Read [the frozen controlled plan](CONTROLLED-PLAN.md) before authorizing execution.

## Hosted campaign

After independent review, dispatch `.github/workflows/performance-evidence.yml` on the reviewed tool revision with `suite=method-facts-732`. Frozen product refs must be available remotely. The job performs all builds and untimed checks before its separate timing step; no process or pair is retried.

`prepare_controlled.py` accepts `--pre-m`, `--m0`, `--m1`, `--build-dotnet`, `--runtime-dotnet`, and a new `--output` directory. It prepares only; it never measures RPCs. It requires Linux x64, SDK 10.0.102, private NETCore/AspNet 10.0.11 and two allowed CPUs on distinct reported physical cores.

`run.py <campaign-directory>` executes the frozen manifest once. `analyze.py <campaign-directory>` calculates the full campaign only when all rows and ledger entries are valid. `test_tools.py` uses synthetic data and simulated subprocesses; it makes no RPCs and supplies no performance evidence.

## Existing local profile

A manifest without `profile` keeps the original **AA AB BB BA BA BB AB AA** schedule: 16 processes, 640,000 measured calls, four cells I/T, P/T, I/U, P/U. Its two-arm calculations and JSON structure remain compatible. The additional controlled runtime/affinity/source prerequisites do not apply to that existing route.

The project retains `ArmPath`/`GeneratorPath` assembly-reference builds. New hosted builds use `EvidenceSourceRoot` project references instead. `describe` records metadata only; `network <new samples.jsonl>` is the timed entry point. The latter always uses the fixed 1,000 warmup plus 5 × 2,000 calls per cell.
