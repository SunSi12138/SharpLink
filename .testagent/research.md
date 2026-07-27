# 0.8.40 regression-test research

## Candidate inventory

- Generated Stubs whose contract has no methods in one invocation category throw the legacy public
  `RpcException`; the equivalent non-empty switch default already returns structured Unimplemented.
- `SharpLinkException` accepts Unknown and undefined enum values even though Protocol v2 refuses to
  serialize them. A custom mapper/interceptor can therefore break error delivery instead of safely
  producing Internal.
- A response-bearing Server interceptor can invoke an incomplete continuation, discard its
  `ValueTask`, and return. The 0.8.39 invoked guard passes while the response owner is still in use.
- A Client interceptor can start an incomplete terminal continuation, discard it, and immediately
  return a short-circuit result. The orphan attempt continues after the logical call completes.
- Generator nullability participates in contract metadata and request validation, but non-nullable
  top-level and stream responses can still serialize or short-circuit null as a successful result.

## Acceptance boundary

- Every unknown method shape returns structured Unimplemented; remove the now-unused public
  `RpcException` abstraction.
- Reject non-wire SharpLink error codes at construction so mapper failures fall through the existing
  safe Internal boundary; all defined concrete codes remain valid.
- If an interceptor invokes `next`, the pipeline must join that continuation before completing even
  when interceptor code discards it. Preserve synchronous fast paths and valid result transformation.
- Enforce generated response nullability for service unary/stream results and Client interceptor
  short circuits; nullable reference responses remain valid and value-type paths do not allocate.

## Planned evidence and pseudo-mutations

- Generate response-only and no-response-only contracts and assert both empty category dispatchers
  use Unimplemented while `RpcException` is absent from the public assembly.
- Exercise Unknown and an undefined code directly and through a custom exception mapper.
- Hold a terminal call behind a deterministic gate while Client and Server interceptors discard
  `next`; prove the outer call currently completes before the terminal.
- Return null for non-nullable/nullable unary and stream contracts, including a Client short circuit.
- A check that only tests `WasInvoked`, only scalar results, or only service responses must leave at
  least one witness failing. Joining must not add work to the zero-interceptor path.

## Preserved pre-fix evidence

- Exact baseline: local 0.8.39 commit `8fffab767ce76c70a6c459148a288a07594e69ea`.
- Generator suite: 118 established tests passed and only
  `EmptyInvocationCategoriesMustUseStructuredUnimplemented` failed because generated source still
  contained `RpcException`.
- Targeted Abstractions suite: 21 established tests passed and only the new public-surface and
  non-wire-code witnesses failed.
- Interceptor Integration class: 14 established tests passed and exactly the four new witnesses
  failed. The pre-fix compilation also emitted CS8613/CS8604 at generated Proxy/Stub nullability
  boundaries.

## Assertion and pseudo-mutation review

- Replacing Unimplemented with Internal or restoring `RpcException` fails independent generated
  source and reflected public-surface assertions.
- Accepting either Unknown or an undefined enum value fails the direct constructor witness; allowing
  the three-argument path also breaks the custom-mapper Internal fallback witness.
- Retaining only `WasInvoked` fails both deterministic gate tests: the outer call must remain pending
  until the discarded continuation completes. The Client test additionally proves the interceptor's
  transformed result is preserved.
- Enforcing only unary or only stream nullability leaves a witness failing; enforcing only generated
  service output leaves the Client short-circuit witness failing. Nullable counterparts prove the
  guard is not unconditional.
- Nullable source display is separated from protocol identity. Generated C# preserves `?`, while
  method IDs, request schemas, manifest wire-type lookup, and existing contract names remain based on
  the prior non-nullable runtime type spelling.
- The first full Unit run exposed one implementation regression: an interceptor that awaited `next`
  caused the same downstream exception to be observed twice and aggregated. Client and Server now
  preserve that exception identity and aggregate only genuinely independent failures; the exact old
  retry test and complete suites pass.
- The first exact-baseline performance probe rejected eager `ValueTask.AsTask()` sharing at
  approximately 1,808 versus 1,584 B/op. Final join state uses a bounded self-node pool, direct
  forwarding remains allocation-free, and descriptor Boolean flags pack response nullability without
  expanding interceptor contexts. Final allocation is approximately 1,560 B/op, descriptor size is
  40 versus the exact baseline's 48 bytes, and latency ranges overlap.
