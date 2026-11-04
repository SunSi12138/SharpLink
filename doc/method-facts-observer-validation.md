# Five-shape method-facts observer validation

This independent validation slice is based on c07e15c9eb9a574436360c86eda64f2997dc41ac.
It does not change production code, the D2 project, or the cancellation diagnostic.

## Gap and focused coverage

The existing CompleteMethodFactsDispatchTests cover all five kinds for dispatch
and queued replacement/removal. Exception projection and enabled telemetry with
immediate admission previously had only Unary coverage. The additional ten named
cases cover Unary, OneWay, client streaming, server streaming and duplex for:

- Three consecutive initial invocation failures, each mapped exactly once with
  its current request ID, expected business exception and the original facts
  object (ReferenceEquals), plus the complete descriptor value.
- Three consecutive successful initial invocations with telemetry, an interceptor
  and admission enabled. Interceptor and stub receive the original facts object;
  each request has completed telemetry with its original contract/method IDs.
- Cold publication resolves the stub once before requests; all subsequent request
  operations leave that count unchanged. Each completed dispatch releases its
  physical call state and both request permits.

Twenty reads of the cached descriptor verify stable values; they do not perform
or measure twenty projections. Sequential admission-enabled success does not
prove rejection/queue policy. Existing AdmissionRejectionShouldResolveOnceWithoutEnteringBusinessCode
and QueuedContinuationShouldReuseFactsAndOriginalRegistration cover those paths.
These no-op/failing stubs exercise initial invocation orchestration, not stream
pumps, pending enumerators, client-stream data, generated final switches, dynamic
retirement, cancellation or physical asynchronous-stream lifetime.

## Execution boundary

The selected lane reuses the proven c07e15 retirement process owner and result
classifier: ordinary 14-project Unit graph, fresh private source/cache, real JSON
UID discovery, source-span binding, one process per singleton UID, native plus
JUnit outcomes, binary/source immutability and joined-process checks. It retains
the first nine unchanged math/path controls and adds the ten named cases. No
classifier restriction is relaxed. Raw evidence is not uploaded; only bounded
summaries are emitted. The original 47-case catalog and runner remain unchanged.

Local static graph/declaration validation and all 20 existing classifier tests
passed. No local .NET execution is claimed. Hosted results must be read for the
exact new commit, independently of possibly stale run-list status.

## Remaining #732 evidence

The all-kind observer gap is narrower than full once-per-RPC acceptance. Remaining
work includes matched exact-source JIT/NativeAOT full-RPC measurements (ns/op,
CPU/op, allocation and code size), streaming data/termination matrices, cancellation
and deadlines, dynamic/module lifetime and final integrated ordinary-suite and
package/ABI gates. Historical M0 NoGo/M1 Hold and earlier narrow B results are not
acceptance for this cold-table source. No speedup, no-regression, merge or release
is established by this slice.
