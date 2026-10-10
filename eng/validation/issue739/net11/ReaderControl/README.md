# Actual reader source-shape controls

Validation-only entry point referencing the real `SharpLink.Runtime` project through the existing `SharpLink.Benchmarks` friend identity. Production source stays pinned to `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`; the net11 runner owns exact SDK/runtime pinning, A/B projection, unchanged consumer reuse and loaded dependency validation. Keep this measurement project traditionally lowered in both variants.

Arguments: `case operations warmup sample output expected-runtime`.

Cases:

- `reader-wrapped-sync`, `reader-direct-sync`
- `reader-wrapped-incomplete`, `reader-direct-incomplete`
- `reader-wrapped-incomplete-burst32`, `reader-direct-incomplete-burst32`

The last pair requires counts divisible by 32. Operations always count individual reads; `bursts` separately counts batches. One reusable source is used for the serial cases and 32 distinct reusable sources for the burst cases. The burst driver starts and verifies all reads before completing any source, and completes all sources before consuming or advancing any result.

All cases use a precreated `ReadResult`, valid buffer and `ManualResetValueTaskSourceCore<ReadResult>`; sync cases set the source result before returning the `ValueTask`. Sources reset only after the preceding result was consumed exactly once and advanced. Wrapped-incomplete cases exercise the actual `ReadOwnershipPipeReader.AwaitReadAsync`; wrapped-sync bypasses that helper. Exact source/driver lifecycle counts and same-thread completion are assertions, not approximate diagnostics. The helper's continuation must complete the outer read inline; unsupported scheduling fails the sample without waits or adaptation. Such a failure is an unsupported microcontrol shape, not a production correctness conclusion.

All object setup, warmup, hash calculation and reporting occur outside the allocation interval. Warmup uses the selected serial/burst shape and retains counters; measured counts are deltas. The raw process-wide precise and current-thread measurements include loop/control overhead with no subtraction. Fixed offsets and paired direct controls remain visible. Source tokens can wrap only after previous lifetimes have finished.

These controls cover warm success-path reader shapes. They do not establish transport/RPC multiplicity, one-connection concurrency32 behavior, cancellation, faults, shutdown, unbounded pool pressure or end-to-end allocation-owner closure. The source is intentionally socket-free. Local SDK10 execution is compile/correctness validation only, never net11 performance evidence.

Standalone correctness checks (six cases, exact counters, serial source token wrap, hash identity and eight invalid-input cases):

```sh
python3 eng/validation/issue739/net11/ReaderControl/test_control.py \
  --dotnet /path/to/dotnet \
  --assembly /path/to/ReaderControl/output/SharpLink.Benchmarks.dll \
  --expected-runtime 11.0.0
```

The test script currently targets Unix hosts, matching the validation workflow.
