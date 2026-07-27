# 0.8.33 regression-test research

## Bounded target inventory

- `GetContractMethods` collapses inherited members by method name/arity/parameter types but ignores return type and route-affecting attributes. Two valid base interfaces with the same parameters and incompatible task payloads therefore emit one Proxy member that cannot implement both declarations instead of a focused diagnostic.
- Stub size-field names replace namespace/type punctuation with underscores. Distinct enum types such as `A.B_C.State` and `A_B.C.State` therefore emit duplicate fields in one generated Stub and fail downstream compilation.
- Client and Server builders synchronously wait on arbitrary user `DisposeAsync` implementations during rollback. If cleanup begins under a non-pumping synchronization context and awaits, the continuation is posted back to the blocked context and Build never returns.
- Client Hosted Service does not reject a second Start before its broad startup catch. A duplicate Start can overwrite `_client`; the accessor then rejects publication and cleanup disposes only the replacement, losing the original owner and poisoning the accessor.
- Multi-Cluster Hosted Service independently has the same ownership bug for its coordinator and therefore requires its own lifecycle fix and regression boundary.

## Engineering boundary

- Reject conflicting inherited route declarations at generation time while continuing to collapse exact compatible redeclarations.
- Make generated type-derived identifiers collision-resistant without changing wire type names, hashes, or ordinary fixed-size code.
- Invoke asynchronous rollback cleanup away from the caller's synchronization context while preserving synchronous Build semantics and complete exception aggregation; this is a cold failure path.
- Reject duplicate Client Hosted Start outside startup cleanup so the existing client owner and accessor remain intact.
- Apply the same once-only boundary independently to Multi-Cluster Hosted Start so its existing coordinator is not transferred into failure cleanup.

## Acceptance checklist

- Incompatible inherited routes report one stable Generator diagnostic and emit no broken contract artifacts.
- Every generated Stub size field has a unique deterministic identifier.
- Builder rollback completes and disposes the resource on a non-pumping synchronization-context thread.
- Duplicate Client Hosted Start leaves the pre-existing client undisposed and does not enter accessor failure cleanup.
- Duplicate Multi-Cluster Hosted Start leaves the pre-existing coordinator undisposed and does not enter accessor failure cleanup.

## Deferred/rejected signals

Extreme DNS jitter was rejected after an executable probe showed current .NET saturates the out-of-range floating-point conversion instead of wrapping negative; no defect exists on the supported runtime. Cross-pool writer return is documented ownership misuse and is not promoted because runtime paths already return to their originating Context and an owner check would tax every packet. A single legacy `object` monitor in the circuit breaker and private-object disposal locks are syntax/clarity cleanup candidates, not P2 evidence and do not advance the version.
