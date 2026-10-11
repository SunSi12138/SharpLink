# RPC wire identity and call admission

SharpLink 3.0 separates local proxy construction, remote observations, and per-call admission. See [ADR 0002](adr/0002-contract-call-admission.md) for the compatibility and lifetime decisions and [protocol v2](protocol-v2.md) for the wire envelope.

## Local proxy construction

`CreateProxy<TContract>(RpcProxyOptions? options = null)`, `Get<TContract>()`, and `GetWithMetadata<TContract>()` are synchronous, local operations. They validate the registered generated contract and its local module lifetime. They do not connect, wait for manifests, or reject a proxy because a remote endpoint is absent or incompatible. The default `CreateProxy` form is the cached `Get` alias.

Each proxy binds the exact local `ContractId` and `RpcAssemblyHash` from its owning generated manifest. A held proxy is not rebound to a newly registered local codec. The built-in client's unbound `IRpcChannel` invocation entries fail with `FailedPrecondition`: method IDs and independently supplied codecs do not identify a safe contract generation. Generated descriptor layout and channel interface signatures remain unchanged.

## Every call admits on its actual connection

`ContractId` and `MethodId` are compact lookup identifiers; neither proves wire compatibility. Every RPC shape, including OneWay and streaming, requires an exact generated assembly hash match before request serialization or client stream production.

Admission uses only endpoints in the current configured topology and preserves endpoint selection/admission policies. It filters incompatible sessions, reserves a connection, and revalidates the actual reserved session if blue/green refresh redirects the reservation. That same connection is passed to send. A compatible endpoint elsewhere in the pool does not authorize sending through an incompatible one.

`RpcAvailability.FailFast` is the default. An unavailable compatible binding yields a `SharpLinkContractAdmissionException` with scalar readiness observations. `WaitForCompatible` queues the logical call using a bounded, event-driven wait. The client shares a limit of 1,024 unsatisfied call/explicit waiters; overflow is `ResourceExhausted`. Completed or canceled waiters release their quota. No payload is serialized and no stream producer begins while compatibility is awaited.

One original monotonic logical deadline covers interceptors, waiting, retries, and delayed stream enumeration. Waiting does not renew it. Existing request publication checks still reject expiration after serialization/compression. Once admitted, streams remain pinned; later manifests do not migrate them. Retry eligibility is unchanged and non-idempotent calls are not replayed merely because admission or publication failed.

## Readiness is advisory

`GetContractReadiness(requirement)` returns current endpoint/session facts and counts distinct compatible endpoint IDs. Facts distinguish unobserved manifests, absence, incompatibility, compatibility, unavailable endpoints, and stopped targets. Removed topology generations and retired sessions are not routing candidates.

`WaitForContractAsync(requirement, options, cancellationToken)` observes a requested minimum count, optional positive timeout, and lifecycle changes. The change signal is captured before inspecting facts so publication cannot lose a wakeup. A successful observation is not a lease or a substitute for per-call admission. Multi-cluster observations are explicitly scoped to one cluster; proxy calls retain their configured cluster routing.

## Registration receipts and timelines

Register/Replace results expose immutable scalar receipts for callable contracts: server instance, registry epoch, generation, contract ID/hash, and registration ID. A manifest publishes the same timeline and registration identities.

A `ContractRequirement` can require:

- `Compatible`: exact contract/hash, independent of registration identity.
- `MinimumVersion`: exact contract/hash plus the same server instance and epoch, with observed generation at least the receipt generation.
- `ExactRegistration`: minimum-version constraints plus the exact registration ID, distinguishing same-hash replacements.

Generations from different server instances or epochs are never ordered against one another. A receipt from one endpoint is not a cluster-global version. Version-scoped requirements only select sessions matching that timeline.

## Server authority and retirement

Client manifests are observations. The Request carries its versioned identity requirement, and the server binds and validates one immutable registration before service activation or business decoding. This closes Replace/Unregister races after client selection. An incompatible unary/stream call is rejected before the wrong codec or service is entered; an incompatible OneWay closes the session rather than interpreting an unknown business stream shape.

Dynamic lease acquisition remains the final retirement gate. Local module retirement wakes queued, unadmitted calls with `Unavailable`; client drain/stop/fault also terminates waiters. Already admitted operations retain their existing drain semantics. Receipt/snapshot identities and retirement signals retain no collectible contract `Type`, `Assembly`, proxy, or runtime module object.

Protocol minor/floor 5 requires negotiated `ContractAdmission`; earlier peers fail closed during handshake. There is no fuzzy hash match or old request-layout fallback.
