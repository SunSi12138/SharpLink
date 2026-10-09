# Server method-facts investigation (#732)

Frozen base: `ca993a1aa89864755e10d1e35030a612b43d6069` (`dev`, includes #796; excludes unmerged #797).

This validation branch does not apply a production change. `candidate.patch` is the smallest
candidate: reuse the immutable descriptor already held by `SharpLinkServerInvocationContext`
for module classification and telemetry. It leaves generated invocation dispatch, cancellation
queries, admission resumes, registration ownership, unknown-method handling and stream error
mapping intact. It adds no fields, generated/public entry points, or method registry.

## Reproduce

With .NET SDK 10.0.112 and Linux NativeAOT prerequisites:

```sh
python3 eng/perf/method-facts/run.py --rounds 7 --iterations 2000000
```

The runner creates isolated baseline/candidate worktrees, copies the same harness into each,
applies the candidate patch only in the candidate, builds JIT and NativeAOT, runs the full
candidate unit suite, captures 80 actual server-dispatch count combinations per arm/runtime,
then alternates baseline/candidate process order for PGO, full-opt JIT and AOT. Finally it
checks all six generated RPC shapes over TCP with plain, intercepted and admission/telemetry
configurations. No result grants merge approval.

## Measurement boundary

- Local timing directly calls the real private `CreateCallContext` and `InvokeServiceAsync`
  through `UnsafeAccessor`; there is no reflection invocation, boxed argument array or hand-written
  copy of production orchestration in the timed loop.
- The generated stub has 16 tiny no-payload Unary methods plus OneWay/cancellation/all stream
  kinds. Local timing rotates the 16 real methods, preserves the final generated invocation
  switch and includes ambient context push/pop and the real pass-through interceptor.
- Context-creation cases include fresh intercepted context allocation. Reused-context local
  cases are supplementary boundary attribution only, not whole-RPC measurements.
- The timed stub has no lookup counters. A separate counting custom stub characterizes actual
  dispatch paths. Its streaming-kind rows measure fact queries only: no typed stream is sent.
  Actual stream reservation, transfer and completion are exercised by generated TCP regressions
  and the existing stream/module tests.
- `SupportsCancellation` is a separate contract query (0 or 1 calls before invocation), not a
  field of `RpcMethodDescriptor`. The custom-stub default and generated implementations remain
  untouched. Do not infer cancellation from kind or response shape.
- `RpcMethodDescriptor` is 32 bytes and its nullable wrapper is 40 bytes on the measured x64
  runtime. This candidate avoids carrying a new wrapper through queue/decode/await state.

## Characterization at the frozen base

D = descriptor lookups; I = 0/1 for no/server interceptor. C is counted separately.

| Successful path | Baseline D | Candidate D |
| --- | ---: | ---: |
| Unary / two-way stream, no admission | 1 + I | 1 |
| Unary / two-way stream, immediate or queued admission | 2 + I | 2 |
| OneWay, no or immediate admission | 2 + I | 2 |
| OneWay, queued admission resume | 3 + I | 3 |

Static/dynamic registration and telemetry listeners do not change these D counts. C = 1 if
wire Cancellable or dynamic module, otherwise 0, on paths reaching invocation setup.

Admission rejection/selector failure uses D=1, C=0 and invokes nothing. Early expired/draining
Unary uses D=0; OneWay first resolves shape (D=1). Unknown OneWay terminates/drains instead of
falling back; unknown two-way preserves the conservative descriptor and the final stub switch's
error. Errors without an invocation context can perform another mapping lookup, unchanged here.
Queued admission revalidates registration; persistent decode retains the exact pre-await
registration snapshot. This candidate changes neither rule.

A broad once-per-RPC descriptor/cancellation cache, packed facts ABI, or registry table is not
implemented. A production decision requires repeatable useful local savings, calibration against
unchanged controls, modest code/layout cost, and no semantic or material regression. Full-RPC
latency improvement is not required and is not assumed from query-count reduction.

## Fixture versions and interpretation

The pre-edge local timing fixture is archived under `local-harness-v1/`. Its four C# source
checksums were independently matched to the portable PDB from the timed binary. Local V1
binary/image hashes and measurements must not be relabelled as V2. The hosted runner uses the
current V2 harness with additional cold edge-characterization entry points; it records its own
hashes, image sizes and timing. Both arms within each experiment use the identical fixture.

The V2 `unknown-method` counting row is specifically a custom stub's missing descriptor plus
conservative fallback (and a controlled generic failure for two-way), not a check of a generated
unknown method's exact wire `Unimplemented` payload. Generated dispatch and exact unknown-method
wire semantics remain covered by the repository protocol/early-rejection tests.
