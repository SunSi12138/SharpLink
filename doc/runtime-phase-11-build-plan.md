# Runtime Architecture Phase 11: immutable build plans

Phase 11 makes Client and Server construction a cold-path pipeline with one terminal ownership
transfer:

```text
Mutable builder -> Compile immutable plan -> Materialize transaction -> Commit final runtime -> Consumed
```

`Compile` snapshots inputs and fully validates generated-manifest API/protocol, descriptor shape,
and ownership before materialization. It does not create endpoint transport factories, Codec or
adapter scopes, a
`SharpLinkRuntimeContext`, a Server service provider, an admission controller, a listener, or a
connection. `Materialize` registers every framework-owned resource with
`SynchronousBuildTransaction`; `Commit` transfers those resources to the finished Client, Server,
or multi-cluster coordinator.

## Client topology and builder terminal state

`SharpClientBuilder` holds exactly one tagged topology draft and compiles it to exactly one tagged
plan:

| Topology | Plan-owned configuration | Framework-owned resource aggregate |
| --- | --- | --- |
| Fixed transport | pool, retry, heartbeat, protocol and Client options | configured direct transport |
| Static endpoints | frozen endpoint array and frozen attributes | factories created only while materializing the plan |
| Dynamic resolver | cluster, retry and endpoint factory delegate | configured resolver |

`UseTransport`, `UseEndpoint`/`UseEndpoints`, and `UseEndpointResolver` are mutually exclusive.
The second call fails immediately; no `modeCount`, nullable topology matrix, preflight endpoint
cache, or hidden legacy Build path remains. Reconfiguring the same topology kind is also rejected,
so a pending framework-owned transport or resolver never has ambiguous ownership.

Both `SharpClientBuilder` and `SharpLinkServerBuilder` have the state sequence
`Mutable -> Building -> Consumed`. A successful Build, a validation failure, a materialization
failure, a competing Build, and a competing configuration call all leave the builder consumed.
Subsequent Build/configuration calls throw:

```text
This SharpLink builder has already been consumed.
```

Create a new builder for every independently configured Client or Server.

## Snapshot and ownership rules

`ClientBuildPlan`, `ServerBuildPlan`, and `SharpLinkRuntimeContextBuildPlan` contain validated
clones, primitive snapshots, copied/frozen collections, and application-owned references only.
Static endpoint sources and caller-supplied manifest lists are copied once during Compile. A later
mutation of the source list, endpoint attributes, pool/options object, admission rule, interceptor
collection, or manifest list cannot change an in-flight plan or final runtime.

| Resource | Created/owned before Commit | Failure handling | Success owner |
| --- | --- | --- |
| Direct Client transport / dynamic resolver | configuration becomes framework ownership | transaction or unbuilt-plan rollback disposes once | `SharpLinkClient` |
| Static endpoint transport factory | created in Materialize only | transaction rollback disposes in reverse creation order | `SharpLinkClient` |
| Runtime Context | created in Materialize only | transaction rollback disposes it | Client or Server |
| Server listener | configuration becomes framework ownership | transaction or unbuilt-plan rollback disposes once | `SharpLinkServer` |
| Framework Server provider, admission controller, registrations | created in Materialize only | transaction rollback disposes in reverse registration order | `SharpLinkServer` |
| Logger factory, caller provider, explicit codec, caller service | application-owned | tracked without a cleanup action | application |

`SharpLinkMultiClusterClientBuilder` compiles each child exactly once, reads
`ClientBuildPlan.MaximumConnections` from that same plan for its budget check, then materializes
that exact plan. It does not re-enumerate a child endpoint source through a budget preflight.

## Migration

Builder reuse is intentionally breaking. Replace code that changes a builder after `Build()` or
tries another `Build()` with a fresh `SharpClientBuilder` or `SharpLinkServerBuilder`. Likewise,
choose one client topology before configuration; do not rely on later topology calls replacing an
earlier transport, endpoint collection, or resolver.

## Focused evidence

- `BuildPlanBuilderTests` covers all six cross-topology orders, same-kind rejection, successful and
  failed consumed states, deterministic Build/configuration races, single-pass endpoints, failed
  enumeration, post-Compile endpoint/manifest mutation, and admission deep snapshots.
- `StaticEndpointBuilderTests` proves Compile validation does not acquire endpoint factories while
  real Materialize failures still roll back transaction-owned factories and Runtime Contexts.
- `BuilderOwnershipRollbackTests` proves Client/Server transaction ordering, caller ownership, and
  Server Compile validation without Runtime Context materialization.
- `SharpLinkMultiClusterClientTests` verifies child plan budget/materialization reuse, including a
  one-shot endpoint collection.

There is no Phase 11 performance benchmark: plans are construction-only and are not retained by
the RPC, frame, session, stream, or endpoint-selection hot paths. Release, AOT, Chaos, and full
integration gates remain scheduled by the coordinating agent on the serialized remote environment.
