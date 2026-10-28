# Scalar stream representation and JIT probe

This standalone probe exercises successful intercepted server streams with 0, 1,
and 100 scalar items, both static and dynamic channels, with no deadline and a
five-minute deadline. The interceptor deliberately short-circuits transport. It is
an allocation/layout/code-generation diagnostic, **not a network throughput test**.

The probe preserves the measured call loop and adds post-measurement runtime and assembly metadata. The probe uses the existing
`SharpLink.Benchmarks` friend-assembly identity to inspect internal representation;
it adds no production API or solution project.

Build both desired revisions first, using the repository-pinned SDK. A convenient
dependency directory is each revision's Release UnitTests output. Then run:

```sh
python3 eng/physical-ownership-probes/scalar/run.py \
  --baseline-libraries /absolute/baseline/test/SharpLink.UnitTests/bin/Release/net10.0 \
  --candidate-libraries /absolute/candidate/test/SharpLink.UnitTests/bin/Release/net10.0 \
  --baseline-revision FULL_BASELINE_COMMIT \
  --candidate-revision FULL_CANDIDATE_COMMIT \
  --output /absolute/new-output-directory
```

The runner refuses an existing output directory. It builds the probe once against
the baseline, uses byte-identical probe binaries for both runtime sets, and copies
only already-present runtime dependencies. It records SDK/runtime information,
source and DLL hashes, and relevant environment values. Every repetition has its
own `run-N` directory; the combined `allocations.json` retains all rows.
`summary.json` reports FullOpts medians, ranges, paired differences and ratios
(`null` when the baseline is zero). It never rebuilds either
production checkout. Revisions are descriptive inputs; verify that the supplied
DLLs were built from those clean revisions before attributing measurements.

Each allocation row warms 2,000 calls and measures 20,000 synchronous successful
calls using thread-local allocation counts. Five independent FullOpts processes
repeat all rows. The JSON also records reflected fields, instance allocation size,
IL bytes, and exception-handler counts. The tiny fractional allocation residue is
fixed harness amortization, not an extra per-item allocation.

JIT listings are collected separately in three same-mode comparisons:

- `fullopts`: tiered compilation disabled.
- `default-tiered`: default tiered compilation and dynamic PGO.
- `tiered-pgo-off`: tiered compilation enabled by default, dynamic PGO disabled.

Each listing identifies its actual Tier0, instrumented Tier0, or Tier1 compilation.
Tier1 PGO layout depends on the observed workload and scheduling; a method not
present in a listing was not observed compiling in that process. Compare matching
modes and method identities, not unmatched aggregate file sizes. Native bytes and
allocation counts do not establish throughput, latency, or performance equivalence.
Use the adjacent real TCP probe for bounded total-path evidence.

## Prepare before the measurement window

Use `--prepare-only` with the existing library/revision arguments to build and
freeze both arms. Then execute without any build:

```sh
python3 eng/physical-ownership-probes/scalar/run.py \
  --prepared /absolute/prepared-directory \
  --output /absolute/new-results-directory \
  --runtime-dotnet /absolute/private-runtime/dotnet \
  --allocation-repeats 5 --affinity 0,1
```

`--dotnet` selects the build SDK; `--runtime-dotnet` selects the execution host
and defaults to `--dotnet`. Select two CPUs from the process's allowed affinity
rather than assuming CPUs 0 and 1 are available. FullOpts repetitions alternate
baseline/candidate order. The [frozen PR931 job](../README.md) coordinates both
probes, pins the frameworks and validates every process's actual runtime.
